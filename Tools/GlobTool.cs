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

    public JsonElement InputSchema { get; } = ToolRegistry.ParseSchema("""
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
        var pattern = input.GetProperty("pattern").GetString()
            ?? throw new InvalidOperationException("pattern is required");

        var searchPath = input.TryGetProperty("path", out var pathProp)
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

        var matcher = new Matcher();
        matcher.AddInclude(pattern);
        var result = matcher.GetResultsInFullPath(searchPath);
        var files = result.OrderBy(f => f, StringComparer.Ordinal).ToList();

        if (files.Count == 0)
        {
            return Task.FromResult(new ToolResult("No files matched the pattern."));
        }

        return Task.FromResult(new ToolResult(string.Join('\n', files)));
    }
}
