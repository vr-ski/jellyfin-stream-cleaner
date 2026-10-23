using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.StreamCleaner.Models;

/// <summary>Persisted state of a single cleaning job.</summary>
public sealed class JobState
{
    public Guid Id { get; set; }
    public required JobRequest Request { get; set; }
    public JobStatus Status { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public List<PerFileResult> Results { get; set; } = new();
}

/// <summary>Lifecycle state of a job.</summary>
public enum JobStatus { Pending, Running, Succeeded, Failed, Interrupted, Cancelled }

/// <summary>Outcome for a single processed file.</summary>
public enum FileOutcome { Running, Succeeded, Skipped, Failed, Cancelled }

/// <summary>Serializable description of a single stream, for UI display.</summary>
public sealed class StreamInfo
{
    public int Index { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public int? Channels { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool Kept { get; set; }
}

/// <summary>Result for a single file within a job.</summary>
public sealed class PerFileResult
{
    public required string Path { get; set; }
    public FileOutcome Outcome { get; set; }
    public string? Reason { get; set; }
    public List<StreamInfo> Streams { get; set; } = new();
    public int? OriginalStreamCount { get; set; }
    public int? KeptStreamCount { get; set; }
    public int? AttachmentCount { get; set; }
    public string? Command { get; set; }
    public TimeSpan? Duration { get; set; }
}

/// <summary>
/// Parameters for a single cleaning run. When <see cref="Recursive"/> is true (default),
/// subdirectories of the selected directory are also processed. When false, only the
/// top-level files inside the selected directory are processed.
/// </summary>
public sealed class JobRequest
{
    public required string InputPath { get; set; }
    public bool Recursive { get; set; } = true;
    public required StreamSelectionRequest Selection { get; set; }
    public bool DryRun { get; set; } = true;
    public bool Verbose { get; set; }
}
