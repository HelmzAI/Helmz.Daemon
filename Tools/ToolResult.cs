namespace Helmz.Daemon.Tools;

/// <summary>
/// Result from executing a tool.
/// </summary>
/// <param name="Content">The output content to return to Claude.</param>
/// <param name="IsError">Whether the execution resulted in an error.</param>
internal sealed record ToolResult(string Content, bool IsError = false);
