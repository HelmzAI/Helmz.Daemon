using System.Collections.Concurrent;
using Helmz.Daemon.Tools;
using Helmz.Spec.V1;

namespace Helmz.Daemon.Sessions;

/// <summary>
/// Represents an active agent session. Holds conversation history,
/// state machine, pending approval actions, and the tool registry.
/// </summary>
internal sealed class AgentSession : IDisposable
{
    private readonly object _stateLock = new();
    private bool _disposed;

    public AgentSession(
        string sessionId,
        AgentProvider provider,
        string workingDirectory,
        ToolRegistry toolRegistry)
    {
        SessionId = sessionId;
        Provider = provider;
        WorkingDirectory = workingDirectory;
        ToolRegistry = toolRegistry;
        CreatedAt = DateTimeOffset.UtcNow;
        State = SessionState.Starting;
        ConversationHistory = [];
        PendingActions = new ConcurrentDictionary<string, TaskCompletionSource<ActionDecision>>(StringComparer.Ordinal);
        CancellationTokenSource = new CancellationTokenSource();
        ApproveAll = false;
    }

    public string SessionId { get; }
    public AgentProvider Provider { get; }
    public string WorkingDirectory { get; }
    public ToolRegistry ToolRegistry { get; }
    public DateTimeOffset CreatedAt { get; }
    public SessionState State { get; private set; }

    /// <summary>Full conversation history for the Anthropic API (alternating user/assistant messages).</summary>
    public List<object> ConversationHistory { get; }

    /// <summary>Pending tool-use actions awaiting user approval. Key = action_id.</summary>
    public ConcurrentDictionary<string, TaskCompletionSource<ActionDecision>> PendingActions { get; }

    /// <summary>Cancellation for this session.</summary>
    public CancellationTokenSource CancellationTokenSource { get; }

    /// <summary>Whether the user has approved all future actions in this session.</summary>
    public bool ApproveAll { get; set; }

    /// <summary>
    /// Transition to a new state. Validates the transition is legal.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the transition is not allowed.</exception>
    public void TransitionTo(SessionState newState)
    {
        lock (_stateLock)
        {
            if (!IsValidTransition(State, newState))
            {
                throw new InvalidOperationException(
                    $"Invalid session state transition: {State} → {newState}");
            }

            State = newState;
        }
    }

    /// <summary>
    /// Try to transition to a new state. Returns false if the transition is not valid.
    /// </summary>
    public bool TryTransitionTo(SessionState newState)
    {
        lock (_stateLock)
        {
            if (!IsValidTransition(State, newState))
            {
                return false;
            }

            State = newState;
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Cancel any pending approvals
        foreach (var (_, tcs) in PendingActions)
        {
            tcs.TrySetCanceled();
        }

        PendingActions.Clear();
        CancellationTokenSource.Cancel();
        CancellationTokenSource.Dispose();
    }

    /// <summary>
    /// State machine transition validation.
    /// Starting  → Running, Failed
    /// Running   → WaitingForInput, WaitingForApproval, Completed, Failed
    /// WaitingForInput    → Running (SendCommand)
    /// WaitingForApproval → Running (RespondToAction)
    /// Completed / Failed → terminal
    /// </summary>
    private static bool IsValidTransition(SessionState from, SessionState to)
    {
        return from switch
        {
            SessionState.Starting => to is SessionState.Running or SessionState.Failed,
            SessionState.Running => to is SessionState.WaitingForInput
                or SessionState.WaitingForApproval
                or SessionState.Completed
                or SessionState.Failed,
            SessionState.WaitingForInput => to is SessionState.Running or SessionState.Failed,
            SessionState.WaitingForApproval => to is SessionState.Running or SessionState.Failed,
            _ => false, // Completed and Failed are terminal
        };
    }
}
