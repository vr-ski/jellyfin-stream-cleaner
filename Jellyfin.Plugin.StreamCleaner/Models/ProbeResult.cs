using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.StreamCleaner.Models;

/// <summary>Request body for the probe preview endpoint.</summary>
public sealed class ProbeRequest
{
    public required string Path { get; set; }
}

/// <summary>Response body from the probe preview endpoint.</summary>
public sealed class ProbeResult
{
    public bool IsDirectory { get; set; }
    public int FileCount { get; set; }
    public int ProbedCount { get; set; }
    public List<ProbedFile> Files { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>Probe result for a single media file.</summary>
public sealed class ProbedFile
{
    public required string Path { get; set; }
    public string? FormatName { get; set; }
    public long? Size { get; set; }
    public TimeSpan? Duration { get; set; }
    public List<StreamInfo> Streams { get; set; } = new();
    public string? Error { get; set; }
}
