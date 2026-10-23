using System;

namespace Jellyfin.Plugin.StreamCleaner.Services;

/// <summary>
/// Machine-readable classification of an FFprobe failure.
/// </summary>
public enum ProbeFailureReason
{
    /// <summary>The failure reason was not classified.</summary>
    Unknown,

    /// <summary>The configured probe timeout elapsed while FFprobe was running.</summary>
    Timeout,

    /// <summary>Captured output exceeded the configured limit.</summary>
    OutputLimit,

    /// <summary>An output reader failed or output could not be drained in time.</summary>
    OutputRead,

    /// <summary>FFprobe exited with a non-zero exit code.</summary>
    NonZeroExit,

    /// <summary>FFprobe produced JSON that could not be parsed.</summary>
    MalformedJson,

    /// <summary>FFprobe produced well-formed JSON with invalid or missing stream metadata.</summary>
    InvalidMetadata,

    /// <summary>The FFprobe process could not be started.</summary>
    ProcessStart,

    /// <summary>The configured FFprobe executable could not be located.</summary>
    NotFound
}

/// <summary>
/// Thrown when an FFprobe probe fails. This is also the base type for
/// <see cref="FfprobeNotFoundException"/>; inspect <see cref="FailureReason"/>
/// to distinguish failure categories.
/// </summary>
public class FfprobeFailedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FfprobeFailedException"/> class.
    /// </summary>
    /// <param name="message">Human-readable failure description.</param>
    /// <param name="failureReason">Machine-readable failure classification.</param>
    /// <param name="exitCode">FFprobe exit code, if one was observed.</param>
    /// <param name="standardError">Captured standard error, if available.</param>
    /// <param name="processExitConfirmed">
    /// Whether process exit was confirmed. <see langword="null"/> means not applicable
    /// or not established, such as when process startup failed.
    /// </param>
    /// <param name="outputDrainComplete">
    /// Whether both redirected output streams were read to completion. <see langword="null"/>
    /// means not applicable or not established.
    /// </param>
    /// <param name="innerException">Optional underlying cause.</param>
    public FfprobeFailedException(
        string message,
        ProbeFailureReason failureReason = ProbeFailureReason.Unknown,
        int? exitCode = null,
        string? standardError = null,
        bool? processExitConfirmed = null,
        bool? outputDrainComplete = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureReason = failureReason;
        ExitCode = exitCode;
        StandardError = standardError;
        IsProcessExitConfirmed = processExitConfirmed;
        IsOutputDrainComplete = outputDrainComplete;
    }

    /// <summary>Gets the machine-readable failure classification.</summary>
    public ProbeFailureReason FailureReason { get; }

    /// <summary>Gets the FFprobe process exit code, when one was observed.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets captured standard error, when available.</summary>
    public string? StandardError { get; }

    /// <summary>Gets whether the failure was caused by the probe timeout.</summary>
    public bool IsTimeout => FailureReason == ProbeFailureReason.Timeout;

    /// <summary>Gets whether the failure was caused by exceeding the output limit.</summary>
    public bool IsOutputLimit => FailureReason == ProbeFailureReason.OutputLimit;

    /// <summary>
    /// Gets whether FFprobe process exit was confirmed. This is independent of whether
    /// the redirected output streams were drained successfully.
    /// </summary>
    public bool? IsProcessExitConfirmed { get; }

    /// <summary>
    /// Gets whether both redirected output streams were drained to completion.
    /// A value of <see langword="false"/> means output capture was incomplete or failed.
    /// </summary>
    public bool? IsOutputDrainComplete { get; }
}

/// <summary>Thrown when the configured FFprobe executable cannot be located.</summary>
public sealed class FfprobeNotFoundException : FfprobeFailedException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FfprobeNotFoundException"/> class.
    /// </summary>
    /// <param name="message">Human-readable failure description.</param>
    /// <param name="innerException">Optional underlying cause.</param>
    public FfprobeNotFoundException(string message, Exception? innerException = null)
        : base(message, ProbeFailureReason.NotFound, innerException: innerException)
    {
    }
}

