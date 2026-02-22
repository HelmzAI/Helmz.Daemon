using System.Text;
using System.Text.Json;
using Helmz.Daemon.Configuration;
using Helmz.Daemon.Sessions;
using Helmz.Daemon.Streaming;
using Helmz.Daemon.Tools;
using Helmz.Spec.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Helmz.Daemon.Anthropic;

/// <summary>
/// The agentic tool-use loop. Sends messages to the Anthropic API,
/// streams the response, executes tools (with approval when required),
/// sends tool results back, and repeats until end_turn or max_tokens.
/// </summary>
internal sealed partial class AgentLoop : IAgentLoop
{
    private const int MaxIterations = 50; // Safety valve

    private readonly IApiClient _client;
    private readonly SessionEventBus _eventBus;
    private readonly DaemonOptions _options;
    private readonly ILogger<AgentLoop> _logger;

    public AgentLoop(
        IApiClient client,
        SessionEventBus eventBus,
        IOptions<DaemonOptions> options,
        ILogger<AgentLoop> logger)
    {
        _client = client;
        _eventBus = eventBus;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(AgentSession session, string userPrompt, CancellationToken cancellationToken)
    {
        session.TransitionTo(SessionState.Running);

        // Add user message to conversation history
        session.ConversationHistory.Add(new ConversationMessage
        {
            Role = "user",
            Content = userPrompt,
        });

        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LogIterationStart(_logger, session.SessionId, iteration + 1);

            // Build the API request
            var request = BuildRequest(session);

            // Collect the full response via SSE streaming
            var (stopReason, contentBlocks, rawJsonBlocks) = await StreamResponseAsync(
                session, request, cancellationToken).ConfigureAwait(false);

            // Add the assistant response to conversation history
            session.ConversationHistory.Add(new ConversationMessage
            {
                Role = "assistant",
                Content = rawJsonBlocks,
            });

            // Handle based on stop reason
            if (stopReason is "end_turn" or "max_tokens")
            {
                LogConversationEnd(_logger, session.SessionId, stopReason);
                session.TransitionTo(SessionState.WaitingForInput);
                return;
            }

            if (stopReason is "tool_use")
            {
                // Execute each tool_use block
                var toolResults = await ExecuteToolsAsync(
                    session, contentBlocks, cancellationToken).ConfigureAwait(false);

                // Add tool results as a user message (Anthropic API convention)
                session.ConversationHistory.Add(new ConversationMessage
                {
                    Role = "user",
                    Content = toolResults,
                });

                // Loop back to send the tool results to the API
                continue;
            }

            // Unknown stop reason
            LogUnknownStopReason(_logger, session.SessionId, stopReason ?? "null");
            session.TransitionTo(SessionState.WaitingForInput);
            return;
        }

        // Max iterations hit
        LogMaxIterations(_logger, session.SessionId, MaxIterations);
        await PublishOutput(session.SessionId, OutputType.Stderr,
            $"Agent loop reached maximum iterations ({MaxIterations}).", cancellationToken).ConfigureAwait(false);
        session.TransitionTo(SessionState.WaitingForInput);
    }

    private MessageRequest BuildRequest(AgentSession session)
    {
        // Convert conversation history to the expected format
        var messages = session.ConversationHistory
            .Cast<ConversationMessage>()
            .ToList();

        return new MessageRequest
        {
            Model = _options.DefaultModel,
            MaxTokens = _options.MaxTokens,
            System = _options.SystemPrompt,
            Messages = messages,
            Tools = session.ToolRegistry.GetToolDefinitions(),
            Stream = true,
        };
    }

