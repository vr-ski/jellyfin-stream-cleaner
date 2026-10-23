using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StreamCleaner;

/// <summary>Persistent plugin configuration.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        TemporaryDirectory = string.Empty;
    }

    /// <summary>Gets or sets the working directory for intermediate FFmpeg output. Empty means system temp.</summary>
    public string TemporaryDirectory { get; set; }
}
