using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.StreamCleaner.Services;

/// <summary>Thrown when FFmpeg cannot run or exits non-zero.</summary>
public sealed class FfmpegFailedException : Exception
{
    public FfmpegFailedException(string message, int? exitCode = null, string? stderr = null, Exception? inner = null)
        : base(message, inner)
    {
        ExitCode = exitCode;
        StandardError = stderr;
    }

    public int? ExitCode { get; }
    public string? StandardError { get; }
}

/// <summary>Runs FFmpeg stream-copy remuxes with strict argument handling and cancellation.</summary>
public sealed class FfmpegService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(2);

    private readonly string _ffmpegPath;
    private readonly TimeSpan _timeout;

    public FfmpegService(string ffmpegPath, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            throw new ArgumentException("FFmpeg path is required.", nameof(ffmpegPath));
        _ffmpegPath = ffmpegPath;
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    /// <summary>
    /// Runs FFmpeg with <c>-c copy</c> over the specified <c>-map</c> arguments.
    /// Output is written to <paramref name="outputPath"/>, which must not exist.
    /// </summary>
    public async Task RunStreamCopyAsync(
        string inputPath,
        string outputPath,
        IReadOnlyList<string> mapArguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!Path.IsPathFullyQualified(inputPath)) throw new ArgumentException("Input must be absolute.", nameof(inputPath));
        if (!Path.IsPathFullyQualified(outputPath)) throw new ArgumentException("Output must be absolute.", nameof(outputPath));
        if (mapArguments is null || mapArguments.Count == 0)
            throw new ArgumentException("At least one -map argument is required.", nameof(mapArguments));
        if (File.Exists(outputPath))
            throw new InvalidOperationException($"Output file already exists: '{outputPath}'.");

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        foreach (var a in mapArguments) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
                throw new FfmpegFailedException("Failed to start FFmpeg.");
        }
        catch (Win32Exception ex) when (IsNotFound(ex))
        {
            throw new FfmpegFailedException($"FFmpeg executable not found: {_ffmpegPath}", inner: ex);
        }
        catch (Win32Exception ex)
        {
            throw new FfmpegFailedException($"Failed to start FFmpeg: {ex.Message}", inner: ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await DrainQuietlyAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            TryDelete(outputPath);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await DrainQuietlyAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            TryDelete(outputPath);
            throw new FfmpegFailedException($"FFmpeg timed out after {_timeout.TotalMinutes:0} minutes.");
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            TryDelete(outputPath);
            throw new FfmpegFailedException(
                $"FFmpeg exited with code {process.ExitCode}.",
                exitCode: process.ExitCode,
                stderr: stderr);
        }
    }

    private static bool IsNotFound(Win32Exception ex) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ex.NativeErrorCode is 2 or 3
            : ex.NativeErrorCode == 2;

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static async Task DrainQuietlyAsync(Task<string> a, Task<string> b)
    {
        try { await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { }
    }
}
