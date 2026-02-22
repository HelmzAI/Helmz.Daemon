using System.Runtime.InteropServices;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Helmz.Daemon.Sessions;
using Helmz.Daemon.Streaming;
using Helmz.Spec.V1;

namespace Helmz.Daemon.Services;

/// <summary>
/// gRPC implementation of DaemonService.
/// Delegates all operations to ISessionManager + SessionEventBus.
/// For local dev testing — in production, these RPCs are tunneled via relay.
/// </summary>
internal sealed class DaemonServiceImpl(ISessionManager sessionManager, SessionEventBus eventBus)
    : DaemonService.DaemonServiceBase
{
    private static readonly string Version = typeof(DaemonServiceImpl).Assembly
        .GetName().Version?.ToString() ?? "0.0.1";

    private readonly ISessionManager _sessionManager = sessionManager;
    private readonly SessionEventBus _eventBus = eventBus;

    public override Task<GetDaemonInfoResponse> GetDaemonInfo(
        GetDaemonInfoRequest request, ServerCallContext context)
    {
        GetDaemonInfoResponse response = new()
        {
            Version = Version,
            Os = RuntimeInformation.OSDescription,
            Hostname = Environment.MachineName,
        };
        response.AvailableProviders.Add(AgentProvider.ClaudeCode);

        return Task.FromResult(response);
    }

    public override async Task<StartSessionResponse> StartSession(
        StartSessionRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.WorkingDirectory))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "working_directory is required."));
        }

        try
        {
            // Map proto fields to nullable values
            int? thinkingBudget = request.Thinking?.BudgetTokens;
            string? model = string.IsNullOrEmpty(request.Model) ? null : request.Model;

            AgentSession session = await _sessionManager.StartSessionAsync(
                request.Provider,
                request.WorkingDirectory,
                request.InitialPrompt,
                thinkingBudget,
                model,
                context.CancellationToken).ConfigureAwait(false);

            return new StartSessionResponse
            {
                Session = ToSessionInfo(session),
            };
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<StopSessionResponse> StopSession(
        StopSessionRequest request, ServerCallContext context)
    {
        try
        {
            await _sessionManager.StopSessionAsync(request.SessionId, context.CancellationToken).ConfigureAwait(false);
            return new StopSessionResponse();
        }
        catch (KeyNotFoundException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Session not found: {request.SessionId}"));
        }
    }

    public override Task<ListSessionsResponse> ListSessions(
        ListSessionsRequest request, ServerCallContext context)
    {
        IReadOnlyList<AgentSession> sessions = _sessionManager.ListSessions();
        ListSessionsResponse response = new();
        response.Sessions.AddRange(sessions.Select(ToSessionInfo));
        return Task.FromResult(response);
    }

    public override async Task<SendCommandResponse> SendCommand(
        SendCommandRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.Command))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "command is required."));
        }

        try
        {
            string? model = string.IsNullOrEmpty(request.Model) ? null : request.Model;
            int? thinkingBudget = request.Thinking?.BudgetTokens;

            await _sessionManager.SendCommandAsync(
                request.SessionId, request.Command,
                model, thinkingBudget,
                context.CancellationToken).ConfigureAwait(false);
            return new SendCommandResponse();
        }
        catch (KeyNotFoundException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Session not found: {request.SessionId}"));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override async Task StreamOutput(
        StreamOutputRequest request,
        IServerStreamWriter<StreamOutputResponse> responseStream,
        ServerCallContext context)
    {
        using EventSubscription<OutputChunk> subscription = _eventBus.SubscribeOutput(request.SessionId);

        try
        {
            await foreach (OutputChunk? chunk in subscription.Reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(
                    new StreamOutputResponse { Chunk = chunk },
                    context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal for streaming RPCs
        }
        // subscription.Dispose() auto-unsubscribes from the EventBus
    }

    public override async Task StreamActions(
        StreamActionsRequest request,
        IServerStreamWriter<StreamActionsResponse> responseStream,
        ServerCallContext context)
    {
        // Subscribe first, then check for a missed action — this ordering ensures
        // we cannot miss an action that arrives between the two steps.
        using EventSubscription<ActionRequest> subscription = _eventBus.SubscribeActions(request.SessionId);

        // Replay any action that was published before this subscriber connected.
        AgentSession? session = _sessionManager.GetSession(request.SessionId);
        if (session?.CurrentPendingAction is { } missed)
        {
            await responseStream.WriteAsync(
                new StreamActionsResponse { Action = missed },
                context.CancellationToken).ConfigureAwait(false);
        }

        try
        {
            await foreach (ActionRequest? action in subscription.Reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(
                    new StreamActionsResponse { Action = action },
                    context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal for streaming RPCs
        }
        // subscription.Dispose() auto-unsubscribes from the EventBus
    }

    public override async Task<RespondToActionResponse> RespondToAction(
        RespondToActionRequest request, ServerCallContext context)
    {
        if (request.ActionResponse is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "action_response is required."));
        }

        try
        {
            await _sessionManager.RespondToActionAsync(
                request.ActionResponse.SessionId,
                request.ActionResponse.ActionId,
                request.ActionResponse.Decision,
                context.CancellationToken).ConfigureAwait(false);
            return new RespondToActionResponse();
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
    }

    public override Task<InterruptTurnResponse> InterruptTurn(
        InterruptTurnRequest request, ServerCallContext context)
    {
        try
        {
            _ = _sessionManager.InterruptTurnAsync(request.SessionId, context.CancellationToken);
            return Task.FromResult(new InterruptTurnResponse());
        }
        catch (KeyNotFoundException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Session not found: {request.SessionId}"));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override Task<GetSessionStatusResponse> GetSessionStatus(
        GetSessionStatusRequest request, ServerCallContext context)
    {
        AgentSession session = _sessionManager.GetSession(request.SessionId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"Session not found: {request.SessionId}"));

        return Task.FromResult(new GetSessionStatusResponse
        {
            Session = ToSessionInfo(session),
        });
    }

    private static SessionInfo ToSessionInfo(AgentSession session)
    {
        return new SessionInfo
        {
            SessionId = session.SessionId,
            Provider = session.Provider,
            State = session.State,
            CreatedAt = Timestamp.FromDateTimeOffset(session.CreatedAt),
            WorkingDirectory = session.WorkingDirectory,
        };
    }
}
