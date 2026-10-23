using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamCleaner.Models;
using Jellyfin.Plugin.StreamCleaner.Services;
using StreamCleaner.Tests.Helpers;
using Xunit;

namespace StreamCleaner.Tests.Services;

public sealed class JobStoreTests
{
    private static JobState SampleState() => new()
    {
        Id = Guid.NewGuid(),
        Request = new JobRequest
        {
            InputPath = "/data/x.mkv",
            Selection = new StreamSelectionRequest { AudioLanguages = new[] { "eng" } },
            DryRun = true
        },
        Status = JobStatus.Pending,
        RequestedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Load_NoFile_ReturnsNull()
    {
        using var tmp = new TempDir();
        var store = new JobStore(tmp.Path);
        Assert.Null(await store.LoadAsync(default));
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTrips()
    {
        using var tmp = new TempDir();
        var store = new JobStore(tmp.Path);
        var state = SampleState();

        await store.SaveAsync(state, default);
        var loaded = await store.LoadAsync(default);

        Assert.NotNull(loaded);
        Assert.Equal(state.Id, loaded!.Id);
        Assert.Equal(state.Status, loaded.Status);
        Assert.Equal("/data/x.mkv", loaded.Request.InputPath);
        Assert.Single(loaded.Request.Selection.AudioLanguages);
    }

    [Fact]
    public async Task Save_OverwritesPrevious()
    {
        using var tmp = new TempDir();
        var store = new JobStore(tmp.Path);

        var first = SampleState();
        await store.SaveAsync(first, default);

        var second = SampleState();
        second.Status = JobStatus.Succeeded;
        await store.SaveAsync(second, default);

        var loaded = await store.LoadAsync(default);
        Assert.Equal(second.Id, loaded!.Id);
        Assert.Equal(JobStatus.Succeeded, loaded.Status);
    }

    [Fact]
    public async Task Clear_RemovesFile()
    {
        using var tmp = new TempDir();
        var store = new JobStore(tmp.Path);
        await store.SaveAsync(SampleState(), default);

        await store.ClearAsync(default);

        Assert.Null(await store.LoadAsync(default));
    }

    [Fact]
    public async Task Clear_NoFile_DoesNotThrow()
    {
        using var tmp = new TempDir();
        var store = new JobStore(tmp.Path);
        await store.ClearAsync(default); // must not throw
    }

    [Fact]
    public async Task ConcurrentSaves_DoNotCorrupt()
    {
        using var tmp = new TempDir();
        var store = new JobStore(tmp.Path);

        var tasks = new Task[10];
        for (var i = 0; i < tasks.Length; i++)
        {
            var idx = i;
            tasks[i] = Task.Run(async () =>
            {
                var s = SampleState();
                s.Results.Add(new PerFileResult { Path = $"/f/{idx}.mkv", Outcome = FileOutcome.Succeeded });
                await store.SaveAsync(s, default);
            });
        }
        await Task.WhenAll(tasks);

        // Whatever won the race, the file must still parse
        Assert.NotNull(await store.LoadAsync(default));
    }
}
