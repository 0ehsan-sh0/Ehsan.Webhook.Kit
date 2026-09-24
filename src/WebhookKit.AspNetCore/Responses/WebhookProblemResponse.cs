using System.Text.Json.Serialization;

namespace WebhookKit.AspNetCore.Responses;

public sealed record WebhookProblemResponse
{
    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceId { get; init; }
}
