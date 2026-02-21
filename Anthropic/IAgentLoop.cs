using Helmz.Daemon.Sessions;

namespace Helmz.Daemon.Anthropic;

/// <summary>
/// The agentic loop engine. Sends messages to the Anthropic API,
/// processes tool-use responses, handles approval flow, and loops
/// until the conversation reaches end_turn or max_tokens.
/// </summary>
internal interface IAgentLoop
{
    /// <summary>
    /// Run the agent loop for a session with the given user prompt.
    /// The loop will:
    /// 1. Send the prompt to the API with tool definitions
    /// 2. Stream the response
    /// 3. If tool_use: execute tools (with approval if needed), send results back, repeat
    /// 4. If end_turn: return (session transitions to WaitingForInput)
    /// </summary>
    /// <param name="session">The agent session to run in.</param>
    /// <param name="userPrompt">The user's prompt/command.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RunAsync(AgentSession session, string userPrompt, CancellationToken cancellationToken);
}
