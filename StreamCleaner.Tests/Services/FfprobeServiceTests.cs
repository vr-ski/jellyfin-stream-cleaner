using System;
using System.Linq;
using Jellyfin.Plugin.StreamCleaner.Models;
using Jellyfin.Plugin.StreamCleaner.Services;
using Xunit;

namespace StreamCleaner.Tests.Services;

public sealed class FfprobeServiceTests
{
    private const string ValidJson = """
    {
      "streams": [
        { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080 },
        { "index": 1, "codec_type": "audio", "codec_name": "aac", "channels": 2,
          "tags": { "language": "eng", "title": "English" } },
        { "index": 2, "codec_type": "subtitle", "codec_name": "subrip",
          "tags": { "language": "jpn" } }
      ],
      "format": { "format_name": "matroska,webm", "duration": "1234.5" }
    }
    """;

    [Fact]
    public void ParseProbeJson_ValidJson_ReturnsAllStreams()
    {
        var media = FfprobeService.ParseProbeJson("/x.mkv", ValidJson);
        Assert.Equal(3, media.Streams.Count);
        Assert.Equal("matroska,webm", media.FormatName);
        Assert.Equal(TimeSpan.FromSeconds(1234.5), media.Duration);
    }

    [Fact]
    public void ParseProbeJson_MapsStreamKinds()
    {
        var media = FfprobeService.ParseProbeJson("/x.mkv", ValidJson);
        Assert.Equal(StreamKind.Video,    media.Streams[0].Kind);
        Assert.Equal(StreamKind.Audio,    media.Streams[1].Kind);
        Assert.Equal(StreamKind.Subtitle, media.Streams[2].Kind);
    }

    [Fact]
    public void ParseProbeJson_MapsVideoDimensions()
    {
        var s = FfprobeService.ParseProbeJson("/x.mkv", ValidJson).Streams[0];
        Assert.Equal(1920, s.Width);
        Assert.Equal(1080, s.Height);
        Assert.Equal("h264", s.CodecName);
    }

    [Fact]
    public void ParseProbeJson_MapsAudioChannelsAndTags()
    {
        var s = FfprobeService.ParseProbeJson("/x.mkv", ValidJson).Streams[1];
        Assert.Equal(2, s.Channels);
        Assert.Equal("eng", s.Language);
        Assert.Equal("English", s.Title);
    }

    [Fact]
    public void ParseProbeJson_TagsAreCaseInsensitive()
    {
        const string json = """
        { "streams": [{ "index": 0, "codec_type": "audio",
            "tags": { "LANGUAGE": "fra", "Title": "Français" } }] }
        """;
        var s = FfprobeService.ParseProbeJson("/x.mkv", json).Streams[0];
        Assert.Equal("fra", s.Language);
        Assert.Equal("Français", s.Title);
    }

    [Fact]
    public void ParseProbeJson_UnknownCodecType_MapsToOther()
    {
        const string json = """{ "streams": [{ "index": 0, "codec_type": "attachment" }] }""";
        Assert.Equal(StreamKind.Other, FfprobeService.ParseProbeJson("/x.mkv", json).Streams[0].Kind);
    }

    [Fact]
    public void ParseProbeJson_StreamWithoutLanguage_HasNullLanguage()
    {
        const string json = """{ "streams": [{ "index": 0, "codec_type": "audio" }] }""";
        Assert.Null(FfprobeService.ParseProbeJson("/x.mkv", json).Streams[0].Language);
    }

    [Fact]
    public void ParseProbeJson_MalformedJson_Throws()
    {
        var ex = Assert.Throws<FfprobeFailedException>(
            () => FfprobeService.ParseProbeJson("/x.mkv", "{ not json"));
        Assert.Equal(ProbeFailureReason.MalformedJson, ex.FailureReason);
    }

    [Fact]
    public void ParseProbeJson_MissingStreamsProperty_Throws()
    {
        var ex = Assert.Throws<FfprobeFailedException>(
            () => FfprobeService.ParseProbeJson("/x.mkv", """{ "format": {} }"""));
        Assert.Equal(ProbeFailureReason.InvalidMetadata, ex.FailureReason);
    }

    [Fact]
    public void ParseProbeJson_StreamsNotArray_Throws()
    {
        var ex = Assert.Throws<FfprobeFailedException>(
            () => FfprobeService.ParseProbeJson("/x.mkv", """{ "streams": {} }"""));
        Assert.Equal(ProbeFailureReason.InvalidMetadata, ex.FailureReason);
    }

    [Fact]
    public void ParseProbeJson_StreamMissingIndex_Throws()
    {
        var ex = Assert.Throws<FfprobeFailedException>(
            () => FfprobeService.ParseProbeJson("/x.mkv", """{ "streams": [{ "codec_type": "audio" }] }"""));
        Assert.Equal(ProbeFailureReason.InvalidMetadata, ex.FailureReason);
    }

    [Fact]
    public void ParseProbeJson_NegativeIndex_Throws()
    {
        var ex = Assert.Throws<FfprobeFailedException>(
            () => FfprobeService.ParseProbeJson("/x.mkv", """{ "streams": [{ "index": -1 }] }"""));
        Assert.Equal(ProbeFailureReason.InvalidMetadata, ex.FailureReason);
    }

    [Fact]
    public void ParseProbeJson_DuplicateIndex_Throws()
    {
        var ex = Assert.Throws<FfprobeFailedException>(
            () => FfprobeService.ParseProbeJson("/x.mkv",
                """{ "streams": [{ "index": 0 }, { "index": 0 }] }"""));
        Assert.Equal(ProbeFailureReason.InvalidMetadata, ex.FailureReason);
    }

    [Theory]
    [InlineData("123.5",  123.5)]
    [InlineData("0",      0.0)]
    public void ParseProbeJson_ValidDuration_Parsed(string raw, double seconds)
    {
        var json = $$"""{ "streams": [], "format": { "duration": "{{raw}}" } }""";
        Assert.Equal(TimeSpan.FromSeconds(seconds), FfprobeService.ParseProbeJson("/x", json).Duration);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("")]
    public void ParseProbeJson_InvalidDuration_ReturnsNull(string raw)
    {
        var json = $$"""{ "streams": [], "format": { "duration": "{{raw}}" } }""";
        Assert.Null(FfprobeService.ParseProbeJson("/x", json).Duration);
    }
}
