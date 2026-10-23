using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamCleaner.Services;
using StreamCleaner.Tests.Helpers;
using Xunit;

namespace StreamCleaner.Tests.Services;

public sealed class FfmpegServiceTests
{
    [Fact]
    public void Constructor_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new FfmpegService(""));
    }

    [Fact]
    public void Constructor_NonPositiveTimeout_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FfmpegService("ffmpeg", TimeSpan.Zero));
    }

    [Fact]
    public async Task RunStreamCopy_EmptyInput_Throws()
    {
        var sut = new FfmpegService("/nonexistent/ffmpeg");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.RunStreamCopyAsync("", "/tmp/o.mkv", new[] { "-map", "0:a" }, default));
    }

    [Fact]
    public async Task RunStreamCopy_RelativeInput_Throws()
    {
        var sut = new FfmpegService("/nonexistent/ffmpeg");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.RunStreamCopyAsync("relative.mkv", "/tmp/o.mkv", new[] { "-map", "0:a" }, default));
    }

    [Fact]
    public async Task RunStreamCopy_EmptyMapArgs_Throws()
    {
        var sut = new FfmpegService("/nonexistent/ffmpeg");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.RunStreamCopyAsync("/tmp/i.mkv", "/tmp/o.mkv", Array.Empty<string>(), default));
    }

    [Fact]
    public async Task RunStreamCopy_OutputAlreadyExists_Throws()
    {
        using var tmp = new TempDir();
        var output = Path.Combine(tmp.Path, "o.mkv");
        File.WriteAllText(output, "existing");

        var sut = new FfmpegService("/nonexistent/ffmpeg");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.RunStreamCopyAsync(
                Path.Combine(tmp.Path, "i.mkv"),
                output,
                new[] { "-map", "0:a" },
                default));
    }

    [Fact]
    public async Task RunStreamCopy_MissingBinary_ReportsNotFound()
    {
        using var tmp = new TempDir();
        var sut = new FfmpegService("/nonexistent/ffmpeg-does-not-exist");

        var ex = await Assert.ThrowsAsync<FfmpegFailedException>(() =>
            sut.RunStreamCopyAsync(
                Path.Combine(tmp.Path, "i.mkv"),
                Path.Combine(tmp.Path, "o.mkv"),
                new[] { "-map", "0:a" },
                default));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
