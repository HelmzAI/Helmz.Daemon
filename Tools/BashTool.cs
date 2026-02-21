using System.Diagnostics;
using System.Text.Json;

namespace Helmz.Daemon.Tools;

/// <summary>
/// Executes bash commands in the session's working directory.
/// </summary>
internal sealed class BashTool : ITool
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    public string Name => "bash";

    public string Description => "Execute a bash command. Returns stdout, stderr, and exit code. Times out after 2 minutes.";

    public JsonElement InputSchema { get; } = ToolRegistry.ParseSchema("""
        {
            "type": "object",
            "properties": {
                "command": {
                    "type": "string",
                    "description": "The bash command to execute."
                },
                "timeout_ms": {
                    "type": "integer",
                    "description": "Optional timeout in milliseconds (default: 120000)."
                }
            },
            "required": ["command"]
        }
        """);

    public bool RequiresApproval => true;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, string workingDirectory, CancellationToken cancellationToken)
    {
        var command = input.GetProperty("command").GetString()
            ?? throw new InvalidOperationException("command is required");

        var timeoutMs = input.TryGetProperty("timeout_ms", out var timeoutProp)
            ? timeoutProp.GetInt32()
            : (int)DefaultTimeout.TotalMilliseconds;

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-c", command },
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start bash process.");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            var output = $"Exit code: {process.ExitCode}";
            if (!string.IsNullOrEmpty(stdout))
            {
                output += $"\n\nSTDOUT:\n{stdout}";
            }

            if (!string.IsNullOrEmpty(stderr))
            {
                output += $"\n\nSTDERR:\n{stderr}";
            }

            return new ToolResult(output, IsError: process.ExitCode != 0);
        }
        catch (OperationCanceledException)
        {
            return new ToolResult($"Command timed out after {timeoutMs}ms.", IsError: true);
        }
    }
}
