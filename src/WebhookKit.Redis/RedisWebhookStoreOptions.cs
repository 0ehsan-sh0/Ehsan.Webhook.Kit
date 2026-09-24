using Microsoft.Extensions.Options;

namespace WebhookKit.Redis;

/// <summary>Configures Redis keys and retention periods for the webhook store.</summary>
public sealed class RedisWebhookStoreOptions
{
    /// <summary>Prefix used for all Redis webhook keys.</summary>
    public string KeyPrefix { get; set; } = "webhookkit";

    /// <summary>TTL for provider/event deduplication keys.</summary>
    public TimeSpan DeduplicationRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>TTL for persisted webhook records.</summary>
    public TimeSpan RecordRetention { get; set; } = TimeSpan.FromDays(30);

    internal void Validate()
    {
        if (!TryValidate(out var failure))
        {
            throw new ArgumentException(failure);
        }
    }

    internal bool TryValidate(out string failure)
    {
        if (string.IsNullOrWhiteSpace(KeyPrefix) || KeyPrefix.Length > 64 || !IsSafePrefix(KeyPrefix))
        {
            failure = "KeyPrefix must contain 1 to 64 ASCII letters, digits, dots, underscores, or hyphens.";
            return false;
        }

        if (DeduplicationRetention < TimeSpan.FromMilliseconds(1))
        {
            failure = "DeduplicationRetention must be at least one millisecond.";
            return false;
        }

        if (RecordRetention < TimeSpan.FromMilliseconds(1))
        {
            failure = "RecordRetention must be at least one millisecond.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool IsSafePrefix(string prefix)
    {
        foreach (var character in prefix)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class RedisWebhookStoreOptionsValidator : IValidateOptions<RedisWebhookStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisWebhookStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.TryValidate(out var failure)
            ? ValidateOptionsResult.Skip
            : ValidateOptionsResult.Fail(failure);
    }
}
