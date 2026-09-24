using System.Text.Json.Serialization;

namespace WebhookKit.AspNetCore.Responses;

/// <summary>JSON problem response emitted for a failed webhook request.</summary>
/// <remarks>The response intentionally contains only safe code, message, and optional trace data.</remarks>
public sealed record WebhookProblemResponse
{
    /// <summary>Stable machine-readable error code.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>Safe human-readable error message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Optional trace identifier, omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceId { get; init; }
}
