using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Reticula.Infrastructure.Assistant;

/// <summary>Assistant settings (plan 8.5): off unless enabled and given an API key.</summary>
public sealed class AssistantOptions
{
    /// <summary>The feature flag (plan 8.5). Settable so a test host can switch it.</summary>
    public bool Enabled { get; set; }
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "claude-sonnet-5-5";
    public string BaseUrl { get; init; } = "https://api.anthropic.com";
    /// <summary>Model calls per user message (each may use tools).</summary>
    public int MaxTurns { get; init; } = 8;
    public int MaxTokens { get; init; } = 2000;

    public bool IsOn => Enabled && (!string.IsNullOrWhiteSpace(ApiKey) || UseFake);

    /// <summary>Tests replace the model; the flag still has to be on.</summary>
    public bool UseFake { get; set; }

    public static AssistantOptions From(IConfiguration config) => new()
    {
        Enabled = config.GetValue("Assistant:Enabled", false),
        ApiKey = config["Assistant:ApiKey"],
        Model = config["Assistant:Model"] is { Length: > 0 } m ? m : "claude-sonnet-5-5",
        BaseUrl = config["Assistant:BaseUrl"] is { Length: > 0 } b ? b : "https://api.anthropic.com",
        MaxTurns = config.GetValue("Assistant:MaxTurns", 8),
        MaxTokens = config.GetValue("Assistant:MaxTokens", 2000),
        UseFake = config.GetValue("Assistant:UseFake", false),
    };
}

/// <summary>One Messages API request: system prompt, tool definitions and the transcript.</summary>
public sealed record ModelRequest(string System, JsonArray Tools, JsonArray Messages);

/// <summary>The model's reply: content blocks (text, tool_use) and why it stopped.</summary>
public sealed record ModelReply(JsonArray Content, string StopReason);

public interface IAssistantModel
{
    Task<ModelReply> SendAsync(ModelRequest request, CancellationToken ct);
}

public sealed class AssistantUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The Claude Messages API over HTTP.</summary>
public sealed class AnthropicModel(HttpClient http, AssistantOptions options) : IAssistantModel
{
    public async Task<ModelReply> SendAsync(ModelRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["max_tokens"] = options.MaxTokens,
            ["system"] = request.System,
            ["tools"] = request.Tools.DeepClone(),
            ["messages"] = request.Messages.DeepClone(),
        };
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/v1/messages") { Content = JsonContent.Create(body) };
        msg.Headers.Add("x-api-key", options.ApiKey);
        msg.Headers.Add("anthropic-version", "2023-06-01");
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(msg, ct);
        }
        catch (HttpRequestException e)
        {
            throw new AssistantUnavailableException("The assistant service could not be reached.", e);
        }
        using (res)
        {
            if (!res.IsSuccessStatusCode)
                throw new AssistantUnavailableException($"The assistant service answered {(int)res.StatusCode}.");
            var json = await res.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new AssistantUnavailableException("Empty reply from the assistant service.");
            return new ModelReply(json["content"] as JsonArray ?? [], json["stop_reason"]?.GetValue<string>() ?? "end_turn");
        }
    }
}
