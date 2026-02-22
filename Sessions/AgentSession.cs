using System.Collections.Concurrent;
using Helmz.Daemon.Tools;
using Helmz.Spec.V1;

namespace Helmz.Daemon.Sessions;

/// <summary>
/// Represents an active agent session. Holds conversation history,
/// state machine, pending approval actions, and the tool registry.
/// </summary>
#pragma warning disable IDE0290 // Use primary constructor — this class has mutable state (_stateLock, _disposed) requiring traditional fields
internal sealed class AgentSession : IDisposable
{
    private readonly Lock _stateLock = new();
    private bool _disposed;

    public AgentSession(
        string sessionId,
        AgentProvider provider,
        string workingDirectory,
        ToolRegistry toolRegistry,
        int? thinkingBudgetTokens = null,
        string? model = null)
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
        ThinkingBudgetTokens = thinkingBudgetTokens;
        Model = model;
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
    /// The action request currently waiting for approval, or null if none.
    /// Stored here so DaemonServiceImpl.StreamActions can replay it
    /// to clients that subscribe after the event was first published to the bus.
    /// Cleared by AgentLoop once a decision is received.
    /// </summary>
    public ActionRequest? CurrentPendingAction { get; set; }

    /// <summary>
    /// Per-turn CancellationTokenSource, linked to the session CTS.
    /// Reset at the start of each turn via <see cref="ResetTurnCts"/>.
    /// Cancelled on <see cref="InterruptCurrentTurn"/> without killing the session.
    /// </summary>
    public CancellationTokenSource? TurnCts { get; private set; }

    /// <summary>
    /// Thinking budget in tokens. Null means thinking is disabled for this session.
    /// Each agent provider maps this to its native API format.
    /// </summary>
    public int? ThinkingBudgetTokens { get; set; }

    /// <summary>
    /// The model to use for API requests. Null = use global default (DaemonOptions.DefaultModel).
    /// Mutable — can be changed mid-session via SendCommand.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>Messages queued while the agent is running. Dequeued automatically after each turn.</summary>
    public ConcurrentQueue<string> MessageQueue { get; } = new();

    /// <summary>Create a new turn-level CTS linked to the session CTS.</summary>
    public CancellationTokenSource ResetTurnCts()
    {
        TurnCts?.Dispose();
        TurnCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationTokenSource.Token);
        return TurnCts;
    }

    /// <summary>Cancel the current turn without killing the session.</summary>
    public void InterruptCurrentTurn()
    {
        // Cancel pending approvals so the TCS unblocks
        foreach ((string _, TaskCompletionSource<ActionDecision>? tcs) in PendingActions)
        {
            _ = tcs.TrySetCanceled();
        }

        PendingActions.Clear();
        CurrentPendingAction = null;

        // Cancel the turn-level token
        TurnCts?.Cancel();
    }

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
        foreach ((string _, TaskCompletionSource<ActionDecision>? tcs) in PendingActions)
        {
            _ = tcs.TrySetCanceled();
        }

        PendingActions.Clear();
        TurnCts?.Cancel();
        TurnCts?.Dispose();
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
            SessionState.Unspecified
                or SessionState.Completed
                or SessionState.Failed
                or _ => false, // Completed, Failed, and Unspecified are terminal
        };
    }
}
#pragma warning restore IDE0290
