namespace Helmz.Daemon.Configuration;

/// <summary>
/// Configuration options for the Helmz daemon.
/// </summary>
internal sealed class DaemonOptions
{
    public const string SectionName = "Daemon";

    /// <summary>Port for the local gRPC dev server.</summary>
    public int GrpcPort { get; set; } = 50051;

    /// <summary>Maximum number of concurrent agent sessions.</summary>
    public int MaxConcurrentSessions { get; set; } = 5;

    /// <summary>Default Claude model to use.</summary>
    public string DefaultModel { get; set; } = "claude-sonnet-4-6";

    /// <summary>Maximum tokens per API response.</summary>
    public int MaxTokens { get; set; } = 16384;

    /// <summary>Anthropic API base URL.</summary>
    public Uri ApiBaseUrl { get; set; } = new("https://api.anthropic.com");

    /// <summary>Optional system prompt override.</summary>
    public string? SystemPrompt { get; set; }
}
