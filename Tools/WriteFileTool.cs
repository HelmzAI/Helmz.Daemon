using System.Text.Json;

namespace Helmz.Daemon.Tools;

/// <summary>
/// Writes content to a file, creating directories as needed.
/// </summary>
internal sealed class WriteFileTool : ITool
{
    public string Name => "write_file";

    public string Description => "Write content to a file. Creates parent directories if they don't exist. Overwrites existing files.";

    public JsonElement InputSchema { get; } = ToolRegistry.ParseSchema("""
        {
            "type": "object",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "Absolute or relative file path to write."
                },
                "content": {
                    "type": "string",
                    "description": "The content to write to the file."
                }
            },
            "required": ["path", "content"]
        }
        """);

    public bool RequiresApproval => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, string workingDirectory, CancellationToken cancellationToken)
    {
        var path = input.GetProperty("path").GetString()
            ?? throw new InvalidOperationException("path is required");
        var content = input.GetProperty("content").GetString() ?? "";

        var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path);

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
        return new ToolResult($"Wrote {content.Length} characters to {fullPath}");
    }
}
