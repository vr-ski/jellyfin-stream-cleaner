using Jellyfin.Plugin.StreamCleaner.Services;
using Jellyfin.Plugin.StreamCleaner.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.StreamCleaner;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton(sp =>
            new JobStore(Plugin.Instance!.DataFolderPath));
        services.AddSingleton<StreamCleanerService>();
        services.AddSingleton<StreamCleanerTask>();
        services.AddSingleton<IScheduledTask>(sp => sp.GetRequiredService<StreamCleanerTask>());
    }
}
