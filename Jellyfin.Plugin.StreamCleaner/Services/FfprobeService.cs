using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamCleaner.Models;

namespace Jellyfin.Plugin.StreamCleaner.Services;

/// <summary>
/// Runs FFprobe against a media file and returns a typed <see cref="ProbedMedia"/> model.
/// This service does not modify media files.
/// </summary>
public sealed class FfprobeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _ffprobePath;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _cleanupTimeout;
    private readonly int _maxOutputChars;
    private readonly Action<string, Exception?>? _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfprobeService"/> class.
    /// </summary>
    /// <param name="ffprobePath">Executable name or absolute path to FFprobe.</param>
    /// <param name="probeTimeout">Maximum FFprobe execution time. Defaults to 30 seconds.</param>
    /// <param name="cleanupTimeout">Overall deadline for process termination and reader cleanup. Defaults to 5 seconds.</param>
    /// <param name="maxOutputChars">Maximum characters retained from each output pipe. Defaults to 1,000,000.</param>
    /// <param name="log">Optional callback for cleanup diagnostics.</param>
    public FfprobeService(
        string ffprobePath = "ffprobe",
        TimeSpan? probeTimeout = null,
        TimeSpan? cleanupTimeout = null,
        int maxOutputChars = 1_000_000,
        Action<string, Exception?>? log = null)
    {
        if (string.IsNullOrWhiteSpace(ffprobePath))
        {
            throw new ArgumentException("FFprobe path is required.", nameof(ffprobePath));
        }

        _ffprobePath = ffprobePath;
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(30);
        _cleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(5);
        _maxOutputChars = maxOutputChars;
        _log = log;

        if (_probeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(probeTimeout));
        }

        if (_cleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
        }

        if (_maxOutputChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputChars));
        }
    }

    /// <summary>
    /// Probes a media file with FFprobe.
    /// </summary>
    /// <param name="mediaPath">Absolute path to the media file.</param>
    /// <param name="cancellationToken">Caller cancellation token.</param>
    /// <returns>The probed media model.</returns>
    /// <exception cref="ArgumentException">The media path is empty or not absolute.</exception>
    /// <exception cref="FfprobeNotFoundException">The FFprobe executable could not be located.</exception>
    /// <exception cref="FfprobeFailedException">FFprobe failed, timed out, exceeded output limits, or returned invalid metadata.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled the operation.</exception>
    public async Task<ProbedMedia> ProbeAsync(
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            throw new ArgumentException("Media path is required.", nameof(mediaPath));
        }

        if (!Path.IsPathFullyQualified(mediaPath))
        {
            throw new ArgumentException("Media path must be absolute.", nameof(mediaPath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(mediaPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(fullPath);

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new FfprobeFailedException(
                    "Failed to start FFprobe.",
                    ProbeFailureReason.ProcessStart);
            }
        }
        catch (Win32Exception ex) when (IsExecutableNotFound(ex))
        {
            throw new FfprobeNotFoundException(
                $"FFprobe executable was not found: {_ffprobePath}",
                ex);
        }
        catch (Win32Exception ex)
        {
            throw new FfprobeFailedException(
                $"Failed to start FFprobe: {_ffprobePath}",
                ProbeFailureReason.ProcessStart,
                innerException: ex);
        }

        using var timeoutCts = new CancellationTokenSource(_probeTimeout);
        using var overflowCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token,
            overflowCts.Token);

        var stdoutTask = ReadToEndLimitedAsync(
            process.StandardOutput,
            _maxOutputChars,
            linkedCts.Token,
            overflowCts);
        var stderrTask = ReadToEndLimitedAsync(
            process.StandardError,
            _maxOutputChars,
            linkedCts.Token,
            overflowCts);
        var readersTask = Task.WhenAll(stdoutTask, stderrTask);
        ObserveFaults(readersTask);
        var exitTask = process.WaitForExitAsync(linkedCts.Token);
        ObserveFaults(exitTask);

        // Observe process exit and both pipes together. A reader fault while the
        // process is still alive must not be hidden until the probe timeout.
        var pending = new HashSet<Task> { exitTask, stdoutTask, stderrTask };
        while (pending.Contains(exitTask))
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            if (completed == exitTask)
            {
                break;
            }

            try
            {
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is classified when awaiting exitTask below.
                break;
            }
            catch (Exception ex)
            {
                var cleanup = await TerminateAndDrainAsync(
                    process,
                    readersTask,
                    linkedCts).ConfigureAwait(false);

                throw new FfprobeFailedException(
                    "Failed to read FFprobe output.",
                    ProbeFailureReason.OutputRead,
                    processExitConfirmed: cleanup.ProcessExitConfirmed,
                    outputDrainComplete: cleanup.OutputDrainComplete,
                    innerException: ex);
            }
        }

        try
        {
            await exitTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cleanup = await TerminateAndDrainAsync(process, readersTask, linkedCts).ConfigureAwait(false);
            _log?.Invoke(
                $"FFprobe cancelled by caller. Process exit confirmed: {cleanup.ProcessExitConfirmed}; output drain complete: {cleanup.OutputDrainComplete}.",
                null);
            throw;
        }
        catch (OperationCanceledException) when (overflowCts.IsCancellationRequested)
        {
            var cleanup = await TerminateAndDrainAsync(process, readersTask, linkedCts).ConfigureAwait(false);
            throw new FfprobeFailedException(
                $"FFprobe output exceeded the configured limit of {_maxOutputChars} characters per pipe.",
                ProbeFailureReason.OutputLimit,
                processExitConfirmed: cleanup.ProcessExitConfirmed,
                // The capture was intentionally truncated at the limit, even if the
                // reader tasks themselves settled during cleanup.
                outputDrainComplete: false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            var cleanup = await TerminateAndDrainAsync(process, readersTask, linkedCts).ConfigureAwait(false);
            throw new FfprobeFailedException(
                "FFprobe timed out.",
                ProbeFailureReason.Timeout,
                processExitConfirmed: cleanup.ProcessExitConfirmed,
                outputDrainComplete: cleanup.OutputDrainComplete);
        }
        catch (OperationCanceledException)
        {
            var cleanup = await TerminateAndDrainAsync(process, readersTask, linkedCts).ConfigureAwait(false);
            throw new FfprobeFailedException(
                "FFprobe wait was cancelled for an unrecognized reason.",
                ProbeFailureReason.Unknown,
                processExitConfirmed: cleanup.ProcessExitConfirmed,
                outputDrainComplete: cleanup.OutputDrainComplete);
        }

        // FFprobe has exited. Drain should normally finish at EOF. If caller
        // cancellation, overflow, timeout, or a drain deadline wins this race,
        // stop the readers and report the process-exit and drain states separately.
        using var drainCts = new CancellationTokenSource(_cleanupTimeout);
        try
        {
            await readersTask.WaitAsync(drainCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopOutputReaders(process, linkedCts);
            _log?.Invoke("Caller cancelled while FFprobe output was draining after process exit.", null);
            throw;
        }
        catch (OperationCanceledException) when (overflowCts.IsCancellationRequested)
        {
            StopOutputReaders(process, linkedCts);
            throw new FfprobeFailedException(
                $"FFprobe output exceeded the configured limit of {_maxOutputChars} characters per pipe.",
                ProbeFailureReason.OutputLimit,
                processExitConfirmed: true,
                outputDrainComplete: false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            StopOutputReaders(process, linkedCts);
            throw new FfprobeFailedException(
                "Timed out while draining FFprobe output after process exit.",
                ProbeFailureReason.OutputRead,
                processExitConfirmed: true,
                outputDrainComplete: false);
        }
        catch (OperationCanceledException) when (drainCts.IsCancellationRequested)
        {
            StopOutputReaders(process, linkedCts);
            throw new FfprobeFailedException(
                "Timed out while draining FFprobe output after process exit.",
                ProbeFailureReason.OutputRead,
                processExitConfirmed: true,
                outputDrainComplete: false);
        }
        catch (OperationCanceledException)
        {
            StopOutputReaders(process, linkedCts);
            throw;
        }
        catch (FfprobeFailedException)
        {
            StopOutputReaders(process, linkedCts);
            throw;
        }
        catch (Exception ex)
        {
            StopOutputReaders(process, linkedCts);
            throw new FfprobeFailedException(
                "Failed to read FFprobe output.",
                ProbeFailureReason.OutputRead,
                processExitConfirmed: true,
                outputDrainComplete: false,
                innerException: ex);
        }

        if (overflowCts.IsCancellationRequested)
        {
            throw new FfprobeFailedException(
                $"FFprobe output exceeded the configured limit of {_maxOutputChars} characters per pipe.",
                ProbeFailureReason.OutputLimit,
                processExitConfirmed: true,
                outputDrainComplete: false);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new FfprobeFailedException(
                $"FFprobe failed with exit code {process.ExitCode}.",
                ProbeFailureReason.NonZeroExit,
                exitCode: process.ExitCode,
                standardError: stderr,
                processExitConfirmed: true,
                outputDrainComplete: true);
        }

        return ParseProbeJson(fullPath, stdout);
    }

    private static bool IsExecutableNotFound(Win32Exception exception)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // ERROR_FILE_NOT_FOUND = 2; ERROR_PATH_NOT_FOUND = 3.
            return exception.NativeErrorCode is 2 or 3;
        }

        // POSIX ENOENT = 2. Do not treat errno 3 (ESRCH) as not-found.
        return exception.NativeErrorCode == 2;
    }

    internal static ProbedMedia ParseProbeJson(string path, string json)
    {
        FfprobeRoot? root;
        try
        {
            root = JsonSerializer.Deserialize<FfprobeRoot>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FfprobeFailedException(
                "FFprobe returned malformed JSON.",
                ProbeFailureReason.MalformedJson,
                processExitConfirmed: true,
                outputDrainComplete: true,
                innerException: ex);
        }

        if (root is null || root.Streams.ValueKind != JsonValueKind.Array)
        {
            throw new FfprobeFailedException(
                "FFprobe JSON is missing a valid streams array.",
                ProbeFailureReason.InvalidMetadata,
                processExitConfirmed: true,
                outputDrainComplete: true);
        }

        var streams = new List<ProbedStream>();
        var seenIndices = new HashSet<int>();

        foreach (var element in root.Streams.EnumerateArray())
        {
            FfprobeStreamJson? raw;
            try
            {
                raw = element.Deserialize<FfprobeStreamJson>(JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new FfprobeFailedException(
                    "FFprobe returned a malformed stream entry.",
                    ProbeFailureReason.MalformedJson,
                    processExitConfirmed: true,
                    outputDrainComplete: true,
                    innerException: ex);
            }

            if (raw?.Index is null)
            {
                throw new FfprobeFailedException(
                    "FFprobe stream entry is missing a required index.",
                    ProbeFailureReason.InvalidMetadata,
                    processExitConfirmed: true,
                    outputDrainComplete: true);
            }

            var index = raw.Index.Value;
            if (index < 0)
            {
                throw new FfprobeFailedException(
                    $"FFprobe returned a negative stream index: {index}.",
                    ProbeFailureReason.InvalidMetadata,
                    processExitConfirmed: true,
                    outputDrainComplete: true);
            }

            if (!seenIndices.Add(index))
            {
                throw new FfprobeFailedException(
                    $"FFprobe returned duplicate stream index: {index}.",
                    ProbeFailureReason.InvalidMetadata,
                    processExitConfirmed: true,
                    outputDrainComplete: true);
            }

            var tags = raw.Tags is null
                ? null
                : new Dictionary<string, string>(raw.Tags, StringComparer.OrdinalIgnoreCase);

            streams.Add(new ProbedStream(
                Index: index,
                Kind: MapStreamKind(raw.CodecType),
                CodecName: raw.CodecName,
                Language: FindTag(tags, "language"),
                Title: FindTag(tags, "title"),
                Duration: ParseDuration(raw.Duration),
                Width: raw.Width,
                Height: raw.Height,
                Channels: raw.Channels,
                BitRate: ParseLong(raw.BitRateString),
                Tags: tags));
        }

        return new ProbedMedia(
            Path: path,
            Duration: ParseDuration(root.Format?.Duration),
            Streams: streams)
        {
            FormatName = root.Format?.FormatName
        };
    }

    private static string? FindTag(
        IReadOnlyDictionary<string, string>? tags,
        string key)
    {
        if (tags is null)
        {
            return null;
        }

        return tags.TryGetValue(key, out var value) ? value : null;
    }

    private static StreamKind MapStreamKind(string? codecType)
    {
        return codecType?.ToLowerInvariant() switch
        {
            "video" => StreamKind.Video,
            "audio" => StreamKind.Audio,
            "subtitle" => StreamKind.Subtitle,
            _ => StreamKind.Other
        };
    }

    private static TimeSpan? ParseDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        if (double.IsNaN(seconds) ||
            double.IsInfinity(seconds) ||
            seconds < 0 ||
            seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            return null;
        }

        try
        {
            return TimeSpan.FromSeconds(seconds);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static long? ParseLong(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return long.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private async Task<string> ReadToEndLimitedAsync(
        StreamReader reader,
        int maxChars,
        CancellationToken cancellationToken,
        CancellationTokenSource overflowCts)
    {
        var builder = new StringBuilder(Math.Min(maxChars, 16_384));
        var buffer = new char[4096];

        while (true)
        {
            int read;
            try
            {
                read = await reader
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (overflowCts.IsCancellationRequested)
            {
                return builder.ToString();
            }

            if (read == 0)
            {
                return builder.ToString();
            }

            if (builder.Length + read > maxChars)
            {
                var remaining = maxChars - builder.Length;
                if (remaining > 0)
                {
                    builder.Append(buffer, 0, remaining);
                }

                // Stop the process promptly; the supervisor classifies the failure.
                overflowCts.Cancel();
                return builder.ToString();
            }

            builder.Append(buffer, 0, read);
        }
    }

    private async Task<CleanupResult> TerminateAndDrainAsync(
        Process process,
        Task readersTask,
        CancellationTokenSource linkedCts)
    {
        using var cleanupCts = new CancellationTokenSource(_cleanupTimeout);
        var processExitConfirmed = await TerminateProcessAsync(process, cleanupCts.Token)
            .ConfigureAwait(false);

        TryCancel(linkedCts, "Failed to cancel FFprobe I/O during cleanup.");

        var outputDrainComplete = false;
        try
        {
            await readersTask.WaitAsync(cleanupCts.Token).ConfigureAwait(false);
            outputDrainComplete = true;
        }
        catch (OperationCanceledException) when (cleanupCts.IsCancellationRequested)
        {
            _log?.Invoke("Cleanup deadline reached while draining FFprobe output.", null);
            CloseOutputReaders(process);
        }
        catch (Exception ex)
        {
            _log?.Invoke("FFprobe cleanup did not drain output cleanly.", ex);
            CloseOutputReaders(process);
        }

        return new CleanupResult(processExitConfirmed, outputDrainComplete);
    }

    private async Task<bool> TerminateProcessAsync(Process process, CancellationToken cleanupToken)
    {
        if (HasExited(process))
        {
            return true;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // The process may have exited between HasExited and Kill.
            if (!HasExited(process))
            {
                _log?.Invoke("Failed to kill FFprobe process tree.", ex);
            }
        }

        try
        {
            await process.WaitForExitAsync(cleanupToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cleanupToken.IsCancellationRequested)
        {
            var exited = HasExited(process);
            if (!exited)
            {
                _log?.Invoke("Cleanup deadline reached while waiting for FFprobe to exit.", null);
            }

            return exited;
        }
        catch (Exception ex)
        {
            _log?.Invoke("Unexpected error waiting for FFprobe to exit.", ex);
            return HasExited(process);
        }
    }

    private void StopOutputReaders(Process process, CancellationTokenSource linkedCts)
    {
        TryCancel(linkedCts, "Failed to cancel FFprobe output readers.");
        CloseOutputReaders(process);
    }

    private void CloseOutputReaders(Process process)
    {
        try
        {
            process.StandardOutput.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Invoke("Failed to close FFprobe stdout reader.", ex);
        }

        try
        {
            process.StandardError.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Invoke("Failed to close FFprobe stderr reader.", ex);
        }
    }

    private void TryCancel(CancellationTokenSource source, string message)
    {
        try
        {
            source.Cancel();
        }
        catch (Exception ex)
        {
            _log?.Invoke(message, ex);
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void ObserveFaults(Task task)
    {
        _ = task.ContinueWith(
            static completed => { _ = completed.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record CleanupResult(bool ProcessExitConfirmed, bool OutputDrainComplete);

    private sealed class FfprobeRoot
    {
        [JsonPropertyName("streams")]
        public JsonElement Streams { get; set; }

        [JsonPropertyName("format")]
        public FfprobeFormatJson? Format { get; set; }
    }

    private sealed class FfprobeFormatJson
    {
        [JsonPropertyName("format_name")]
        public string? FormatName { get; set; }

        [JsonPropertyName("duration")]
        public string? Duration { get; set; }
    }

    private sealed class FfprobeStreamJson
    {
        [JsonPropertyName("index")]
        public int? Index { get; set; }

        [JsonPropertyName("codec_type")]
        public string? CodecType { get; set; }

        [JsonPropertyName("codec_name")]
        public string? CodecName { get; set; }

        [JsonPropertyName("duration")]
        public string? Duration { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        [JsonPropertyName("channels")]
        public int? Channels { get; set; }

        [JsonPropertyName("bit_rate")]
        public string? BitRateString { get; set; }

        [JsonPropertyName("tags")]
        public Dictionary<string, string>? Tags { get; set; }
    }
}

