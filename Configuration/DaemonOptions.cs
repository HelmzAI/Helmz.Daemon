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
    public string DefaultModel { get; set; } = "claude-opus-4-6";

    /// <summary>Maximum tokens per API response.</summary>
    public int MaxTokens { get; set; } = 16384;

    /// <summary>Anthropic API base URL.</summary>
    public Uri ApiBaseUrl { get; set; } = new("https://api.anthropic.com");

    /// <summary>Optional system prompt override.</summary>
    public string? SystemPrompt { get; set; }

    /// <summary>
    /// Default thinking budget in tokens. 0 = thinking disabled.
    /// Can be overridden per-session via StartSessionRequest.
    /// </summary>
    public int DefaultThinkingBudgetTokens { get; set; }

    /// <summary>
    /// Input token threshold that triggers server-side context compaction.
    /// 0 = compaction disabled. Must be >= 50000 when enabled.
    /// Default: 100000. Claude Code uses ~167K for 200K context models.
    /// </summary>
    public int CompactionTriggerTokens { get; set; } = 100_000;
}
