using Helmz.Daemon.Anthropic;
using Helmz.Daemon.Configuration;
using Helmz.Daemon.Sessions;
using Helmz.Daemon.Streaming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helmz.Daemon.Extensions;

/// <summary>
/// DI registration for the Helmz daemon services.
/// </summary>
internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHelmzDaemon(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Configuration
        services.Configure<DaemonOptions>(configuration.GetSection(DaemonOptions.SectionName));

        // HTTP client factory for Anthropic API
        services.AddHttpClient("anthropic");

        // API client — ClaudeCodeApi (subscription/setup-token auth)
        // Future: add AnthropicApi for direct API key auth
        services.AddSingleton<IApiClient, ClaudeCodeApi>();
        services.AddSingleton<SessionEventBus>();
        services.AddSingleton<IAgentLoop, AgentLoop>();
        services.AddSingleton<ISessionManager, SessionManager>();

        return services;
    }
}
