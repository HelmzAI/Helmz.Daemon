using System.Collections.Concurrent;
using Helmz.Daemon.Anthropic;
using Helmz.Daemon.Configuration;
using Helmz.Daemon.Streaming;
using Helmz.Daemon.Tools;
using Helmz.Spec.V1;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helmz.Daemon.Sessions;

/// <summary>
/// Manages the lifecycle of agent sessions using a ConcurrentDictionary.
/// Delegates agent loop execution to IAgentLoop.
/// </summary>
internal sealed partial class SessionManager : ISessionManager, IDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly IAgentLoop _agentLoop;
    private readonly SessionEventBus _eventBus;
    private readonly DaemonOptions _options;
    private readonly ILogger<SessionManager> _logger;
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

    public SessionManager(
        IAgentLoop agentLoop,
        SessionEventBus eventBus,
        IOptions<DaemonOptions> options,
        ILogger<SessionManager> logger)
    {
        _agentLoop = agentLoop;
        _eventBus = eventBus;
        _options = options.Value;
        _logger = logger;
    }

    public Task<AgentSession> StartSessionAsync(
        AgentProvider provider, string workingDirectory, string initialPrompt, CancellationToken cancellationToken)
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

        var sessionId = Guid.NewGuid().ToString("N");

        // Create tool registry with built-in tools
        var toolRegistry = new ToolRegistry();
        foreach (var tool in BuiltInTools)
        {
            toolRegistry.Register(tool);
        }

        var session = new AgentSession(sessionId, provider, workingDirectory, toolRegistry);

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
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        LogSessionStopped(_logger, sessionId);

        session.TryTransitionTo(SessionState.Failed);
        session.Dispose();
        _eventBus.CompleteSession(sessionId);

        return Task.CompletedTask;
    }

    public IReadOnlyList<AgentSession> ListSessions()
    {
        return _sessions.Values.ToList();
    }

    public AgentSession? GetSession(string sessionId)
    {
        return _sessions.GetValueOrDefault(sessionId);
    }

    public Task SendCommandAsync(string sessionId, string command, CancellationToken cancellationToken)
    {
        var session = _sessions.GetValueOrDefault(sessionId)
            ?? throw new KeyNotFoundException($"Session not found: {sessionId}");

        if (session.State != SessionState.WaitingForInput)
        {
            throw new InvalidOperationException(
                $"Session {sessionId} is not waiting for input (current state: {session.State}).");
        }

        LogCommandSent(_logger, sessionId, Truncate(command, 100));

        // Launch a new iteration of the agent loop in the background
        _ = RunAgentLoopInBackgroundAsync(session, command);

        return Task.CompletedTask;
    }

    public Task RespondToActionAsync(string sessionId, string actionId, ActionDecision decision, CancellationToken cancellationToken)
    {
        var session = _sessions.GetValueOrDefault(sessionId)
            ?? throw new KeyNotFoundException($"Session not found: {sessionId}");

        if (!session.PendingActions.TryGetValue(actionId, out var tcs))
        {
            throw new KeyNotFoundException($"Action not found: {actionId}");
        }

        LogActionResponse(_logger, sessionId, actionId, decision.ToString());

        tcs.TrySetResult(decision);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var (id, session) in _sessions)
        {
            session.Dispose();
            _eventBus.CompleteSession(id);
        }

        _sessions.Clear();
    }

    private async Task RunAgentLoopInBackgroundAsync(AgentSession session, string prompt)
    {
        try
        {
            await _agentLoop.RunAsync(session, prompt, session.CancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogSessionCancelled(_logger, session.SessionId);
        }
        catch (HttpRequestException ex)
        {
            LogSessionError(_logger, session.SessionId, ex.Message);
            session.TryTransitionTo(SessionState.Failed);
        }
        catch (InvalidOperationException ex)
        {
            LogSessionError(_logger, session.SessionId, ex.Message);
            session.TryTransitionTo(SessionState.Failed);
        }
        catch (TimeoutException ex)
        {
            LogSessionError(_logger, session.SessionId, ex.Message);
            session.TryTransitionTo(SessionState.Failed);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Session {SessionId} error: {ErrorMessage}")]
    private static partial void LogSessionError(ILogger logger, string sessionId, string errorMessage);
}
