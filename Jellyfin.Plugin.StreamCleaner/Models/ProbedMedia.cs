using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.StreamCleaner.Models;

/// <summary>
/// Represents the probed metadata of a media file.
/// </summary>
/// <param name="Path">Validated, normalized absolute path of the media file. Not symlink-resolved.</param>
/// <param name="Duration">Container duration, or <c>null</c> if unavailable or invalid.</param>
/// <param name="Streams">Streams reported by the probe. Order is preserved from FFprobe output.</param>
public sealed record ProbedMedia(
    string Path,
    TimeSpan? Duration,
    IReadOnlyList<ProbedStream> Streams)
{
    /// <summary>
    /// Container format name reported by FFprobe (for example <c>matroska,webm</c>).
    /// <c>null</c> means the format was not reported; callers must not assume a default.
    /// </summary>
    public string? FormatName { get; init; }
}

/// <summary>
/// Represents a single stream from a probed media file.
/// </summary>
/// <param name="Index">Non-negative stream index as reported by FFprobe. Unique within a <see cref="ProbedMedia"/>.</param>
/// <param name="Kind">Normalized stream kind.</param>
/// <param name="CodecName">Codec short name as reported by FFprobe, or <c>null</c>.</param>
/// <param name="Language">Language tag value, or <c>null</c>. Value is unchanged from metadata.</param>
/// <param name="Title">Title tag value, or <c>null</c>.</param>
/// <param name="Duration">Stream duration, or <c>null</c> if unavailable or invalid.</param>
/// <param name="Width">Video width in pixels, or <c>null</c>.</param>
/// <param name="Height">Video height in pixels, or <c>null</c>.</param>
/// <param name="Channels">Audio channel count, or <c>null</c>.</param>
/// <param name="BitRate">Bit rate in bits per second, or <c>null</c>.</param>
/// <param name="Tags">Stream tags, or <c>null</c> if none were reported. Keys are case-insensitive.</param>
public sealed record ProbedStream(
    int Index,
    StreamKind Kind,
    string? CodecName,
    string? Language,
    string? Title,
    TimeSpan? Duration,
    int? Width,
    int? Height,
    int? Channels,
    long? BitRate,
    IReadOnlyDictionary<string, string>? Tags);

/// <summary>
/// Normalized classification of a media stream.
/// </summary>
public enum StreamKind
{
    /// <summary>Video stream.</summary>
    Video,

    /// <summary>Audio stream.</summary>
    Audio,

    /// <summary>Subtitle stream.</summary>
    Subtitle,

    /// <summary>Any stream type not recognized as video, audio, or subtitle.</summary>
    Other
}
