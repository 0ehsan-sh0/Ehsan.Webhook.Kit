// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Core.Options;

/// <summary>Replay-window settings for one provider.</summary>
public sealed class WebhookTimestampOptions
{
    /// <summary>Header carrying the provider timestamp.</summary>
    public string? HeaderName { get; set; }

    /// <summary>Acceptable clock skew in either direction. Default 5 minutes.</summary>
    public TimeSpan Tolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>When true, requests without a timestamp are accepted (verified only).</summary>
    public bool AllowMissing { get; set; }
}
