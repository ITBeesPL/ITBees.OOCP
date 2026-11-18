using ITBees.OOCP.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ITBees.OOCP.Hosting;

public static class OcppHostingExtensions
{
    /// <summary>
    /// Registers the central system and the host's handler as singletons. The handler is resolved once, so it must be
    /// thread-safe and take scoped services through <c>IServiceScopeFactory</c>.
    /// </summary>
    public static IServiceCollection AddOcppCentralSystem<THandler>(this IServiceCollection services,
        Action<OcppCentralSystemOptions>? configure = null) where THandler : class, IOcppCentralSystemHandler
    {
        var options = new OcppCentralSystemOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<THandler>();
        services.AddSingleton<IOcppCentralSystemHandler>(provider => provider.GetRequiredService<THandler>());
        services.AddSingleton<IOcppCentralSystem, OcppCentralSystem>();
        return services;
    }

    /// <summary>
    /// Serves the OCPP-J endpoint under <paramref name="path"/>: a charger configured with the URL
    /// <c>ws://host:port{path}</c> connects to <c>ws://host:port{path}/{chargePointId}</c>. The branch enables
    /// WebSockets for itself only (like SignalR), the rest of the pipeline is not touched. Put it before
    /// middleware that would answer the path otherwise (static files, fallbacks).
    /// </summary>
    public static IApplicationBuilder UseOcppCentralSystem(this IApplicationBuilder app, PathString path)
    {
        var centralSystem = app.ApplicationServices.GetRequiredService<IOcppCentralSystem>();
        app.Map(path, branch =>
        {
            branch.UseWebSockets();
            branch.Run(context =>
            {
                // The last path segment is the identity: ".../ocpp/CP01", ".../ocpp/CP01/" and ".../ocpp/site/CP01"
                // (a charger that appends its id to a URL with an extra segment) all give "CP01".
                var remainder = context.Request.Path.Value ?? string.Empty;
                var lastSegment = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
                return centralSystem.HandleWebSocketRequest(context, Uri.UnescapeDataString(lastSegment));
            });
        });
        return app;
    }
}
