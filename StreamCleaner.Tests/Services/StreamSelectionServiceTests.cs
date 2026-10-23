using System;
using System.Collections.Generic;
using Jellyfin.Plugin.StreamCleaner.Models;
using Jellyfin.Plugin.StreamCleaner.Services;

namespace StreamCleaner.Tests.Services;

public sealed class StreamSelectionServiceTests
{
    private readonly StreamSelectionService _sut = new();

    // ---------- Fixtures ----------

    private static ProbedStream Stream(int index, StreamKind kind, string? language = null) =>
        new(index, kind, null, language, null, null, null, null, null, null, null);

    private static ProbedMedia Media(params ProbedStream[] streams) =>
        new("test.mkv", null, streams);

    private static readonly ProbedMedia MixedFile = Media(
        Stream(0, StreamKind.Video),
        Stream(1, StreamKind.Audio, "eng"),
        Stream(2, StreamKind.Audio, "jpn"),
        Stream(3, StreamKind.Subtitle, "eng"),
        Stream(4, StreamKind.Subtitle, "fra"));

    private static StreamSelectionRequest Req(
        IReadOnlyList<string>? audioLanguages = null,
        IReadOnlyList<int>? audioIndices = null,
        IReadOnlyList<string>? subtitleLanguages = null,
        IReadOnlyList<int>? subtitleIndices = null,
        bool includeVideo = false,
        bool includeAttachments = false) =>
        new()
        {
            AudioLanguages = audioLanguages ?? Array.Empty<string>(),
            AudioTrackIndices = audioIndices ?? Array.Empty<int>(),
            SubtitleLanguages = subtitleLanguages ?? Array.Empty<string>(),
            SubtitleTrackIndices = subtitleIndices ?? Array.Empty<int>(),
            IncludeVideo = includeVideo,
            IncludeAttachments = includeAttachments
        };

    // ---------- Audio ----------

    [Fact]
    public void AudioByLanguage_KeepsMatchingStream()
    {
        var plan = _sut.Plan(MixedFile, Req(audioLanguages: new[] { "jpn" }));
        Assert.Equal(new[] { 2 }, plan.AudioIndices);
        Assert.True(plan.IsEligible);
    }

    [Fact]
    public void AudioByLanguage_IsCaseInsensitive()
    {
        var plan = _sut.Plan(MixedFile, Req(audioLanguages: new[] { "ENG" }));
        Assert.Equal(new[] { 1 }, plan.AudioIndices);
    }

    [Fact]
    public void AudioByLanguage_NoMatch_FileSkipped()
    {
        var plan = _sut.Plan(MixedFile, Req(audioLanguages: new[] { "deu" }));
        Assert.Empty(plan.AudioIndices);
        Assert.False(plan.IsEligible);
        Assert.NotNull(plan.SkipReason);
    }

    [Fact]
    public void AudioByIndex_KeepsSpecifiedStream()
    {
        var plan = _sut.Plan(MixedFile, Req(audioIndices: new[] { 2 }));
        Assert.Equal(new[] { 2 }, plan.AudioIndices);
    }

    [Fact]
    public void AudioByIndex_Deduplicates()
    {
        var plan = _sut.Plan(MixedFile, Req(audioIndices: new[] { 2, 2 }));
        Assert.Equal(new[] { 2 }, plan.AudioIndices);
    }

    [Fact]
    public void AudioByIndex_SortsAscending()
    {
        var plan = _sut.Plan(MixedFile, Req(audioIndices: new[] { 2, 1 }));
        Assert.Equal(new[] { 1, 2 }, plan.AudioIndices);
    }

