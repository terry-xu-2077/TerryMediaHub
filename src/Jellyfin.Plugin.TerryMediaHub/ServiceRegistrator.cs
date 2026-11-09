using Jellyfin.Plugin.TerryMediaHub.Library;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.TerryMediaHub;

/// <summary>把插件自己的服务注册进 Jellyfin 的 DI 容器。</summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<LibraryRegistrar>();
        serviceCollection.AddSingleton<RatingEnricher>();
        serviceCollection.AddSingleton<IHostedService>(services => services.GetRequiredService<RatingEnricher>());
    }
}
