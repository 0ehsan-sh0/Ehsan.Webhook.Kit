// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Text.Json;

namespace WebhookKit.Core.Options;

/// <summary>Root WebhookKit options.</summary>
public sealed class WebhookKitOptions
{
    /// <summary>Default maximum request body size (1 MiB).</summary>
    public const long DefaultMaxRequestBodySizeBytes = 1024 * 1024;

    /// <summary>Named provider configurations. Case-insensitive.</summary>
    public Dictionary<string, WebhookProviderOptions> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Global maximum request body size in bytes. Exceeding requests map to 413.</summary>
    public long MaxRequestBodySizeBytes { get; set; } = DefaultMaxRequestBodySizeBytes;

    public WebhookStorageOptions Storage { get; set; } = new();

    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Register a provider. Throws <see cref="WebhookConfigurationException"/> on null/empty/duplicate names.</summary>
    public WebhookKitOptions AddProvider(string name, Action<WebhookProviderOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new WebhookConfigurationException("Provider name must be non-empty.");
        }

        if (Providers.ContainsKey(name))
        {
            throw new WebhookConfigurationException($"Duplicate provider name '{name}'.");
        }

        var options = new WebhookProviderOptions();
        configure(options);
        Providers.Add(name, options);
        return this;
    }
}