    /// <summary>
    /// Stream the API response via SSE, publishing output chunks in real-time.
    /// Returns the stop reason and parsed content blocks.
    /// </summary>
    private async Task<(string? StopReason, List<ContentBlock> Blocks, List<JsonElement> RawBlocks)> StreamResponseAsync(
        AgentSession session,
        MessageRequest request,
        CancellationToken cancellationToken)
    {
        var contentBlocks = new List<ContentBlock>();
        var rawJsonBlocks = new List<JsonElement>();
        string? stopReason = null;

        // SSE streaming accumulators
        var currentBlockType = "";
        var textAccumulator = new StringBuilder();
        var thinkingAccumulator = new StringBuilder();
        var toolInputAccumulator = new StringBuilder();
        string currentToolId = "";
        string currentToolName = "";

        await foreach (var sse in _client.StreamMessageAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (sse.EventType)
            {
                case "message_start":
                    // Initial message metadata — nothing to accumulate
                    break;

                case "content_block_start":
                    // A new content block begins
                    if (sse.Data.TryGetProperty("content_block", out var blockStart))
                    {
                        currentBlockType = blockStart.TryGetProperty("type", out var tp)
                            ? tp.GetString() ?? "" : "";

                        if (currentBlockType is "tool_use")
                        {
                            currentToolId = blockStart.TryGetProperty("id", out var id)
                                ? id.GetString() ?? "" : "";
                            currentToolName = blockStart.TryGetProperty("name", out var name)
                                ? name.GetString() ?? "" : "";
                            toolInputAccumulator.Clear();
                        }
                        else
                        {
                            textAccumulator.Clear();
                            thinkingAccumulator.Clear();
                        }
                    }

                    break;

                case "content_block_delta":
                    // Incremental content
                    if (sse.Data.TryGetProperty("delta", out var delta))
                    {
                        var deltaType = delta.TryGetProperty("type", out var dt)
                            ? dt.GetString() ?? "" : "";

                        switch (deltaType)
                        {
                            case "text_delta":
                                var text = delta.TryGetProperty("text", out var t)
                                    ? t.GetString() ?? "" : "";
                                textAccumulator.Append(text);
                                await PublishOutput(session.SessionId, OutputType.Stdout,
                                    text, cancellationToken).ConfigureAwait(false);
                                break;

                            case "thinking_delta":
                                var thinking = delta.TryGetProperty("thinking", out var th)
                                    ? th.GetString() ?? "" : "";
                                thinkingAccumulator.Append(thinking);
                                await PublishOutput(session.SessionId, OutputType.Thinking,
                                    thinking, cancellationToken).ConfigureAwait(false);
                                break;

                            case "input_json_delta":
                                var partial = delta.TryGetProperty("partial_json", out var pj)
                                    ? pj.GetString() ?? "" : "";
                                toolInputAccumulator.Append(partial);
                                break;
                        }
                    }

                    break;

                case "content_block_stop":
                    // Block is complete — finalize it
                    switch (currentBlockType)
                    {
                        case "text":
                            var textBlock = new TextContentBlock(textAccumulator.ToString());
                            contentBlocks.Add(textBlock);
                            rawJsonBlocks.Add(SerializeBlock(new { type = "text", text = textBlock.Text }));
                            break;

                        case "thinking":
                            var thinkBlock = new ThinkingContentBlock(thinkingAccumulator.ToString());
                            contentBlocks.Add(thinkBlock);
                            rawJsonBlocks.Add(SerializeBlock(new { type = "thinking", thinking = thinkBlock.Thinking }));
                            break;

                        case "tool_use":
                            JsonElement toolInput;
                            try
                            {
                                using var doc = JsonDocument.Parse(
                                    toolInputAccumulator.Length > 0 ? toolInputAccumulator.ToString() : "{}");
                                toolInput = doc.RootElement.Clone();
                            }
                            catch (JsonException)
                            {
                                using var doc = JsonDocument.Parse("{}");
                                toolInput = doc.RootElement.Clone();
                            }

                            var toolBlock = new ToolUseContentBlock(currentToolId, currentToolName, toolInput);
                            contentBlocks.Add(toolBlock);
                            rawJsonBlocks.Add(SerializeBlock(new
                            {
                                type = "tool_use",
                                id = toolBlock.Id,
                                name = toolBlock.Name,
                                input = toolInput,
                            }));
                            break;
                    }

                    break;

                case "message_delta":
                    // Extract stop_reason from the message delta
                    if (sse.Data.TryGetProperty("delta", out var msgDelta) &&
                        msgDelta.TryGetProperty("stop_reason", out var sr))
                    {
                        stopReason = sr.GetString();
                    }

                    break;

                case "message_stop":
                    // End of message
                    break;

                case "ping":
                    // Keep-alive, ignore
                    break;
            }
        }

        return (stopReason, contentBlocks, rawJsonBlocks);
    }

