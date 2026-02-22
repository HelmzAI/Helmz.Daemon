using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Helmz.Daemon.Tools;

/// <summary>
/// Searches file contents using regex patterns.
/// </summary>
internal sealed class GrepTool : ITool
{
    private const int MaxResults = 500;

    public string Name => "grep";

    public string Description => "Search file contents using a regex pattern. Optionally filter files by glob. Returns matching lines with file paths and line numbers.";

    public JsonElement InputSchema { get; } = ToolRegistry.ParseSchema(/*lang=json,strict*/ """
        {
            "type": "object",
            "properties": {
                "pattern": {
                    "type": "string",
                    "description": "Regex pattern to search for."
                },
                "path": {
                    "type": "string",
                    "description": "Optional: directory to search in. Defaults to working directory."
                },
                "file_glob": {
                    "type": "string",
                    "description": "Optional: glob pattern to filter files (e.g. \"*.cs\")."
                }
            },
            "required": ["pattern"]
        }
        """);

    public bool RequiresApproval => false;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, string workingDirectory, CancellationToken cancellationToken)
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
            return new ToolResult($"Directory not found: {searchPath}", IsError: true);
        }

        string fileGlob = input.TryGetProperty("file_glob", out JsonElement globProp)
            ? globProp.GetString() ?? "**/*"
            : "**/*";

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException ex)
        {
            return new ToolResult($"Invalid regex: {ex.Message}", IsError: true);
        }

        Matcher matcher = new();
        _ = matcher.AddInclude(fileGlob);
        IOrderedEnumerable<string> files = matcher.GetResultsInFullPath(searchPath).OrderBy(f => f, StringComparer.Ordinal);

        List<string> results = [];
        foreach (string file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                string[] lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        results.Add($"{file}:{i + 1}: {lines[i]}");
                        if (results.Count >= MaxResults)
                        {
                            results.Add($"... truncated at {MaxResults} results");
                            return new ToolResult(string.Join('\n', results));
                        }
                    }
                }
            }
            catch (IOException)
            {
                // Skip files that can't be read (binary, locked, etc.)
            }
        }

        return results.Count == 0
            ? new ToolResult("No matches found.")
            : new ToolResult(string.Join('\n', results));
    }
}
