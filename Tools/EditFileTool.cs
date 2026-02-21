using System.Text.Json;

namespace Helmz.Daemon.Tools;

/// <summary>
/// Performs exact string replacement in a file.
/// </summary>
internal sealed class EditFileTool : ITool
{
    public string Name => "edit_file";

    public string Description => "Replace an exact string in a file with new content. The old_string must be unique in the file.";

    public JsonElement InputSchema { get; } = ToolRegistry.ParseSchema("""
        {
            "type": "object",
            "properties": {
                "path": {
                    "type": "string",
                    "description": "Absolute or relative file path to edit."
                },
                "old_string": {
                    "type": "string",
                    "description": "The exact string to find and replace."
                },
                "new_string": {
                    "type": "string",
                    "description": "The replacement string."
                }
            },
            "required": ["path", "old_string", "new_string"]
        }
        """);

    public bool RequiresApproval => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, string workingDirectory, CancellationToken cancellationToken)
    {
        var path = input.GetProperty("path").GetString()
            ?? throw new InvalidOperationException("path is required");
        var oldString = input.GetProperty("old_string").GetString()
            ?? throw new InvalidOperationException("old_string is required");
        var newString = input.GetProperty("new_string").GetString() ?? "";

        var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(workingDirectory, path);

        if (!File.Exists(fullPath))
        {
            return new ToolResult($"File not found: {fullPath}", IsError: true);
        }

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);

        var occurrences = CountOccurrences(content, oldString);
        if (occurrences == 0)
        {
            return new ToolResult("old_string not found in file.", IsError: true);
        }

        if (occurrences > 1)
        {
            return new ToolResult($"old_string found {occurrences} times. It must be unique. Provide more context.", IsError: true);
        }

        var updated = content.Replace(oldString, newString, StringComparison.Ordinal);
        await File.WriteAllTextAsync(fullPath, updated, cancellationToken).ConfigureAwait(false);
        return new ToolResult($"Edited {fullPath}");
    }

    private static int CountOccurrences(string text, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
