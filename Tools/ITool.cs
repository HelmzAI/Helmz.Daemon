using System.Text.Json;

namespace Helmz.Daemon.Tools;

/// <summary>
/// Represents a tool that Claude can invoke during an agent session.
/// Implementations handle specific capabilities (file I/O, bash, search).
/// </summary>
internal interface ITool
{
    /// <summary>Tool name as sent to the Anthropic API (e.g. "read_file").</summary>
    string Name { get; }

    /// <summary>Human-readable description for the API.</summary>
    string Description { get; }

    /// <summary>JSON Schema for the tool's input parameters.</summary>
    JsonElement InputSchema { get; }

    /// <summary>Whether this tool requires user approval before execution.</summary>
    bool RequiresApproval { get; }

    /// <summary>Execute the tool with the given input.</summary>
    /// <param name="input">The tool input as parsed JSON.</param>
    /// <param name="workingDirectory">The session's working directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tool execution result.</returns>
    Task<ToolResult> ExecuteAsync(JsonElement input, string workingDirectory, CancellationToken cancellationToken);
}
