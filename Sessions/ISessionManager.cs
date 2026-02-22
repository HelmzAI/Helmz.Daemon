using Helmz.Spec.V1;

namespace Helmz.Daemon.Sessions;

/// <summary>
/// Manages the lifecycle of agent sessions.
/// </summary>
internal interface ISessionManager
{
    /// <summary>Start a new agent session.</summary>
    Task<AgentSession> StartSessionAsync(
        AgentProvider provider, string workingDirectory, string initialPrompt,
        int? thinkingBudgetTokens, string? model, CancellationToken cancellationToken);

    /// <summary>Stop a running session.</summary>
    Task StopSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Interrupt the current turn without killing the session.
    /// Cancels any in-progress API call, tool execution, or pending approval
    /// and transitions the session to WaitingForInput.
    /// </summary>
    Task InterruptTurnAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>List all active sessions.</summary>
    IReadOnlyList<AgentSession> ListSessions();

    /// <summary>Get a session by ID. Returns null if not found.</summary>
    AgentSession? GetSession(string sessionId);

    /// <summary>Send a new command/prompt to a session that's waiting for input.</summary>
    Task SendCommandAsync(string sessionId, string command, string? model, int? thinkingBudgetTokens, CancellationToken cancellationToken);

    /// <summary>Respond to a pending action request (approve/reject).</summary>
    Task RespondToActionAsync(string sessionId, string actionId, ActionDecision decision, CancellationToken cancellationToken);
}
