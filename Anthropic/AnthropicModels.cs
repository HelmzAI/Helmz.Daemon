using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helmz.Daemon.Anthropic;

// --- Request models ---

internal sealed class MessageRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("max_tokens")]
    public required int MaxTokens { get; init; }

    [JsonPropertyName("system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? System { get; init; }

    [JsonPropertyName("messages")]
    public required IList<ConversationMessage> Messages { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stream { get; init; }

    [JsonPropertyName("thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicThinkingConfig? Thinking { get; init; }

    [JsonPropertyName("context_management")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContextManagement? ContextManagement { get; init; }
}

internal sealed class ConversationMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required object Content { get; init; } // string or List<ContentBlock>
}

internal sealed class ToolDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("input_schema")]
    public required JsonElement InputSchema { get; init; }
}

/// <summary>
/// Anthropic extended thinking configuration.
/// Sent as the "thinking" field on <see cref="MessageRequest"/>.
/// </summary>
internal sealed class AnthropicThinkingConfig
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "enabled";

    [JsonPropertyName("budget_tokens")]
    public required int BudgetTokens { get; init; }
}

/// <summary>Anthropic context management configuration for server-side compaction.</summary>
internal sealed class ContextManagement
{
    [JsonPropertyName("edits")]
    public required IReadOnlyList<ContextEdit> Edits { get; init; }
}

internal sealed class ContextEdit
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "compact_20260112";

    [JsonPropertyName("trigger")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContextEditTrigger? Trigger { get; init; }
}

internal sealed class ContextEditTrigger
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "input_tokens";

    [JsonPropertyName("value")]
    public required int Value { get; init; }
}

// --- Response models ---

internal sealed class MessageResponse
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }

    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("content")]
    public IReadOnlyList<JsonElement> Content { get; init; } = [];

    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; init; }
}

internal sealed class UsageInfo
{
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; init; }
}

// --- Content block types (parsed from JsonElement) ---

internal abstract record ContentBlock(string Type);

internal sealed record TextContentBlock(string Text) : ContentBlock("text");

internal sealed record ThinkingContentBlock(string Thinking, string Signature = "") : ContentBlock("thinking");

internal sealed record ToolUseContentBlock(string Id, string Name, JsonElement Input) : ContentBlock("tool_use");

internal sealed record CompactionContentBlock(string Content) : ContentBlock("compaction");

// --- Tool result (sent back in user message) ---

internal sealed class ToolResultContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "tool_result";

    [JsonPropertyName("tool_use_id")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("is_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsError { get; init; }
}

// --- SSE event (from streaming) ---

internal sealed record SseEvent(string EventType, JsonElement Data);

// --- Content block helpers ---

internal static class ContentBlockParser
{
    public static ContentBlock? Parse(JsonElement element)
    {
        if (!element.TryGetProperty("type", out JsonElement typeProp))
        {
            return null;
        }

        string? type = typeProp.GetString();
        return type switch
        {
            "text" => new TextContentBlock(
                element.GetProperty("text").GetString() ?? ""),
            "thinking" => new ThinkingContentBlock(
                element.GetProperty("thinking").GetString() ?? "",
                element.TryGetProperty("signature", out JsonElement sigEl) ? sigEl.GetString() ?? "" : ""),
            "tool_use" => new ToolUseContentBlock(
                element.GetProperty("id").GetString() ?? "",
                element.GetProperty("name").GetString() ?? "",
                element.GetProperty("input")),
            "compaction" => new CompactionContentBlock(
                element.GetProperty("content").GetString() ?? ""),
            _ => null,
        };
    }
}
