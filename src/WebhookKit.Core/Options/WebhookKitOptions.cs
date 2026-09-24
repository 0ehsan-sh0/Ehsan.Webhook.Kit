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

    /// <summary>Storage settings shared by all providers.</summary>
    public WebhookStorageOptions Storage { get; set; } = new();

    /// <summary>Queue settings for asynchronous admission.</summary>
    public WebhookQueueOptions Queue { get; set; } = new();

    /// <summary>Background worker and recovery settings.</summary>
    public WebhookBackgroundOptions Background { get; set; } = new();

    /// <summary>JSON settings used by the default deserializer; the options object is caller-owned.</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Registers a provider configuration.</summary>
    /// <param name="name">Non-empty provider name; names are case-insensitive.</param>
    /// <param name="configure">Callback that populates the new provider options.</param>
    /// <returns>This options instance for fluent configuration.</returns>
    /// <exception cref="WebhookConfigurationException">The name is empty or already registered.</exception>
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
