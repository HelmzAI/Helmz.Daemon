using System.Text;
using System.Text.Json;
using Helmz.Daemon.Configuration;
using Helmz.Daemon.Sessions;
using Helmz.Daemon.Streaming;
using Helmz.Daemon.Tools;
using Helmz.Spec.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;

namespace Helmz.Daemon.Anthropic;

/// <summary>
/// The agentic tool-use loop. Sends messages to the Anthropic API,
/// streams the response, executes tools (with approval when required),
/// sends tool results back, and repeats until end_turn or max_tokens.
/// </summary>
internal sealed partial class AgentLoop(
    IApiClient client,
    SessionEventBus eventBus,
    IOptions<DaemonOptions> options,
    ILogger<AgentLoop> logger) : IAgentLoop
{
    private const int MaxIterations = 50; // Safety valve
    private const int MaxToolResultChars = 80_000; // ~20K tokens — prevents context overflow

    private readonly IApiClient _client = client;
    private readonly SessionEventBus _eventBus = eventBus;
    private readonly DaemonOptions _options = options.Value;
    private readonly ILogger<AgentLoop> _logger = logger;

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
            MessageRequest request = BuildRequest(session);

            // Collect the full response via SSE streaming
            (string? stopReason, List<ContentBlock>? contentBlocks, List<JsonElement>? rawJsonBlocks) = await StreamResponseAsync(
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

            if (stopReason is "compaction")
            {
                // Server-side compaction with pause — the API generated a compaction
                // summary and paused. Continue the loop to send a follow-up request.
                LogCompactionOccurred(_logger, session.SessionId);
                continue;
            }

            if (stopReason is "tool_use")
            {
                // Execute each tool_use block
                List<ToolResultContent> toolResults = await ExecuteToolsAsync(
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
            $"Agent loop reached maximum iterations ({MaxIterations}).").ConfigureAwait(false);
        session.TransitionTo(SessionState.WaitingForInput);
    }

    private MessageRequest BuildRequest(AgentSession session)
    {
        // Convert conversation history to the expected format
        List<ConversationMessage> messages = [.. session.ConversationHistory.Cast<ConversationMessage>()];

        int? budget = session.ThinkingBudgetTokens;
        int compactionTrigger = _options.CompactionTriggerTokens;
        return new MessageRequest
        {
            Model = session.Model ?? _options.DefaultModel,
            MaxTokens = _options.MaxTokens,
            System = _options.SystemPrompt,
            Messages = messages,
            Tools = session.ToolRegistry.GetToolDefinitions(),
            Stream = true,
            Thinking = budget.HasValue
                ? new AnthropicThinkingConfig { BudgetTokens = budget.Value }
                : null,
            ContextManagement = compactionTrigger > 0
                ? new ContextManagement
                {
                    Edits = [new ContextEdit
                    {
                        Trigger = new ContextEditTrigger { Value = compactionTrigger },
                    }],
                }
                : null,
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
        List<ContentBlock> contentBlocks = [];
        List<JsonElement> rawJsonBlocks = [];
        string? stopReason = null;

        // SSE streaming accumulators
        string currentBlockType = "";
        StringBuilder textAccumulator = new();
        StringBuilder thinkingAccumulator = new();
        string thinkingSignature = "";
        StringBuilder toolInputAccumulator = new();
        StringBuilder compactionAccumulator = new();
        string currentToolId = "";
        string currentToolName = "";

        await foreach (SseEvent? sse in _client.StreamMessageAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (sse.EventType)
            {
                case "message_start":
                    // Initial message metadata — nothing to accumulate
                    break;

                case "content_block_start":
                    // A new content block begins
                    if (sse.Data.TryGetProperty("content_block", out JsonElement blockStart))
                    {
                        currentBlockType = blockStart.TryGetProperty("type", out JsonElement tp)
                            ? tp.GetString() ?? "" : "";

                        if (currentBlockType is "tool_use")
                        {
                            currentToolId = blockStart.TryGetProperty("id", out JsonElement id)
                                ? id.GetString() ?? "" : "";
                            currentToolName = blockStart.TryGetProperty("name", out JsonElement name)
                                ? name.GetString() ?? "" : "";
                            _ = toolInputAccumulator.Clear();
                        }
                        else if (currentBlockType is "compaction")
                        {
                            _ = compactionAccumulator.Clear();
                        }
                        else if (currentBlockType is "thinking")
                        {
                            _ = thinkingAccumulator.Clear();
                            thinkingSignature = "";
                        }
                        else
                        {
                            _ = textAccumulator.Clear();
                        }
                    }

                    break;

                case "content_block_delta":
                    // Incremental content
                    if (sse.Data.TryGetProperty("delta", out JsonElement delta))
                    {
                        string deltaType = delta.TryGetProperty("type", out JsonElement dt)
                            ? dt.GetString() ?? "" : "";

                        switch (deltaType)
                        {
                            case "text_delta":
                                string text = delta.TryGetProperty("text", out JsonElement t)
                                    ? t.GetString() ?? "" : "";
                                _ = textAccumulator.Append(text);
                                await PublishOutput(session.SessionId, OutputType.Stdout,
                                    text).ConfigureAwait(false);
                                break;

                            case "thinking_delta":
                                string thinking = delta.TryGetProperty("thinking", out JsonElement th)
                                    ? th.GetString() ?? "" : "";
                                _ = thinkingAccumulator.Append(thinking);
                                await PublishOutput(session.SessionId, OutputType.Thinking,
                                    thinking).ConfigureAwait(false);
                                break;

                            case "input_json_delta":
                                string partial = delta.TryGetProperty("partial_json", out JsonElement pj)
                                    ? pj.GetString() ?? "" : "";
                                _ = toolInputAccumulator.Append(partial);
                                break;

                            case "signature_delta":
                                thinkingSignature = delta.TryGetProperty("signature", out JsonElement sig)
                                    ? sig.GetString() ?? "" : "";
                                break;

                            case "compaction_delta":
                                string compactionContent = delta.TryGetProperty("content", out JsonElement cc)
                                    ? cc.GetString() ?? "" : "";
                                _ = compactionAccumulator.Append(compactionContent);
                                await PublishOutput(session.SessionId, OutputType.Stderr,
                                    "[compaction] context summarized by API").ConfigureAwait(false);
                                break;

                            default:
                                break;
                        }
                    }

                    break;

                case "content_block_stop":
                    // Block is complete — finalize it
                    switch (currentBlockType)
                    {
                        case "text":
                            TextContentBlock textBlock = new(textAccumulator.ToString());
                            contentBlocks.Add(textBlock);
                            rawJsonBlocks.Add(SerializeBlock(new { type = "text", text = textBlock.Text }));
                            break;

                        case "thinking":
                            ThinkingContentBlock thinkBlock = new(thinkingAccumulator.ToString(), thinkingSignature);
                            contentBlocks.Add(thinkBlock);
                            rawJsonBlocks.Add(SerializeBlock(new { type = "thinking", thinking = thinkBlock.Thinking, signature = thinkBlock.Signature }));
                            break;

                        case "tool_use":
                            JsonElement toolInput;
                            try
                            {
                                using JsonDocument doc = JsonDocument.Parse(
                                    toolInputAccumulator.Length > 0 ? toolInputAccumulator.ToString() : "{}");
                                toolInput = doc.RootElement.Clone();
                            }
                            catch (JsonException)
                            {
                                using JsonDocument doc = JsonDocument.Parse("{}");
                                toolInput = doc.RootElement.Clone();
                            }

                            ToolUseContentBlock toolBlock = new(currentToolId, currentToolName, toolInput);
                            contentBlocks.Add(toolBlock);
                            rawJsonBlocks.Add(SerializeBlock(new
                            {
                                type = "tool_use",
                                id = toolBlock.Id,
                                name = toolBlock.Name,
                                input = toolInput,
                            }));
                            break;

                        case "compaction":
                            CompactionContentBlock compactBlock = new(compactionAccumulator.ToString());
                            contentBlocks.Add(compactBlock);
                            rawJsonBlocks.Add(SerializeBlock(new
                            {
                                type = "compaction",
                                content = compactBlock.Content,
                            }));
                            break;

                        default:
                            break;
                    }

                    break;

                case "message_delta":
                    // Extract stop_reason from the message delta
                    if (sse.Data.TryGetProperty("delta", out JsonElement msgDelta) &&
                        msgDelta.TryGetProperty("stop_reason", out JsonElement sr))
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

                default:
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
        List<ToolResultContent> results = [];

        foreach (ContentBlock block in contentBlocks)
        {
            if (block is not ToolUseContentBlock toolUse)
            {
                continue;
            }

            ITool? tool = session.ToolRegistry.Resolve(toolUse.Name);
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
                ActionDecision decision = await RequestApprovalAsync(
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
                        $"[REJECTED] {toolUse.Name}").ConfigureAwait(false);
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

            // Offload oversized tool results to a temp file so the AI can
            // selectively read relevant parts instead of losing the data.
            string resultContent = toolResult.Content.Length > MaxToolResultChars
                ? SaveToTempFile(toolResult.Content, toolUse.Name)
                : toolResult.Content;

            results.Add(new ToolResultContent
            {
                ToolUseId = toolUse.Id,
                Content = resultContent,
                IsError = toolResult.IsError,
            });

            // Publish tool result to output stream
            string outputContent = $"[{toolUse.Name}] {(toolResult.IsError ? "ERROR: " : "")}{Truncate(toolResult.Content, 500)}";
            await PublishOutput(session.SessionId, OutputType.ToolUse,
                outputContent).ConfigureAwait(false);
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
        string actionId = Guid.NewGuid().ToString("N");

        session.TransitionTo(SessionState.WaitingForApproval);

        // Create a TCS that the RespondToAction RPC will complete
        TaskCompletionSource<ActionDecision> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingActions[actionId] = tcs;

        try
        {
            // Determine action type from tool name
            ActionType actionType = tool.Name switch
            {
                "edit_file" => ActionType.FileEdit,
                "write_file" => ActionType.FileCreate,
                "bash" => ActionType.CommandExecute,
                _ => ActionType.ToolUse,
            };

            ActionRequest actionRequest = new()
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
            await _eventBus.PublishActionAsync(session.SessionId, actionRequest).ConfigureAwait(false);

            LogAwaitingApproval(_logger, session.SessionId, toolUse.Name, actionId);

            // Wait for the user's decision
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, session.CancellationTokenSource.Token);
            _ = linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token));

            ActionDecision decision = await tcs.Task.ConfigureAwait(false);

            LogApprovalDecision(_logger, session.SessionId, actionId, decision.ToString());

            return decision;
        }
        finally
        {
            session.CurrentPendingAction = null;
            _ = session.PendingActions.TryRemove(actionId, out _);
            _ = session.TryTransitionTo(SessionState.Running);
        }
    }

    private async ValueTask PublishOutput(string sessionId, OutputType type, string content)
    {
        OutputChunk chunk = new()
        {
            SessionId = sessionId,
            Type = type,
            Content = content,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

        await _eventBus.PublishOutputAsync(sessionId, chunk).ConfigureAwait(false);
    }

    private static JsonElement SerializeBlock(object block)
    {
        string json = JsonSerializer.Serialize(block);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Saves oversized tool output to a temp file and returns a truncated preview
    /// with instructions for the AI to read the full output using tools.
    /// </summary>
    private static string SaveToTempFile(string fullContent, string toolName)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "helmz");
        _ = Directory.CreateDirectory(tempDir);

        string fileName = $"{toolName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.txt";
        string tempPath = Path.Combine(tempDir, fileName);
        File.WriteAllText(tempPath, fullContent);

        // Return the first portion as a preview + instructions
        string preview = fullContent[..Math.Min(MaxToolResultChars / 2, fullContent.Length)];
        return $"{preview}\n\n" +
            $"[output truncated — full output ({fullContent.Length:N0} chars) saved to {tempPath}]\n" +
            $"Use read_file with offset/limit, or bash tools (head, tail, grep, sed) to read specific sections.";
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId}: context compaction occurred")]
    private static partial void LogCompactionOccurred(ILogger logger, string sessionId);

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
