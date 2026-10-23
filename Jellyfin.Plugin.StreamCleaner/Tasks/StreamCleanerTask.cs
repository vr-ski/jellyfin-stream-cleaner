using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.StreamCleaner.Models;
using Jellyfin.Plugin.StreamCleaner.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.StreamCleaner.Tasks;

/// <summary>Scheduled task that runs the pending Stream Cleaner job.</summary>
public sealed class StreamCleanerTask : IScheduledTask
{
    private readonly StreamCleanerService _service;

    public StreamCleanerTask(StreamCleanerService service) => _service = service;

    /// <inheritdoc />
    public string Name => "Stream Cleaner: run pending job";

    /// <inheritdoc />
    public string Key => "StreamCleanerRun";

    /// <inheritdoc />
    public string Description => "Runs the pending Stream Cleaner job if one is queued.";

    /// <inheritdoc />
    public string Category => "Stream Cleaner";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var state = await _service.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is null || state.Status != JobStatus.Pending)
        {
            progress.Report(1);
            return;
        }

        await _service.ExecutePendingAsync(progress, cancellationToken).ConfigureAwait(false);
    }
}
