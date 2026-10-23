using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamCleaner.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StreamCleaner.Services;

/// <summary>Orchestrates a single job: validate, probe, select, run FFmpeg, verify, commit.</summary>
/// <remarks>
/// Commit model: all files are prepared first (FFmpeg to temp + verify + stage). If any file fails
/// preparation, nothing is committed and the source files are left untouched. If all prepare
/// successfully, all files are committed together (hardlink backup + atomic rename). On full success,
/// backups are deleted and the library is refreshed. This gives all-or-nothing semantics for a batch.
/// Processing is non-recursive: only files directly inside the selected directory are considered.
/// Per-file state is published to the UI as soon as probe+plan completes, so the operator sees the
/// plan for each file while FFmpeg is running. The report updates when preparation completes and
/// again when the batch commits.
/// </remarks>
public sealed class StreamCleanerService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".mov", ".ts", ".m2ts", ".avi", ".webm"
    };

    private const int ProbeTimeoutSeconds = 60;
    private const int FfmpegTimeoutHours = 2;
    private const int ProbePreviewFileLimit = 20;

    private readonly JobStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IDirectoryService _directoryService;
    private readonly ISessionManager _sessionManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<StreamCleanerService> _logger;

    public StreamCleanerService(
        JobStore store,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IDirectoryService directoryService,
        ISessionManager sessionManager,
        IMediaEncoder mediaEncoder,
        ILogger<StreamCleanerService> logger)
    {
        _store = store;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _directoryService = directoryService;
        _sessionManager = sessionManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>Reads persisted job state. Never mutates it.</summary>
    public Task<JobState?> GetStateAsync(CancellationToken ct) => _store.LoadAsync(ct);

    /// <summary>Persists a new pending job. Rejects if a non-terminal job exists.</summary>
    public async Task<JobState> SubmitAsync(JobRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.InputPath))
            throw new ArgumentException("InputPath is required.", nameof(request));
        if (request.Selection is null)
            throw new ArgumentException("Selection is required.", nameof(request));

        var existing = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (existing is not null && existing.Status is JobStatus.Pending or JobStatus.Running)
            throw new InvalidOperationException("A job is already pending or running.");

        var state = new JobState
        {
            Id = Guid.NewGuid(),
            Request = request,
            Status = JobStatus.Pending,
            RequestedAt = DateTime.UtcNow
        };
        await _store.SaveAsync(state, ct).ConfigureAwait(false);
        return state;
    }

    /// <summary>Preview probe: enumerate files and probe the first few without touching anything.</summary>
    public async Task<ProbeResult> ProbePathAsync(string inputPath, CancellationToken ct)
    {
        var result = new ProbeResult();
        try
        {
            var validator = CreateValidator();
            var probe = new FfprobeService(GetFfprobePath(), TimeSpan.FromSeconds(ProbeTimeoutSeconds));

            if (Directory.Exists(inputPath))
            {
                var dir = validator.ValidateExistingDirectory(inputPath);
                result.IsDirectory = true;
                var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .Select(f => validator.ValidateExistingFile(f))
                    .ToList();
                result.FileCount = files.Count;
                foreach (var f in files.Take(ProbePreviewFileLimit))
                    result.Files.Add(await ProbeFileAsync(f, probe, ct).ConfigureAwait(false));
                result.ProbedCount = result.Files.Count;
            }
            else if (File.Exists(inputPath))
            {
                var file = validator.ValidateExistingFile(inputPath);
                result.FileCount = 1;
                result.Files.Add(await ProbeFileAsync(file, probe, ct).ConfigureAwait(false));
                result.ProbedCount = 1;
            }
            else
            {
                result.Error = "Path does not exist.";
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    /// <summary>Executes the currently pending job. Returns the terminal state.</summary>
    public async Task<JobState> ExecutePendingAsync(IProgress<double> progress, CancellationToken ct)
    {
        var state = await _store.LoadAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No pending job to execute.");
        if (state.Status != JobStatus.Pending)
            throw new InvalidOperationException($"Job state is {state.Status}, expected Pending.");

        state.Status = JobStatus.Running;
        state.StartedAt = DateTime.UtcNow;
        await _store.SaveAsync(state, ct).ConfigureAwait(false);

        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin instance unavailable.");

        var preparedFiles = new List<PreparedFile>();

        try
        {
            var validator = CreateValidator();
            var probe = new FfprobeService(GetFfprobePath(), TimeSpan.FromSeconds(ProbeTimeoutSeconds));
            var selector = new StreamSelectionService();
            var ffmpeg = new FfmpegService(GetFfmpegPath(), TimeSpan.FromHours(FfmpegTimeoutHours));

            var tempDir = string.IsNullOrWhiteSpace(config.TemporaryDirectory)
                ? Path.GetTempPath()
                : config.TemporaryDirectory;
            EnsureDirWritable(tempDir);

            var files = DiscoverFiles(state.Request, validator);
            state.TotalFiles = files.Count;
            await _store.SaveAsync(state, ct).ConfigureAwait(false);

            // ---------- Phase 1: Prepare all ----------
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(0.7 * (double)i / Math.Max(files.Count, 1));

                var prep = await PrepareFileAsync(
                    files[i], state.Request, probe, selector, ffmpeg, tempDir,
                    async r =>
                    {
                        if (!state.Results.Contains(r))
                            state.Results.Add(r);
                        await _store.SaveAsync(state, ct).ConfigureAwait(false);
                    },
                    ct).ConfigureAwait(false);

                state.ProcessedFiles = i + 1;
                await _store.SaveAsync(state, ct).ConfigureAwait(false);

                if (prep.Prepared is not null)
                    preparedFiles.Add(prep.Prepared);
            }

            // ---------- Phase 2: Commit or report ----------
            progress.Report(0.75);
            await FinalizeAsync(state, preparedFiles, ct).ConfigureAwait(false);

            var anyTerminalFailure = state.Results.Any(
                r => r.Outcome is FileOutcome.Failed or FileOutcome.Cancelled);
            var anyStuck = state.Results.Any(r => r.Outcome == FileOutcome.Running);
            if (anyStuck)
            {
                _logger.LogError("Job {JobId}: some results were not finalized after commit.", state.Id);
                state.Status = JobStatus.Failed;
                state.ErrorMessage = "Internal error: some files were not finalized.";
            }
            else
            {
                state.Status = ct.IsCancellationRequested
                    ? JobStatus.Cancelled
                    : (anyTerminalFailure ? JobStatus.Failed : JobStatus.Succeeded);
            }
        }
        catch (OperationCanceledException)
        {
            state.Status = JobStatus.Cancelled;
            state.ErrorMessage = "Cancelled by user.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed.", state.Id);
            state.Status = JobStatus.Failed;
            state.ErrorMessage = ex.Message;
        }
        finally
        {
            foreach (var pf in preparedFiles)
            {
                TryDelete(pf.TempPath);
                TryDelete(pf.StagedPath);
            }

            state.FinishedAt = DateTime.UtcNow;
            progress.Report(1.0);
            await _store.SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
        }
        return state;
    }

    private async Task FinalizeAsync(
        JobState state, List<PreparedFile> preparedFiles, CancellationToken ct)
    {
        if (preparedFiles.Count == 0)
            return;

        if (ct.IsCancellationRequested)
        {
            foreach (var pf in preparedFiles)
            {
                pf.Report.Outcome = FileOutcome.Cancelled;
                pf.Report.Reason = "Cancelled before commit.";
            }
            return;
        }

        if (state.Request.DryRun)
            return;

        var anyFailed = state.Results.Any(r => r.Outcome == FileOutcome.Failed);
        if (anyFailed)
        {
            foreach (var pf in preparedFiles)
            {
                pf.Report.Outcome = FileOutcome.Failed;
                pf.Report.Reason = "Batch aborted: another file in the job failed.";
            }
            return;
        }

        try
        {
            await CommitAllAsync(preparedFiles, ct).ConfigureAwait(false);

            foreach (var pf in preparedFiles)
            {
                pf.Report.Outcome = FileOutcome.Succeeded;
                pf.Report.Reason = null;
            }

            foreach (var pf in preparedFiles)
            {
                if (pf.BackupPath is not null)
                {
                    TryDelete(pf.BackupPath);
                    pf.BackupPath = null;
                }
            }

            foreach (var pf in preparedFiles)
                RefreshLibrary(pf.SourcePath);
        }
        catch (OperationCanceledException)
        {
            foreach (var pf in preparedFiles)
            {
                pf.Report.Outcome = FileOutcome.Cancelled;
                pf.Report.Reason = "Commit cancelled.";
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch commit failed and was rolled back.");
            foreach (var pf in preparedFiles)
            {
                pf.Report.Outcome = FileOutcome.Failed;
                pf.Report.Reason = "Batch commit failed and was rolled back: " + ex.Message;
            }
        }
    }

    private async Task CommitAllAsync(List<PreparedFile> files, CancellationToken ct)
    {
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var backup = $"{f.SourcePath}.bak.{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            try
            {
                HardLink.Create(backup, f.SourcePath);
                f.BackupPath = backup;
            }
            catch
            {
                foreach (var f2 in files.Where(x => x.BackupPath is not null))
                {
                    TryDelete(f2.BackupPath!);
                    f2.BackupPath = null;
                }
                throw;
            }
        }

        try
        {
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                File.Move(f.StagedPath, f.SourcePath, overwrite: true);
                f.Committed = true;
            }
        }
        catch
        {
            foreach (var f in files.Where(x => x.Committed))
            {
                try
                {
                    File.Move(f.BackupPath!, f.SourcePath, overwrite: true);
                    f.BackupPath = null;
                    f.Committed = false;
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(
                        rollbackEx,
                        "Rollback failed for '{Source}'. Manual recovery: mv '{Backup}' '{Source}'.",
                        f.SourcePath, f.BackupPath, f.SourcePath);
                }
            }

            foreach (var f in files.Where(x => x.BackupPath is not null))
            {
                TryDelete(f.BackupPath!);
                f.BackupPath = null;
            }
            throw;
        }
    }

    private sealed class PreparedFile
    {
        public required string SourcePath { get; init; }
        public required string TempPath { get; init; }
        public required string StagedPath { get; init; }
        public required PerFileResult Report { get; init; }
        public string? BackupPath { get; set; }
        public bool Committed { get; set; }
    }

    private sealed class PrepareResult
    {
        public required PerFileResult Report { get; init; }
        public PreparedFile? Prepared { get; init; }
    }

    private async Task<PrepareResult> PrepareFileAsync(
        string path, JobRequest request,
        FfprobeService probe, StreamSelectionService selector,
        FfmpegService ffmpeg, string tempDir,
        Func<PerFileResult, Task> publishReport,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PerFileResult? report = null;
        string? tempOut = null;
        string? staged = null;

        try
        {
            var media = await probe.ProbeAsync(path, ct).ConfigureAwait(false);
            var plan = selector.Plan(media, request.Selection);

            var displayStreams = media.Streams
                .Where(s => s.Kind != StreamKind.Other)
                .ToList();
            var attachmentCount = media.Streams.Count(s => s.Kind == StreamKind.Other);

            var streams = displayStreams.Select(s =>
            {
                var kept = plan.VideoIndices.Contains(s.Index)
                        || plan.AudioIndices.Contains(s.Index)
                        || plan.SubtitleIndices.Contains(s.Index);
                return MapToStreamInfo(s, kept);
            }).ToList();

            if (!plan.IsEligible)
            {
                report = new PerFileResult
                {
                    Path = path,
                    Outcome = FileOutcome.Skipped,
                    Reason = plan.SkipReason,
                    Streams = streams,
                    OriginalStreamCount = displayStreams.Count,
                    KeptStreamCount = 0,
                    Duration = sw.Elapsed
                };
                await publishReport(report).ConfigureAwait(false);
                return new PrepareResult { Report = report };
            }

            var mapArgs = plan.BuildMapArguments();
            if (mapArgs.Count == 0)
                throw new InvalidOperationException("Selection produced no -map arguments; refusing to invoke FFmpeg.");

            var keptCount = plan.VideoIndices.Count + plan.AudioIndices.Count + plan.SubtitleIndices.Count;
            var reportedAttachments = plan.IncludeAttachments ? (int?)attachmentCount : null;

            // Real filename plus a short unique suffix. Handles the stale-temp case (a previous
            // run crashed and left /tmp/<name> behind) and the collision case (two files with the
            // same basename across different show folders, since recursion is on). The check keeps 
            // the total name well under the 255-byte limit on ext4/xfs.
            var ext = Path.GetExtension(path);
            var baseName = Path.GetFileNameWithoutExtension(path);
            var suffix = ".sc" + Guid.NewGuid().ToString("N").Substring(0, 8);
            // 255 (NAME_MAX) minus the extension, the suffix, and one byte of slack.
            var budget = 255 - System.Text.Encoding.UTF8.GetByteCount(ext) - suffix.Length - 1;
            while (baseName.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(baseName) > budget)
            {
                baseName = baseName.Substring(0, baseName.Length - 1);
            }

            var fileName = baseName + suffix + ext;

            tempOut = Path.Combine(tempDir, fileName);
            staged = Path.Combine(Path.GetDirectoryName(path)!, "." + fileName);

            var command = request.Verbose
                ? BuildVerboseCommand(GetFfmpegPath(), path, tempOut, mapArgs)
                : null;

            if (request.DryRun)
            {
                report = new PerFileResult
                {
                    Path = path,
                    Outcome = FileOutcome.Succeeded,
                    Reason = "Dry run: would remove streams.",
                    Streams = streams,
                    OriginalStreamCount = displayStreams.Count,
                    KeptStreamCount = keptCount,
                    AttachmentCount = reportedAttachments,
                    Command = command,
                    Duration = sw.Elapsed
                };
                await publishReport(report).ConfigureAwait(false);
                return new PrepareResult { Report = report };
            }

            if (IsFileBeingPlayed(path))
            {
                report = new PerFileResult
                {
                    Path = path,
                    Outcome = FileOutcome.Skipped,
                    Reason = "File is currently being played by a Jellyfin session.",
                    Streams = streams,
                    OriginalStreamCount = displayStreams.Count,
                    KeptStreamCount = 0,
                    Duration = sw.Elapsed
                };
                await publishReport(report).ConfigureAwait(false);
                return new PrepareResult { Report = report };
            }

            var sourceDir = Path.GetDirectoryName(path)!;
            EnsureDirWritable(sourceDir);
            EnsureHardlinkPossible(sourceDir, path);

            // Interim report: the plan is known, so publish it now. The UI sees the stream
            // table with ✓ marks while FFmpeg runs — this is the "dry run output" the operator
            // expects during a real run.
            report = new PerFileResult
            {
                Path = path,
                Outcome = FileOutcome.Running,
                Reason = "Preparing…",
                Streams = streams,
                OriginalStreamCount = displayStreams.Count,
                KeptStreamCount = keptCount,
                AttachmentCount = reportedAttachments,
                Command = command
            };
            await publishReport(report).ConfigureAwait(false);

            try
            {
                await ffmpeg.RunStreamCopyAsync(path, tempOut, mapArgs, ct).ConfigureAwait(false);

                var tempVerified = await probe.ProbeAsync(tempOut, ct).ConfigureAwait(false);
                var tempVaS = tempVerified.Streams.Count(s => s.Kind != StreamKind.Other);
                if (tempVaS != keptCount)
                    throw new InvalidOperationException(
                        $"Verification failed on temp output: expected {keptCount} V/A/S streams, got {tempVaS}.");

                File.Copy(tempOut, staged, overwrite: false);
                var stagedVerified = await probe.ProbeAsync(staged, ct).ConfigureAwait(false);
                var stagedVaS = stagedVerified.Streams.Count(s => s.Kind != StreamKind.Other);
                if (stagedVaS != keptCount)
                    throw new InvalidOperationException(
                        $"Verification failed on staged copy: expected {keptCount} V/A/S streams, got {stagedVaS}.");

                report.Reason = "Staged, pending commit";
                report.Duration = sw.Elapsed;
                await publishReport(report).ConfigureAwait(false);

                return new PrepareResult
                {
                    Report = report,
                    Prepared = new PreparedFile
                    {
                        SourcePath = path,
                        TempPath = tempOut,
                        StagedPath = staged,
                        Report = report
                    }
                };
            }
            catch
            {
                TryDelete(tempOut);
                TryDelete(staged);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            if (report is null)
            {
                report = new PerFileResult
                {
                    Path = path,
                    Outcome = FileOutcome.Cancelled,
                    Reason = "Cancelled.",
                    Duration = sw.Elapsed
                };
                await publishReport(report).ConfigureAwait(false);
            }
            else
            {
                report.Outcome = FileOutcome.Cancelled;
                report.Reason = "Cancelled.";
                report.Duration = sw.Elapsed;
                await publishReport(report).ConfigureAwait(false);
            }

            if (tempOut is not null) TryDelete(tempOut);
            if (staged is not null) TryDelete(staged);
            return new PrepareResult { Report = report };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prepare {Path}", path);

            if (report is null)
            {
                report = new PerFileResult
                {
                    Path = path,
                    Outcome = FileOutcome.Failed,
                    Reason = ex.Message,
                    Duration = sw.Elapsed
                };
                await publishReport(report).ConfigureAwait(false);
            }
            else
            {
                report.Outcome = FileOutcome.Failed;
                report.Reason = ex.Message;
                report.Duration = sw.Elapsed;
                await publishReport(report).ConfigureAwait(false);
            }

            if (tempOut is not null) TryDelete(tempOut);
            if (staged is not null) TryDelete(staged);
            return new PrepareResult { Report = report };
        }
    }

    private static string BuildVerboseCommand(
        string ffmpegPath, string inputPath, string tempOut, IReadOnlyList<string> mapArgs)
    {
        var parts = new List<string> { ffmpegPath, "-i", $"\"{inputPath}\"" };
        parts.AddRange(mapArgs);
        parts.Add("-c");
        parts.Add("copy");
        parts.Add($"\"{tempOut}\"");
        var ffmpegCmd = string.Join(" ", parts);

        return ffmpegCmd + "\n" + $"mv -v \"{tempOut}\" \"{inputPath}\"";
    }

    private PathValidator CreateValidator()
    {
        var roots = _libraryManager.GetVirtualFolders()
            .SelectMany(vf => vf.Locations ?? Array.Empty<string>())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
        return new PathValidator(roots);
    }

    private string GetFfmpegPath()
    {
        var path = _mediaEncoder.EncoderPath;
        return string.IsNullOrWhiteSpace(path) ? "ffmpeg" : path;
    }

    private string GetFfprobePath()
    {
        var path = _mediaEncoder.ProbePath;
        return string.IsNullOrWhiteSpace(path) ? "ffprobe" : path;
    }

    private static List<string> DiscoverFiles(JobRequest request, PathValidator validator)
    {
        if (Directory.Exists(request.InputPath))
        {
            var dir = validator.ValidateExistingDirectory(request.InputPath);
            var option = request.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return Directory.EnumerateFiles(dir, "*", option)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => validator.ValidateExistingFile(f))
                .ToList();
        }
        return new List<string> { validator.ValidateExistingFile(request.InputPath) };
    }

    private async Task<ProbedFile> ProbeFileAsync(string path, FfprobeService probe, CancellationToken ct)
    {
        var info = new ProbedFile { Path = path };
        try
        {
            var media = await probe.ProbeAsync(path, ct).ConfigureAwait(false);
            info.FormatName = media.FormatName;
            info.Duration = media.Duration;
            try { info.Size = new FileInfo(path).Length; } catch { }
            info.Streams = media.Streams
                .Where(s => s.Kind != StreamKind.Other)
                .Select(s => MapToStreamInfo(s, kept: false))
                .ToList();
        }
        catch (Exception ex)
        {
            info.Error = ex.Message;
        }
        return info;
    }

    private static StreamInfo MapToStreamInfo(ProbedStream s, bool kept) => new()
    {
        Index = s.Index,
        Kind = s.Kind.ToString().ToLowerInvariant(),
        Codec = s.CodecName,
        Language = s.Language,
        Title = s.Title,
        Channels = s.Channels,
        Width = s.Width,
        Height = s.Height,
        Kept = kept
    };

    private bool IsFileBeingPlayed(string path)
    {
        try
        {
            foreach (var session in _sessionManager.Sessions)
            {
                if (session.NowPlayingItem?.Path is { } np &&
                    string.Equals(np, path, StringComparison.Ordinal))
                    return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Playback check failed for {Path}", path);
        }
        return false;
    }

    private static void EnsureDirWritable(string dir)
    {
        Directory.CreateDirectory(dir);
        var probe = Path.Combine(dir, $".sc-probe.{Guid.NewGuid():N}");
        try { File.WriteAllText(probe, "ok"); }
        finally { try { File.Delete(probe); } catch { } }
    }

    private static void EnsureHardlinkPossible(string dir, string existingFile)
    {
        var probe = Path.Combine(dir, $".sc-hl-probe.{Guid.NewGuid():N}");
        try { HardLink.Create(probe, existingFile); }
        finally { try { File.Delete(probe); } catch { } }
    }

    private void RefreshLibrary(string path)
    {
        try
        {
            var item = _libraryManager.FindByPath(path, isFolder: false);
            if (item is null) return;
            var options = new MetadataRefreshOptions(_directoryService)
            {
                MetadataRefreshMode = MetadataRefreshMode.Default
            };
            _providerManager.QueueRefresh(item.Id, options, RefreshPriority.High);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Library refresh failed for {Path}", path);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