    [Fact]
    public void AudioByIndex_InvalidIndex_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _sut.Plan(MixedFile, Req(audioIndices: new[] { 99 })));
    }

    [Fact]
    public void AudioByIndex_PointingAtVideoStream_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _sut.Plan(MixedFile, Req(audioIndices: new[] { 0 })));
    }

    [Fact]
    public void BothAudioLanguagesAndIndices_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _sut.Plan(MixedFile, Req(
                audioLanguages: new[] { "eng" },
                audioIndices: new[] { 1 })));
    }

    // ---------- Subtitles ----------

    [Fact]
    public void SubtitleByLanguage_KeepsMatchingStream()
    {
        var plan = _sut.Plan(MixedFile, Req(subtitleLanguages: new[] { "fra" }));
        Assert.Equal(new[] { 4 }, plan.SubtitleIndices);
    }

    [Fact]
    public void SubtitleByIndex_KeepsSpecifiedStream()
    {
        var plan = _sut.Plan(MixedFile, Req(subtitleIndices: new[] { 3 }));
        Assert.Equal(new[] { 3 }, plan.SubtitleIndices);
    }

    [Fact]
    public void SubtitleByIndex_PointingAtAudioStream_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _sut.Plan(MixedFile, Req(subtitleIndices: new[] { 1 })));
    }

    [Fact]
    public void BothSubtitleLanguagesAndIndices_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _sut.Plan(MixedFile, Req(
                subtitleLanguages: new[] { "eng" },
                subtitleIndices: new[] { 3 })));
    }

    // ---------- Video ----------

    [Fact]
    public void VideoExcluded_DoesNotMapVideoStreams()
    {
        var plan = _sut.Plan(MixedFile, Req(audioLanguages: new[] { "eng" }, includeVideo: false));
        Assert.Empty(plan.VideoIndices);
    }

    [Fact]
    public void VideoIncluded_MapsVideoStreams()
    {
        var plan = _sut.Plan(MixedFile, Req(audioLanguages: new[] { "eng" }, includeVideo: true));
        Assert.Equal(new[] { 0 }, plan.VideoIndices);
    }

    [Fact]
    public void VideoOnlySelection_IsIneligible()
    {
        var plan = _sut.Plan(MixedFile, Req(includeVideo: true));
        Assert.True(plan.VideoIndices.Count > 0);
        Assert.False(plan.IsEligible);
        Assert.Contains("No audio or subtitle", plan.SkipReason);
    }

    // ---------- Mixed ----------

    [Fact]
    public void AudioLanguageAndSubtitleIndex_Combines()
    {
        var plan = _sut.Plan(MixedFile, Req(
            audioLanguages: new[] { "jpn" },
            subtitleIndices: new[] { 3 }));
        Assert.Equal(new[] { 2 }, plan.AudioIndices);
        Assert.Equal(new[] { 3 }, plan.SubtitleIndices);
    }

    // ---------- Language tag edge cases ----------

    [Fact]
    public void StreamWithNullLanguage_DoesNotMatchLanguageRequest()
    {
        var media = Media(
            Stream(0, StreamKind.Audio, language: null),
            Stream(1, StreamKind.Audio, "eng"));
        var plan = _sut.Plan(media, Req(audioLanguages: new[] { "eng" }));
        Assert.Equal(new[] { 1 }, plan.AudioIndices);
    }

    [Fact]
    public void NoLanguageNormalization_Iso639OneVsTwoDoNotMatch()
    {
        var media = Media(Stream(0, StreamKind.Audio, "ja"));
        var plan = _sut.Plan(media, Req(audioLanguages: new[] { "jpn" }));
        Assert.Empty(plan.AudioIndices);
        Assert.False(plan.IsEligible);
    }

    // ---------- Map arguments ----------

    [Fact]
    public void BuildMapArguments_OrdersByIndex()
    {
        var plan = _sut.Plan(MixedFile, Req(
            audioLanguages: new[] { "jpn" },
            subtitleIndices: new[] { 3 },
            includeVideo: true));
        var args = plan.BuildMapArguments();
        Assert.Equal(new[]
        {
            "-map", "0:0",
            "-map", "0:2",
            "-map", "0:3"
        }, args);
    }

    [Fact]
    public void BuildMapArguments_WithAttachments_AppendsAttachmentSelector()
    {
        var plan = _sut.Plan(MixedFile, Req(
            audioLanguages: new[] { "eng" },
            includeAttachments: true));
        var args = plan.BuildMapArguments();
        Assert.Equal(new[]
        {
            "-map", "0:1",
            "-map", "0:t?"
        }, args);
    }

    [Fact]
    public void BuildMapArguments_WithoutAttachments_HasNoAttachmentSelector()
    {
        var plan = _sut.Plan(MixedFile, Req(audioLanguages: new[] { "eng" }));
        var args = plan.BuildMapArguments();
        Assert.DoesNotContain("0:t?", args);
    }
}
