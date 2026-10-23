using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.StreamCleaner.Models;

namespace Jellyfin.Plugin.StreamCleaner.Services;

/// <summary>
/// Pure stream-selection logic. Given a <see cref="ProbedMedia"/> and a
/// <see cref="StreamSelectionRequest"/>, produces a deterministic plan.
/// No I/O, no process execution, no filesystem access.
/// </summary>
/// <remarks>
/// Language matching is case-insensitive exact equality against the tag value
/// stored by FFprobe. No ISO 639-1/639-2 normalization is performed. A stream
/// without a language tag never matches a requested language.
/// </remarks>
public sealed class StreamSelectionService
{
    /// <summary>
    /// Plans which streams to keep for a single file.
    /// </summary>
    /// <param name="media">The probed media.</param>
    /// <param name="request">The selection request.</param>
    /// <returns>The plan. Callers must check <see cref="StreamSelectionPlan.IsEligible"/>.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// Both audio languages and audio track indices were specified, or an
    /// audio track index does not refer to an audio stream.
    /// </exception>
    public StreamSelectionPlan Plan(ProbedMedia media, StreamSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(request);

        if (request.AudioLanguages.Count > 0 && request.AudioTrackIndices.Count > 0)
        {
            throw new ArgumentException(
                "Audio languages and audio track indices are mutually exclusive.",
                nameof(request));
        }

        if (request.SubtitleLanguages.Count > 0 && request.SubtitleTrackIndices.Count > 0)
        {
            throw new ArgumentException(
                "Subtitle languages and subtitle track indices are mutually exclusive.",
                nameof(request));
        }

        var videoIndices = request.IncludeVideo
            ? media.Streams
                .Where(s => s.Kind == StreamKind.Video)
                .Select(s => s.Index)
                .OrderBy(i => i)
                .ToArray()
            : Array.Empty<int>();

        var audioIndices = ResolveAudioIndices(media, request);
        var subtitleIndices = ResolveSubtitleIndices(media, request);

        var eligible = audioIndices.Length > 0 || subtitleIndices.Length > 0;
        string? skipReason = null;
        if (!eligible)
        {
            skipReason = (request.AudioLanguages.Count == 0
                          && request.AudioTrackIndices.Count == 0
                          && request.SubtitleLanguages.Count == 0
                          && request.SubtitleTrackIndices.Count == 0)
                ? "No audio or subtitle selection was requested."
                : "No streams matched the requested selection.";
        }

        return new StreamSelectionPlan
        {
            InputPath = media.Path,
            VideoIndices = videoIndices,
            AudioIndices = audioIndices,
            SubtitleIndices = subtitleIndices,
            IsEligible = eligible,
            SkipReason = skipReason,
            IncludeAttachments = request.IncludeAttachments
        };
    }

    private static int[] ResolveAudioIndices(ProbedMedia media, StreamSelectionRequest request)
    {
        if (request.AudioTrackIndices.Count > 0)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            foreach (var idx in request.AudioTrackIndices)
            {
                if (idx < 0)
                {
                    throw new ArgumentException(
                        $"Audio track index must be non-negative: {idx}", nameof(request));
                }

                if (!seen.Add(idx))
                {
                    continue; // dedupe silently
                }

                var stream = media.Streams.FirstOrDefault(s => s.Index == idx);
                if (stream is null)
                {
                    throw new ArgumentException(
                        $"Audio track index {idx} does not exist in this file.", nameof(request));
                }

                if (stream.Kind != StreamKind.Audio)
                {
                    throw new ArgumentException(
                        $"Stream index {idx} is a {stream.Kind} stream, not audio.", nameof(request));
                }

                result.Add(idx);
            }

            result.Sort();
            return result.ToArray();
        }

        return ResolveLanguageMatches(media, StreamKind.Audio, request.AudioLanguages);
    }

    private static int[] ResolveSubtitleIndices(ProbedMedia media, StreamSelectionRequest request)
    {
        if (request.SubtitleTrackIndices.Count > 0)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            foreach (var idx in request.SubtitleTrackIndices)
            {
                if (idx < 0)
                {
                    throw new ArgumentException(
                        $"Subtitle track index must be non-negative: {idx}", nameof(request));
                }

                if (!seen.Add(idx))
                {
                    continue;
                }

                var stream = media.Streams.FirstOrDefault(s => s.Index == idx);
                if (stream is null)
                {
                    throw new ArgumentException(
                        $"Subtitle track index {idx} does not exist in this file.", nameof(request));
                }

                if (stream.Kind != StreamKind.Subtitle)
                {
                    throw new ArgumentException(
                        $"Stream index {idx} is a {stream.Kind} stream, not subtitle.", nameof(request));
                }

                result.Add(idx);
            }

            result.Sort();
            return result.ToArray();
        }

        return ResolveLanguageMatches(media, StreamKind.Subtitle, request.SubtitleLanguages);
    }

    private static int[] ResolveLanguageMatches(
        ProbedMedia media, StreamKind kind, IReadOnlyList<string> languages)
    {
        if (languages.Count == 0)
        {
            return Array.Empty<int>();
        }

        var wanted = new HashSet<string>(
            languages.Where(l => !string.IsNullOrEmpty(l)),
            StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0)
        {
            return Array.Empty<int>();
        }

        return media.Streams
            .Where(s => s.Kind == kind)
            .Where(s => s.Language is not null && wanted.Contains(s.Language))
            .Select(s => s.Index)
            .OrderBy(i => i)
            .ToArray();
    }
}
