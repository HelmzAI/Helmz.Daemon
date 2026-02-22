using System.Collections.Concurrent;
using Helmz.Daemon.Anthropic;
using Helmz.Daemon.Configuration;
using Helmz.Daemon.Streaming;
using Helmz.Daemon.Tools;
using Helmz.Spec.V1;
using Microsoft.Extensions.Options;

namespace Helmz.Daemon.Sessions;

/// <summary>
/// Manages the lifecycle of agent sessions using a ConcurrentDictionary.
/// Delegates agent loop execution to IAgentLoop.
/// </summary>
internal sealed partial class SessionManager(
    IAgentLoop agentLoop,
    SessionEventBus eventBus,
    IOptions<DaemonOptions> options,
    ILogger<SessionManager> logger) : ISessionManager, IDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly IAgentLoop _agentLoop = agentLoop;
    private readonly SessionEventBus _eventBus = eventBus;
    private readonly DaemonOptions _options = options.Value;
    private readonly ILogger<SessionManager> _logger = logger;
    private bool _disposed;

    // Built-in tools to register on each session
    private static readonly ITool[] BuiltInTools =
    [
        new ReadFileTool(),
        new WriteFileTool(),
        new EditFileTool(),
        new BashTool(),
        new GlobTool(),
        new GrepTool(),
    ];

    public Task<AgentSession> StartSessionAsync(
        AgentProvider provider, string workingDirectory, string initialPrompt,
        int? thinkingBudgetTokens, string? model, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_sessions.Count >= _options.MaxConcurrentSessions)
        {
            throw new InvalidOperationException(
                $"Maximum concurrent sessions ({_options.MaxConcurrentSessions}) reached.");
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new ArgumentException($"Working directory does not exist: {workingDirectory}", nameof(workingDirectory));
        }

        string sessionId = Guid.NewGuid().ToString("N");

        // Create tool registry with built-in tools
        ToolRegistry toolRegistry = new();
        foreach (ITool tool in BuiltInTools)
        {
            toolRegistry.Register(tool);
        }

        // Resolve thinking budget: request > global default > null (disabled)
        int? resolvedBudget = thinkingBudgetTokens
            ?? (_options.DefaultThinkingBudgetTokens > 0 ? _options.DefaultThinkingBudgetTokens : null);

        AgentSession session = new(sessionId, provider, workingDirectory, toolRegistry, resolvedBudget, model);

        if (!_sessions.TryAdd(sessionId, session))
        {
            session.Dispose();
            throw new InvalidOperationException("Failed to create session (ID collision).");
        }

        LogSessionStarted(_logger, sessionId, provider.ToString(), workingDirectory);

        // Launch the agent loop in the background
        _ = RunAgentLoopInBackgroundAsync(session, initialPrompt);

        return Task.FromResult(session);
    }

    public Task StopSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(sessionId, out AgentSession? session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        LogSessionStopped(_logger, sessionId);

        _ = session.TryTransitionTo(SessionState.Failed);
        session.Dispose();
        _eventBus.CompleteSession(sessionId);

        return Task.CompletedTask;
    }

    public IReadOnlyList<AgentSession> ListSessions()
    {
        return [.. _sessions.Values];
    }

    public AgentSession? GetSession(string sessionId)
    {
        return _sessions.GetValueOrDefault(sessionId);
    }

    public Task SendCommandAsync(string sessionId, string command, string? model, int? thinkingBudgetTokens, CancellationToken cancellationToken)
    {
        AgentSession session = _sessions.GetValueOrDefault(sessionId)
            ?? throw new KeyNotFoundException($"Session not found: {sessionId}");

        // Apply config overrides before the next turn
        if (!string.IsNullOrEmpty(model))
        {
            session.Model = model;
        }

        if (thinkingBudgetTokens.HasValue)
        {
            session.ThinkingBudgetTokens = thinkingBudgetTokens.Value > 0
                ? thinkingBudgetTokens.Value
                : null; // 0 = disable thinking
        }

        if (session.State == SessionState.WaitingForInput)
        {
            // Session is idle — start a new turn immediately
            LogCommandSent(_logger, sessionId, Truncate(command, 100));
            _ = RunAgentLoopInBackgroundAsync(session, command);
            return Task.CompletedTask;
        }

        if (session.State == SessionState.Running)
        {
            // Session is busy — queue the message for automatic pickup after the current turn
            session.MessageQueue.Enqueue(command);
            LogCommandQueued(_logger, sessionId, Truncate(command, 100));
            return Task.CompletedTask;
        }

        // WaitingForApproval, Failed, Completed → reject
        throw new InvalidOperationException(
            $"Session {sessionId} cannot accept commands (current state: {session.State}).");
    }

    public Task InterruptTurnAsync(string sessionId, CancellationToken cancellationToken)
    {
        AgentSession session = _sessions.GetValueOrDefault(sessionId)
            ?? throw new KeyNotFoundException($"Session not found: {sessionId}");

        if (session.State is not (SessionState.Running or SessionState.WaitingForApproval))
        {
            throw new InvalidOperationException(
                $"Session {sessionId} has no active turn to interrupt (current state: {session.State}).");
        }

        LogTurnInterrupted(_logger, sessionId);
        session.InterruptCurrentTurn();
        return Task.CompletedTask;
    }

    public Task RespondToActionAsync(string sessionId, string actionId, ActionDecision decision, CancellationToken cancellationToken)
    {
        AgentSession session = _sessions.GetValueOrDefault(sessionId)
            ?? throw new KeyNotFoundException($"Session not found: {sessionId}");

        if (!session.PendingActions.TryGetValue(actionId, out TaskCompletionSource<ActionDecision>? tcs))
        {
            throw new KeyNotFoundException($"Action not found: {actionId}");
        }

        LogActionResponse(_logger, sessionId, actionId, decision.ToString());

        _ = tcs.TrySetResult(decision);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach ((string? id, AgentSession? session) in _sessions)
        {
            session.Dispose();
            _eventBus.CompleteSession(id);
        }

        _sessions.Clear();
    }

    private async Task RunAgentLoopInBackgroundAsync(AgentSession session, string prompt)
    {
        string currentPrompt = prompt;

        while (true)
        {
            CancellationTokenSource turnCts = session.ResetTurnCts();
            try
            {
                await _agentLoop.RunAsync(session, currentPrompt, turnCts.Token).ConfigureAwait(false);

                // Turn completed — check queue for next message
                if (session.MessageQueue.TryDequeue(out string? next))
                {
                    LogDequeuedCommand(_logger, session.SessionId, Truncate(next, 100));
                    currentPrompt = next;
                    continue; // Process next queued message
                }

                return; // No more queued messages — done (session is already WaitingForInput)
            }
            catch (OperationCanceledException)
            {
                LogSessionCancelled(_logger, session.SessionId);

                // If the session-level CTS is NOT cancelled, this was a turn interrupt
                // (not a full session stop) — transition to WaitingForInput so the user
                // can send a new prompt.
                if (!session.CancellationTokenSource.IsCancellationRequested)
                {
                    _ = session.TryTransitionTo(SessionState.WaitingForInput);
                }

                // Drain the queue — queued messages are stale after an interrupt
                while (session.MessageQueue.TryDequeue(out _)) { }
                return;
            }
            catch (HttpRequestException ex)
            {
                LogSessionError(_logger, session.SessionId, ex.Message);
                await PublishErrorAsync(session.SessionId, ex.Message).ConfigureAwait(false);
                // API errors are recoverable — let the user retry with a new message
                _ = session.TryTransitionTo(SessionState.WaitingForInput);
                while (session.MessageQueue.TryDequeue(out _)) { }
                return;
            }
            catch (InvalidOperationException ex)
            {
                LogSessionError(_logger, session.SessionId, ex.Message);
                await PublishErrorAsync(session.SessionId, ex.Message).ConfigureAwait(false);
                _ = session.TryTransitionTo(SessionState.Failed);
                while (session.MessageQueue.TryDequeue(out _)) { }
                return;
            }
            catch (TimeoutException ex)
            {
                LogSessionError(_logger, session.SessionId, ex.Message);
                await PublishErrorAsync(session.SessionId, ex.Message).ConfigureAwait(false);
                // Timeouts are transient — let the user retry
                _ = session.TryTransitionTo(SessionState.WaitingForInput);
                while (session.MessageQueue.TryDequeue(out _)) { }
                return;
            }
        }
    }

    private async Task PublishErrorAsync(string sessionId, string errorMessage)
    {
        try
        {
            OutputChunk chunk = new()
            {
                SessionId = sessionId,
                Type = OutputType.Stderr,
                Content = $"[error] {errorMessage}",
                Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            };

            await _eventBus.PublishOutputAsync(sessionId, chunk).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort — don't let a publishing failure mask the original error
            LogSessionError(_logger, sessionId, $"Failed to publish error to output stream: {ex.Message}");
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...");
    }

    // --- High-performance logging (CA1848) ---

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} started (provider: {Provider}, dir: {WorkingDirectory})")]
    private static partial void LogSessionStarted(ILogger logger, string sessionId, string provider, string workingDirectory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} stopped")]
    private static partial void LogSessionStopped(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: command sent: {Command}")]
    private static partial void LogCommandSent(ILogger logger, string sessionId, string command);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: action {ActionId} response: {Decision}")]
    private static partial void LogActionResponse(ILogger logger, string sessionId, string actionId, string decision);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} cancelled")]
    private static partial void LogSessionCancelled(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: current turn interrupted")]
    private static partial void LogTurnInterrupted(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: command queued: {Command}")]
    private static partial void LogCommandQueued(ILogger logger, string sessionId, string command);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: dequeued command: {Command}")]
    private static partial void LogDequeuedCommand(ILogger logger, string sessionId, string command);

    [LoggerMessage(Level = LogLevel.Error, Message = "Session {SessionId} error: {ErrorMessage}")]
    private static partial void LogSessionError(ILogger logger, string sessionId, string errorMessage);
}
