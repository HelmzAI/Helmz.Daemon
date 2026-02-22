using Helmz.Daemon.Anthropic;
using Helmz.Daemon.Configuration;
using Helmz.Daemon.Sessions;
using Helmz.Daemon.Streaming;

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
        _ = services.Configure<DaemonOptions>(configuration.GetSection(DaemonOptions.SectionName));

        // HTTP client factory for Anthropic API
        _ = services.AddHttpClient("anthropic");

        // API client — ClaudeCodeApi (subscription/setup-token auth)
        // Future: add AnthropicApi for direct API key auth
        _ = services.AddSingleton<IApiClient, ClaudeCodeApi>();
        _ = services.AddSingleton<SessionEventBus>();
        _ = services.AddSingleton<IAgentLoop, AgentLoop>();
        _ = services.AddSingleton<ISessionManager, SessionManager>();

        return services;
    }
}
