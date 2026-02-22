using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Helmz.Daemon.Configuration;
using Microsoft.Extensions.Options;

namespace Helmz.Daemon.Anthropic;

/// <summary>
/// Claude Code subscription API client. Uses ANTHROPIC_SETUP_TOKEN for auth,
/// mimicking the Claude Code CLI to talk to Anthropic's subscription backend.
/// Auth headers: Bearer token + anthropic-beta + user-agent: claude-cli + x-app: cli.
/// Adapted from PolymarketBot ClaudeApiProvider (auth) + CodexApiProvider (SSE streaming).
/// </summary>
internal sealed partial class ClaudeCodeApi : IApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DaemonOptions _options;
    private readonly ILogger<ClaudeCodeApi> _logger;

    private readonly string? _setupToken;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ClaudeCodeApi(
        IHttpClientFactory httpClientFactory,
        IOptions<DaemonOptions> options,
        ILogger<ClaudeCodeApi> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;

        _setupToken = Environment.GetEnvironmentVariable("ANTHROPIC_SETUP_TOKEN");

        if (string.IsNullOrEmpty(_setupToken))
        {
            LogNoCredentials(_logger);
        }
        else
        {
            LogCredentialsDetected(_logger);
        }
    }

    /// <inheritdoc />
    public bool HasCredentials => !string.IsNullOrEmpty(_setupToken);

    /// <inheritdoc />
    public async Task<MessageResponse> SendMessageAsync(MessageRequest request, CancellationToken cancellationToken)
    {
        using HttpClient httpClient = CreateClient();
        string json = JsonSerializer.Serialize(request, SerializerOptions);
        using StringContent content = new(json, Encoding.UTF8, "application/json");

        Uri url = new(_options.ApiBaseUrl, "/v1/messages");
        LogSendingMessage(_logger, url, request.Model);

        HttpResponseMessage response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            LogApiError(_logger, (int)response.StatusCode, responseText);
            throw new HttpRequestException($"Anthropic API error {response.StatusCode}: {responseText}");
        }

        return JsonSerializer.Deserialize<MessageResponse>(responseText, SerializerOptions)
            ?? throw new InvalidOperationException("Failed to deserialize Anthropic response.");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SseEvent> StreamMessageAsync(
        MessageRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpClient httpClient = CreateClient();
        MessageRequest streamRequest = CloneWithStream(request);
        string json = JsonSerializer.Serialize(streamRequest, SerializerOptions);
        using StringContent httpContent = new(json, Encoding.UTF8, "application/json");

        Uri url = new(_options.ApiBaseUrl, "/v1/messages");
        using HttpRequestMessage httpRequest = new(HttpMethod.Post, url)
        {
            Content = httpContent,
        };

        LogStreamingMessage(_logger, url, request.Model);

        // ResponseHeadersRead enables streaming — we don't wait for the full body
        using HttpResponseMessage response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            LogSseError(_logger, (int)response.StatusCode, errorText);
            throw new HttpRequestException($"Anthropic API error {response.StatusCode}: {errorText}");
        }

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream);

        string? currentEventType = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            // End of stream
            if (line is null)
            {
                break;
            }

            // Empty line = event boundary (SSE spec)
            if (string.IsNullOrEmpty(line))
            {
                currentEventType = null;
                continue;
            }

            // Event type line: "event: message_start"
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                currentEventType = line[7..];
                continue;
            }

            // Data line: "data: {json}"
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                string data = line[6..].Trim();

                // Some APIs use [DONE] sentinel
                if (data is "[DONE]")
                {
                    break;
                }

                JsonElement dataElement;
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(data);
                    dataElement = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    LogMalformedSse(_logger, ex.Message);
                    continue;
                }

                yield return new SseEvent(
                    currentEventType ?? ExtractEventType(dataElement),
                    dataElement);
            }
        }
    }

    private HttpClient CreateClient()
    {
        HttpClient client = _httpClientFactory.CreateClient("anthropic");
        client.Timeout = TimeSpan.FromMinutes(5); // Long timeout for streaming

        // Common header
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        // Subscription (OAuth setup-token) auth — mimic Claude Code client
        client.DefaultRequestHeaders.Add("anthropic-beta", "claude-code-20250219,oauth-2025-04-20");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _setupToken);
        _ = client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", "claude-cli/2.1.2 (external, cli)");
        client.DefaultRequestHeaders.Add("x-app", "cli");

        return client;
    }

    /// <summary>Create a copy of MessageRequest with Stream = true.</summary>
    private static MessageRequest CloneWithStream(MessageRequest source)
    {
        return new MessageRequest
        {
            Model = source.Model,
            MaxTokens = source.MaxTokens,
            System = source.System,
            Messages = source.Messages,
            Tools = source.Tools,
            Stream = true,
        };
    }

    /// <summary>Extract event type from the data payload if not provided by the event: line.</summary>
    private static string ExtractEventType(JsonElement element)
    {
        return element.TryGetProperty("type", out JsonElement typeProp)
            ? typeProp.GetString() ?? "unknown"
            : "unknown";
    }

    // --- High-performance logging (CA1848) ---

    [LoggerMessage(Level = LogLevel.Warning, Message = "ANTHROPIC_SETUP_TOKEN not set. Claude Code API calls will fail.")]
    private static partial void LogNoCredentials(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Claude Code API token detected.")]
    private static partial void LogCredentialsDetected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending message to {Url} (model: {Model})")]
    private static partial void LogSendingMessage(ILogger logger, Uri url, string model);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Streaming message from {Url} (model: {Model})")]
    private static partial void LogStreamingMessage(ILogger logger, Uri url, string model);

    [LoggerMessage(Level = LogLevel.Error, Message = "Claude Code API error {StatusCode}: {Body}")]
    private static partial void LogApiError(ILogger logger, int statusCode, string body);

    [LoggerMessage(Level = LogLevel.Error, Message = "Claude Code SSE error {StatusCode}: {Body}")]
    private static partial void LogSseError(ILogger logger, int statusCode, string body);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping malformed SSE data: {ErrorMessage}")]
    private static partial void LogMalformedSse(ILogger logger, string errorMessage);
}
