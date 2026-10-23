using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.StreamCleaner.Models;

/// <summary>Parameters for a single stream-selection decision.</summary>
public sealed class StreamSelectionRequest
{
    public IReadOnlyList<string> AudioLanguages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> AudioTrackIndices { get; init; } = Array.Empty<int>();
    public IReadOnlyList<string> SubtitleLanguages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> SubtitleTrackIndices { get; init; } = Array.Empty<int>();
    public bool IncludeVideo { get; init; }

    /// <summary>
    /// Gets a value indicating whether attachment streams (fonts for ASS/SSA subtitles,
    /// cover art in some containers) are preserved. Additive only: does not make a file
    /// eligible for processing on its own.
    /// </summary>
    public bool IncludeAttachments { get; init; }
}

/// <summary>Result of applying a <see cref="StreamSelectionRequest"/> to a <see cref="ProbedMedia"/>.</summary>
public sealed class StreamSelectionPlan
{
    public required string InputPath { get; init; }
    public IReadOnlyList<int> VideoIndices { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> AudioIndices { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> SubtitleIndices { get; init; } = Array.Empty<int>();
    public required bool IsEligible { get; init; }
    public string? SkipReason { get; init; }

    /// <summary>Gets a value indicating whether attachment streams should be preserved.</summary>
    public bool IncludeAttachments { get; init; }

    /// <summary>
    /// Builds the FFmpeg <c>-map</c> argument list. When <see cref="IncludeAttachments"/>
    /// is true, a trailing <c>-map 0:t?</c> is appended so every attachment stream is
    /// preserved; the <c>?</c> makes it a no-op for files without attachments.
    /// </summary>
    public IReadOnlyList<string> BuildMapArguments()
    {
        var all = VideoIndices
            .Concat(AudioIndices)
            .Concat(SubtitleIndices)
            .OrderBy(i => i);

        var args = new List<string>();
        foreach (var idx in all)
        {
            args.Add("-map");
            args.Add($"0:{idx}");
        }
        if (IncludeAttachments)
        {
            args.Add("-map");
            args.Add("0:t?");
        }
        return args;
    }
}
