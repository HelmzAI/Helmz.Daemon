using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Helmz.Daemon.Tools;

/// <summary>
/// Finds files matching glob patterns.
/// </summary>
internal sealed class GlobTool : ITool
{
    public string Name => "glob";

    public string Description => "Find files matching a glob pattern (e.g. \"**/*.cs\", \"src/**/*.proto\"). Returns matching file paths.";

    public JsonElement InputSchema { get; } = ToolRegistry.ParseSchema(/*lang=json,strict*/ """
        {
            "type": "object",
            "properties": {
                "pattern": {
                    "type": "string",
                    "description": "The glob pattern to match files against."
                },
                "path": {
                    "type": "string",
                    "description": "Optional: directory to search in. Defaults to working directory."
                }
            },
            "required": ["pattern"]
        }
        """);

    public bool RequiresApproval => false;

    public Task<ToolResult> ExecuteAsync(JsonElement input, string workingDirectory, CancellationToken cancellationToken)
    {
        string pattern = input.GetProperty("pattern").GetString()
            ?? throw new InvalidOperationException("pattern is required");

        string searchPath = input.TryGetProperty("path", out JsonElement pathProp)
            ? pathProp.GetString() ?? workingDirectory
            : workingDirectory;

        if (!Path.IsPathRooted(searchPath))
        {
            searchPath = Path.Combine(workingDirectory, searchPath);
        }

        if (!Directory.Exists(searchPath))
        {
            return Task.FromResult(new ToolResult($"Directory not found: {searchPath}", IsError: true));
        }

        Matcher matcher = new();
        _ = matcher.AddInclude(pattern);
        IEnumerable<string> result = matcher.GetResultsInFullPath(searchPath);
        List<string> files = [.. result.OrderBy(f => f, StringComparer.Ordinal)];

        return files.Count == 0
            ? Task.FromResult(new ToolResult("No files matched the pattern."))
            : Task.FromResult(new ToolResult(string.Join('\n', files)));
    }
}
