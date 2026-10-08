using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Reticula.Infrastructure.Assistant;

/// <summary>Settings under "Assistant". The assistant is off unless Enabled is true and an API key is set (plan 8.5).</summary>
public sealed class AssistantSettings
{
    public bool Enabled { get; init; }
    public string Model { get; init; } = "claude-fable-5-1";
    public string BaseUrl { get; init; } = "https://api.anthropic.com";
    public string? ApiKey { get; init; }
    public int MaxTokens { get; init; } = 2048;
    /// <summary>Model calls per message, tool rounds included.</summary>
    public int MaxTurns { get; init; } = 6;

    public static AssistantSettings From(IConfiguration config) => new()
    {
        Enabled = config.GetValue("Assistant:Enabled", false),
        Model = config["Assistant:Model"] is { Length: > 0 } m ? m : "claude-fable-5-1",
        BaseUrl = config["Assistant:BaseUrl"] is { Length: > 0 } u ? u : "https://api.anthropic.com",
        ApiKey = config["Assistant:ApiKey"] is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
        MaxTokens = config.GetValue("Assistant:MaxTokens", 2048),
        MaxTurns = config.GetValue("Assistant:MaxTurns", 6),
    };

    /// <summary>Why the assistant is unavailable, or null when it can be used.</summary>
    public string? Unavailable => !Enabled ? "The design assistant is turned off." : string.IsNullOrWhiteSpace(ApiKey) ? "The design assistant has no API key." : null;
}

/// <summary>The language model behind the assistant: one Messages API call. Tests replace it with a scripted model.</summary>
public interface IAssistantModel
{
    /// <param name="request">A Messages API request body: model, max_tokens, system, tools, messages.</param>
    /// <returns>The response body: content blocks and stop_reason.</returns>
    Task<JsonObject> CreateMessageAsync(JsonObject request, CancellationToken ct);
}

/// <summary>The Anthropic Messages API over plain HTTP. The API key goes only in the request header, never into a message.</summary>
public sealed class AnthropicModel(HttpClient http, AssistantSettings settings) : IAssistantModel
{
    public async Task<JsonObject> CreateMessageAsync(JsonObject request, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.BaseUrl), "/v1/messages"))
        {
            Content = new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-api-key", settings.ApiKey);
        msg.Headers.Add("anthropic-version", "2023-06-01");
        using var r = await http.SendAsync(msg, ct);
        if (!r.IsSuccessStatusCode)
            throw new AssistantUnavailableException($"The model service returned {(int)r.StatusCode}.");
        return await r.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new AssistantUnavailableException("The model service returned an empty body.");
    }
}

public sealed class AssistantUnavailableException(string message) : Exception(message);
