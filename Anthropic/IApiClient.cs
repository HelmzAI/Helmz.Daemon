namespace Helmz.Daemon.Anthropic;

/// <summary>
/// Abstraction for talking to the Anthropic Messages API.
/// Implementations differ by auth method:
/// - ClaudeCodeApi: subscription/setup-token auth (Claude Code backend)
/// - AnthropicApi: direct API key auth (future)
/// </summary>
internal interface IApiClient
{
    /// <summary>Whether auth credentials are available.</summary>
    bool HasCredentials { get; }

    /// <summary>Send a non-streaming message to the API.</summary>
    Task<MessageResponse> SendMessageAsync(MessageRequest request, CancellationToken cancellationToken);

    /// <summary>Stream a message from the API via SSE.</summary>
    IAsyncEnumerable<SseEvent> StreamMessageAsync(MessageRequest request, CancellationToken cancellationToken);
}
