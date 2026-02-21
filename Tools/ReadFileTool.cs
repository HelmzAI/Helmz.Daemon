using System.Text.Json;

namespace Helmz.Daemon.Tools;

/// <summary>
/// Reads file contents with optional line range support.
/// </summary>
internal sealed class ReadFileTool : ITool
{
    public string Name => "read_file";

    public string Description => "Read the contents of a file. Supports optional line range (start_line and end_line, 1-indexed).";

    public JsonElement InputSchema { get; } = ToolRegistry.ParseSchema("""
        {
            "type": "object",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "Absolute or relative file path to read."
                },
                "start_line": {
                    "type": "integer",
                    "description": "Optional: first line to read (1-indexed)."
                },
                "end_line": {
                    "type": "integer",
                    "description": "Optional: last line to read (1-indexed, inclusive)."
                }
            },
            "required": ["path"]
        }
        """);

    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, string workingDirectory, CancellationToken cancellationToken)
    {
        var path = input.GetProperty("path").GetString()
            ?? throw new InvalidOperationException("path is required");

        var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path);

        if (!File.Exists(fullPath))
        {
            return new ToolResult($"File not found: {fullPath}", IsError: true);
        }

        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken).ConfigureAwait(false);

        int startLine = input.TryGetProperty("start_line", out var startProp) ? startProp.GetInt32() : 1;
        int endLine = input.TryGetProperty("end_line", out var endProp) ? endProp.GetInt32() : lines.Length;

        startLine = Math.Max(1, startLine);
        endLine = Math.Min(lines.Length, endLine);

        if (startLine > endLine)
        {
            return new ToolResult($"Invalid line range: {startLine}-{endLine}", IsError: true);
        }

        var selectedLines = lines.Skip(startLine - 1).Take(endLine - startLine + 1);
        var numbered = selectedLines.Select((line, i) => $"{startLine + i,6}\t{line}");
        return new ToolResult(string.Join('\n', numbered));
    }
}
