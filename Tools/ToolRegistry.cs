using System.Collections.Concurrent;
using System.Text.Json;
using Helmz.Daemon.Anthropic;

namespace Helmz.Daemon.Tools;

/// <summary>
/// Registry of available tools. Provides tool definitions for the Anthropic API
/// and resolves tools by name for execution. Extensible for MCP integration.
/// </summary>
internal sealed class ToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    /// <summary>Register a tool.</summary>
    public void Register(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
    }

    /// <summary>Resolve a tool by name. Returns null if not found.</summary>
    public ITool? Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _tools.GetValueOrDefault(name);
    }

    /// <summary>Get all tool definitions for the Anthropic API request.</summary>
    public IReadOnlyList<ToolDefinition> GetToolDefinitions()
    {
        return _tools.Values.Select(tool => new ToolDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
        }).ToList();
    }

    /// <summary>Get all registered tool names.</summary>
    public IReadOnlyCollection<string> GetRegisteredToolNames()
    {
        return _tools.Keys.ToList().AsReadOnly();
    }

    /// <summary>Get the number of registered tools.</summary>
    public int Count => _tools.Count;

    /// <summary>Parse a JSON schema string into a JsonElement.</summary>
    public static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