    /// <summary>
    /// Execute all tool_use blocks from the assistant response.
    /// Handles approval flow for tools that require it.
    /// </summary>
    private async Task<List<ToolResultContent>> ExecuteToolsAsync(
        AgentSession session,
        List<ContentBlock> contentBlocks,
        CancellationToken cancellationToken)
    {
        var results = new List<ToolResultContent>();

        foreach (var block in contentBlocks)
        {
            if (block is not ToolUseContentBlock toolUse)
            {
                continue;
            }

            var tool = session.ToolRegistry.Resolve(toolUse.Name);
            if (tool is null)
            {
                results.Add(new ToolResultContent
                {
                    ToolUseId = toolUse.Id,
                    Content = $"Unknown tool: {toolUse.Name}",
                    IsError = true,
                });
                continue;
            }

            // Approval flow
            if (tool.RequiresApproval && !session.ApproveAll)
            {
                var decision = await RequestApprovalAsync(
                    session, toolUse, tool, cancellationToken).ConfigureAwait(false);

                if (decision == ActionDecision.ApproveAll)
                {
                    session.ApproveAll = true;
                }
                else if (decision == ActionDecision.Reject)
                {
                    results.Add(new ToolResultContent
                    {
                        ToolUseId = toolUse.Id,
                        Content = "User rejected this action.",
                        IsError = true,
                    });

                    await PublishOutput(session.SessionId, OutputType.ToolUse,
                        $"[REJECTED] {toolUse.Name}", cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            // Execute the tool
            LogToolExecution(_logger, session.SessionId, toolUse.Name, toolUse.Id);
            ToolResult toolResult;
            try
            {
                toolResult = await tool.ExecuteAsync(
                    toolUse.Input, session.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                toolResult = new ToolResult($"Tool execution error: {ex.Message}", IsError: true);
            }

            results.Add(new ToolResultContent
            {
                ToolUseId = toolUse.Id,
                Content = toolResult.Content,
                IsError = toolResult.IsError,
            });

            // Publish tool result to output stream
            var outputContent = $"[{toolUse.Name}] {(toolResult.IsError ? "ERROR: " : "")}{Truncate(toolResult.Content, 500)}";
            await PublishOutput(session.SessionId, OutputType.ToolUse,
                outputContent, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>
    /// Request approval from the user for a tool execution.
    /// Publishes an ActionRequest and waits for the user's response.
    /// </summary>
    private async Task<ActionDecision> RequestApprovalAsync(
        AgentSession session,
        ToolUseContentBlock toolUse,
        ITool tool,
        CancellationToken cancellationToken)
    {
        var actionId = Guid.NewGuid().ToString("N");

        session.TransitionTo(SessionState.WaitingForApproval);

        // Create a TCS that the RespondToAction RPC will complete
        var tcs = new TaskCompletionSource<ActionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingActions[actionId] = tcs;

        try
        {
            // Determine action type from tool name
            var actionType = tool.Name switch
            {
                "edit_file" => ActionType.FileEdit,
                "write_file" => ActionType.FileCreate,
                "bash" => ActionType.CommandExecute,
                _ => ActionType.ToolUse,
            };

            var actionRequest = new ActionRequest
            {
                SessionId = session.SessionId,
                ActionId = actionId,
                Type = actionType,
                Description = $"Tool: {toolUse.Name}\nInput: {toolUse.Input.GetRawText()}",
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            };

            // Store on session so late-connecting StreamActions subscribers can replay it.
            session.CurrentPendingAction = actionRequest;

            // Publish to action stream for already-connected subscribers.
            await _eventBus.PublishActionAsync(session.SessionId, actionRequest, cancellationToken).ConfigureAwait(false);

            LogAwaitingApproval(_logger, session.SessionId, toolUse.Name, actionId);

            // Wait for the user's decision
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, session.CancellationTokenSource.Token);
            linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token));

            var decision = await tcs.Task.ConfigureAwait(false);

            LogApprovalDecision(_logger, session.SessionId, actionId, decision.ToString());

            return decision;
        }
        finally
        {
            session.CurrentPendingAction = null;
            session.PendingActions.TryRemove(actionId, out _);
            session.TryTransitionTo(SessionState.Running);
        }
    }

    private async ValueTask PublishOutput(string sessionId, OutputType type, string content, CancellationToken cancellationToken)
    {
        var chunk = new OutputChunk
        {
            SessionId = sessionId,
            Type = type,
            Content = content,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await _eventBus.PublishOutputAsync(sessionId, chunk, cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement SerializeBlock(object block)
    {
        var json = JsonSerializer.Serialize(block);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...");
    }

    // --- High-performance logging (CA1848) ---

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: iteration {Iteration}")]
    private static partial void LogIterationStart(ILogger logger, string sessionId, int iteration);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: conversation ended ({StopReason})")]
    private static partial void LogConversationEnd(ILogger logger, string sessionId, string stopReason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: unknown stop_reason '{StopReason}'")]
    private static partial void LogUnknownStopReason(ILogger logger, string sessionId, string stopReason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {SessionId}: max iterations reached ({MaxIterations})")]
    private static partial void LogMaxIterations(ILogger logger, string sessionId, int maxIterations);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {SessionId}: executing tool {ToolName} (id: {ToolId})")]
    private static partial void LogToolExecution(ILogger logger, string sessionId, string toolName, string toolId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: awaiting approval for {ToolName} (action: {ActionId})")]
    private static partial void LogAwaitingApproval(ILogger logger, string sessionId, string toolName, string actionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: action {ActionId} decision: {Decision}")]
    private static partial void LogApprovalDecision(ILogger logger, string sessionId, string actionId, string decision);
}
