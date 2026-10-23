using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.StreamCleaner.Models;
using Jellyfin.Plugin.StreamCleaner.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.StreamCleaner;

/// <summary>Plugin entry point.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        RecoverInterruptedJob();
    }

    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("2feb1d37-2bc3-4828-9cf9-11c7a094820a");

    /// <inheritdoc />
    public override string Name => "Stream Cleaner";

    /// <inheritdoc />
    public override string Description =>
        "Select and remux audio/subtitle tracks without re-encoding. " +
        "Stream-copy only, with verified replacement and recoverable backups.";

    /// <inheritdoc />
    public override string ConfigurationFileName => "Jellyfin.Plugin.StreamCleaner.xml";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html",
            EnableInMainMenu = true,
            MenuSection = "Plugin Settings",
            MenuIcon = "cleaning_services"
        };
    }

    /// <summary>
    /// On plugin load, mark any Pending or Running job as Interrupted. This
    /// is the only place state transitions of this kind happen; the status
    /// endpoint must never mutate state, or a running job would be corrupted
    /// on every poll.
    /// </summary>
    private void RecoverInterruptedJob()
    {
        try
        {
            var store = new JobStore(DataFolderPath);
            var state = store.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (state is null) return;

            if (state.Status is JobStatus.Pending or JobStatus.Running)
            {
                state.Status = JobStatus.Interrupted;
                state.ErrorMessage =
                    "Server restarted before the job completed. Review staging and backup files, then resubmit.";
                state.FinishedAt = DateTime.UtcNow;
                store.SaveAsync(state, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // Never block plugin load on recovery failure.
        }
    }
}
