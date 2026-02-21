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

internal sealed record ThinkingContentBlock(string Thinking) : ContentBlock("thinking");

internal sealed record ToolUseContentBlock(string Id, string Name, JsonElement Input) : ContentBlock("tool_use");

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
        if (!element.TryGetProperty("type", out var typeProp))
        {
            return null;
        }

        var type = typeProp.GetString();
        return type switch
        {
            "text" => new TextContentBlock(
                element.GetProperty("text").GetString() ?? ""),
            "thinking" => new ThinkingContentBlock(
                element.GetProperty("thinking").GetString() ?? ""),
            "tool_use" => new ToolUseContentBlock(
                element.GetProperty("id").GetString() ?? "",
                element.GetProperty("name").GetString() ?? "",
                element.GetProperty("input")),
            _ => null,
        };
    }
}
