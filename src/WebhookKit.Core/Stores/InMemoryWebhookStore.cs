using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using WebhookKit.Abstractions;

namespace WebhookKit.Core.Stores;

public sealed class InMemoryWebhookStore : IWebhookStore
{
    private readonly ConcurrentDictionary<string, WebhookRecord> _byDeduplicationKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WebhookRecord> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _recordLocks = new(StringComparer.Ordinal);
    private readonly IWebhookClock _clock;
    private readonly object _createGate = new();

    public InMemoryWebhookStore(IWebhookClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public ValueTask<WebhookRecord?> GetAsync(string provider, string eventId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = BuildEventKey(NormalizeProvider(provider), ValidateEventId(eventId));
        return FindByDeduplicationKey(key);
    }

    public ValueTask<bool> TryCreateAsync(WebhookRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ValidateRecord(record);
        var stored = Copy(record, key);

        lock (_createGate)
        {
            if (_byDeduplicationKey.ContainsKey(key) || _byId.ContainsKey(stored.Id))
            {
                return ValueTask.FromResult(false);
            }

            _recordLocks.GetOrAdd(stored.Id, static _ => new object());
            if (!_byDeduplicationKey.TryAdd(key, stored))
            {
                return ValueTask.FromResult(false);
            }

            if (!_byId.TryAdd(stored.Id, stored))
            {
                _byDeduplicationKey.TryRemove(key, out _);
                _recordLocks.TryRemove(stored.Id, out _);
                return ValueTask.FromResult(false);
            }
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask UpdateAsync(WebhookRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ValidateRecord(record);
        if (!_byId.TryGetValue(record.Id, out var initial))
        {
            throw new KeyNotFoundException($"Webhook record '{record.Id}' was not found.");
        }

        var gate = GetLock(initial.Id);
        lock (gate)
        {
            var current = _byId[initial.Id];
            if (!ValidateUpdate(current, record, key))
            {
                return default;
            }

            var updated = Copy(record, key);
            _byId[updated.Id] = updated;
            _byDeduplicationKey[updated.DeduplicationKey] = updated;
        }

        return default;
    }

    public ValueTask<WebhookRecord?> GetByWebhookIdAsync(string webhookId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        return FindById(id);
    }

    public ValueTask<bool> TryClaimAsync(string webhookId, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        var owner = ValidateLeaseOwner(leaseOwner);
        ValidateLeaseDuration(leaseDuration);

        if (!_byId.TryGetValue(id, out var initial))
        {
            return ValueTask.FromResult(false);
        }

        var gate = GetLock(initial.Id);
        lock (gate)
        {
            var current = _byId[initial.Id];
            var now = _clock.UtcNow;
            if (!IsClaimable(current, now))
            {
                return ValueTask.FromResult(false);
            }

            DateTimeOffset expiresAt;
            try
            {
                expiresAt = now.Add(leaseDuration);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ArgumentOutOfRangeException(nameof(leaseDuration));
            }

            var updated = Copy(current);
            updated.Status = WebhookProcessingStatus.Processing;
            updated.ProcessingLeaseOwner = owner;
            updated.ProcessingLeaseExpiresAt = expiresAt;
            updated.AttemptCount = checked(current.AttemptCount + 1);
            updated.LastAttemptAt = now;
            updated.ProcessedAt = null;
            updated.FailedAt = null;
            updated.FailureReason = null;
            updated.FailureCode = null;
            _byId[updated.Id] = updated;
            _byDeduplicationKey[updated.DeduplicationKey] = updated;
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> ReleaseAsync(string webhookId, string leaseOwner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        var owner = ValidateLeaseOwner(leaseOwner);
        if (!_byId.TryGetValue(id, out var initial))
        {
            return ValueTask.FromResult(false);
        }

        var gate = GetLock(initial.Id);
        lock (gate)
        {
            var current = _byId[initial.Id];
            if (!IsOwnedProcessingRecord(current, owner))
            {
                return ValueTask.FromResult(false);
            }

            var updated = Copy(current);
            updated.Status = WebhookProcessingStatus.Received;
            updated.ProcessingLeaseOwner = null;
            updated.ProcessingLeaseExpiresAt = null;
            updated.ProcessedAt = null;
            updated.FailedAt = null;
            updated.FailureReason = null;
            updated.FailureCode = null;
            _byId[updated.Id] = updated;
            _byDeduplicationKey[updated.DeduplicationKey] = updated;
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> MarkProcessedAsync(string webhookId, string leaseOwner, DateTimeOffset processedAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        var owner = ValidateLeaseOwner(leaseOwner);
        if (!_byId.TryGetValue(id, out var initial))
        {
            return ValueTask.FromResult(false);
        }

        var gate = GetLock(initial.Id);
        lock (gate)
        {
            var current = _byId[initial.Id];
            if (!IsOwnedProcessingRecord(current, owner))
            {
                return ValueTask.FromResult(false);
            }

            var updated = Copy(current);
            updated.Status = WebhookProcessingStatus.Processed;
            updated.ProcessedAt = processedAt;
            updated.FailedAt = null;
            updated.FailureReason = null;
            updated.FailureCode = null;
            updated.ProcessingLeaseOwner = null;
            updated.ProcessingLeaseExpiresAt = null;
            _byId[updated.Id] = updated;
            _byDeduplicationKey[updated.DeduplicationKey] = updated;
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> MarkFailedAsync(string webhookId, string leaseOwner, DateTimeOffset failedAt, string? failureReason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        var owner = ValidateLeaseOwner(leaseOwner);
        var safeReason = NormalizeFailureReason(failureReason);
        var safeCode = safeReason ?? "processing-failed";
        if (!_byId.TryGetValue(id, out var initial))
        {
            return ValueTask.FromResult(false);
        }

        var gate = GetLock(initial.Id);
        lock (gate)
        {
            var current = _byId[initial.Id];
            if (!IsOwnedProcessingRecord(current, owner))
            {
                return ValueTask.FromResult(false);
            }

            var updated = Copy(current);
            updated.Status = WebhookProcessingStatus.Failed;
            updated.FailedAt = failedAt;
            updated.ProcessedAt = null;
            updated.FailureReason = safeReason;
            updated.FailureCode = safeCode;
            updated.ProcessingLeaseOwner = null;
            updated.ProcessingLeaseExpiresAt = null;
            _byId[updated.Id] = updated;
            _byDeduplicationKey[updated.DeduplicationKey] = updated;
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask<IReadOnlyList<WebhookRecord>> GetRecoverableAsync(DateTimeOffset now, TimeSpan expiredLeaseAge, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(expiredLeaseAge, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        DateTimeOffset cutoff;
        try
        {
            cutoff = now.Subtract(expiredLeaseAge);
        }
        catch (ArgumentOutOfRangeException)
        {
            cutoff = DateTimeOffset.MinValue;
        }

        var recoverable = _byId.Values
            .Where(record => IsRecoverable(record, cutoff))
            .OrderBy(record => record.ReceivedAt)
            .ThenBy(record => record.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(record => Copy(record))
            .ToArray();

        return new ValueTask<IReadOnlyList<WebhookRecord>>(recoverable);
    }

    private ValueTask<WebhookRecord?> FindByDeduplicationKey(string key)
    {
        return _byDeduplicationKey.TryGetValue(key, out var record)
            ? new ValueTask<WebhookRecord?>(Copy(record))
            : new ValueTask<WebhookRecord?>((WebhookRecord?)null);
    }

    private ValueTask<WebhookRecord?> FindById(string id)
    {
        return _byId.TryGetValue(id, out var record)
            ? new ValueTask<WebhookRecord?>(Copy(record))
            : new ValueTask<WebhookRecord?>((WebhookRecord?)null);
    }

    private object GetLock(string id)
    {
        return _recordLocks.GetOrAdd(id, static _ => new object());
    }

    private static string ValidateRecord(WebhookRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateWebhookId(record.Id);
        var provider = NormalizeProvider(record.Provider);
        ValidateRequired(record.DeduplicationKey, nameof(record.DeduplicationKey));
        ArgumentNullException.ThrowIfNull(record.Headers);
        ValidateAttemptCount(record.AttemptCount);
        if (record.ProcessingLeaseOwner is not null)
        {
            ValidateLeaseOwner(record.ProcessingLeaseOwner);
        }

        if (record.EventId is null)
        {
            if (!TryGetFallbackKey(provider, record.DeduplicationKey, out var fallbackKey))
            {
                throw new ArgumentException("A null EventId requires a provider-scoped sha256 deduplication key.", nameof(record));
            }

            return fallbackKey;
        }

        var eventId = ValidateEventId(record.EventId);
        if (!MatchesEventKey(record.DeduplicationKey, provider, eventId))
        {
            throw new ArgumentException("The deduplication key does not match the provider and EventId.", nameof(record));
        }

        return BuildEventKey(provider, eventId);
    }

    private static bool ValidateUpdate(WebhookRecord current, WebhookRecord incoming, string incomingKey)
    {
        if (!string.Equals(current.Provider, incoming.Provider, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.DeduplicationKey, incomingKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The record identity cannot be changed by UpdateAsync.");
        }

        if (current.Status == WebhookProcessingStatus.Processing &&
            !string.Equals(current.ProcessingLeaseOwner, incoming.ProcessingLeaseOwner, StringComparison.Ordinal))
        {
            return false;
        }

        if (current.AttemptCount > incoming.AttemptCount)
        {
            throw new InvalidOperationException("The processing attempt count cannot decrease.");
        }

        if (IsTerminal(current.Status) && current.Status != incoming.Status)
        {
            return false;
        }

        if (current.Status == WebhookProcessingStatus.Received && incoming.Status == WebhookProcessingStatus.Processing)
        {
            return false;
        }

        if (incoming.Status == WebhookProcessingStatus.Processing && current.Status != WebhookProcessingStatus.Processing)
        {
            return false;
        }

        return true;
    }

    private static WebhookRecord Copy(WebhookRecord source, string? deduplicationKey = null)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in source.Headers)
        {
            ArgumentNullException.ThrowIfNull(header.Value);
            headers[header.Key] = (string[])header.Value.Clone();
        }

        var safeFailureReason = NormalizeFailureReason(source.FailureReason);
        string? safeFailureCode = null;
        if (source.Status == WebhookProcessingStatus.Failed || source.FailureReason is not null || source.FailureCode is not null)
        {
            safeFailureCode = NormalizeFailureCode(source.FailureCode, safeFailureReason);
        }

        return new WebhookRecord
        {
            Id = source.Id,
            Provider = source.Provider,
            EventId = source.EventId,
            DeduplicationKey = deduplicationKey ?? source.DeduplicationKey,
            EventType = source.EventType,
            HttpMethod = source.HttpMethod,
            RequestPath = source.RequestPath,
            Headers = new ReadOnlyDictionary<string, string[]>(headers),
            ContentType = source.ContentType,
            ContentLength = source.ContentLength,
            RawBody = source.RawBody?.ToArray(),
            ReceivedAt = source.ReceivedAt,
            ProviderTimestamp = source.ProviderTimestamp,
            Status = source.Status,
            AttemptCount = source.AttemptCount,
            LastAttemptAt = source.LastAttemptAt,
            ProcessingLeaseOwner = source.ProcessingLeaseOwner,
            ProcessingLeaseExpiresAt = source.ProcessingLeaseExpiresAt,
            ProcessedAt = source.ProcessedAt,
            FailedAt = source.FailedAt,
            FailureReason = safeFailureReason,
            FailureCode = safeFailureCode
        };
    }

    private static string NormalizeProvider(string provider)
    {
        ValidateRequired(provider, nameof(provider));
        return provider.ToLowerInvariant();
    }

    private static string ValidateWebhookId(string webhookId)
    {
        ValidateRequired(webhookId, nameof(webhookId));
        return webhookId;
    }

    private static string ValidateEventId(string eventId)
    {
        ValidateRequired(eventId, nameof(eventId));
        return eventId;
    }

    private static string ValidateLeaseOwner(string leaseOwner)
    {
        ValidateRequired(leaseOwner, nameof(leaseOwner));
        return leaseOwner;
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseDuration.Ticks);
    }

    private static void ValidateAttemptCount(int attemptCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptCount);
    }

    private static void ValidateRequired(string? value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must be non-empty.", parameterName);
        }
    }

    private static string BuildEventKey(string provider, string eventId)
    {
        return $"{provider}:{eventId}";
    }

    private static bool MatchesEventKey(string key, string provider, string eventId)
    {
        var separator = key.IndexOf(':');
        return separator > 0 &&
               string.Equals(key[..separator], provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(key[(separator + 1)..], eventId, StringComparison.Ordinal);
    }

    private static bool TryGetFallbackKey(string provider, string key, out string canonicalKey)
    {
        canonicalKey = string.Empty;
        var prefix = $"{provider}:sha256:";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = key[prefix.Length..];
        if (suffix.Length != 64)
        {
            return false;
        }

        var normalized = suffix.ToCharArray();
        for (var index = 0; index < normalized.Length; index++)
        {
            var character = normalized[index];
            if (character is >= 'A' and <= 'F')
            {
                normalized[index] = char.ToLowerInvariant(character);
            }
            else if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        canonicalKey = prefix + new string(normalized);
        return true;
    }

    private static bool IsClaimable(WebhookRecord record, DateTimeOffset now)
    {
        return record.Status == WebhookProcessingStatus.Received ||
               (record.Status == WebhookProcessingStatus.Processing &&
                (!record.ProcessingLeaseExpiresAt.HasValue || record.ProcessingLeaseExpiresAt <= now));
    }

    private static bool IsOwnedProcessingRecord(WebhookRecord record, string owner)
    {
        return record.Status == WebhookProcessingStatus.Processing &&
               string.Equals(record.ProcessingLeaseOwner, owner, StringComparison.Ordinal);
    }

    private static bool IsTerminal(WebhookProcessingStatus status)
    {
        return status is WebhookProcessingStatus.Processed or
            WebhookProcessingStatus.Failed or
            WebhookProcessingStatus.Ignored or
            WebhookProcessingStatus.Duplicate;
    }

    private static bool IsRecoverable(WebhookRecord record, DateTimeOffset cutoff)
    {
        return record.Status == WebhookProcessingStatus.Received ||
               (record.Status == WebhookProcessingStatus.Processing &&
                (!record.ProcessingLeaseExpiresAt.HasValue || record.ProcessingLeaseExpiresAt <= cutoff));
    }

    private static string? NormalizeFailureReason(string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason))
        {
            return null;
        }

        var candidate = failureReason.Trim();
        return IsSafeCode(candidate) ? candidate : "processing-failed";
    }

    private static string? NormalizeFailureCode(string? failureCode, string? failureReason)
    {
        var candidate = string.IsNullOrWhiteSpace(failureCode) ? failureReason : failureCode.Trim();
        return IsSafeCode(candidate) ? candidate : "processing-failed";
    }

    private static bool IsSafeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z') and
                not (>= 'A' and <= 'Z') and
                not (>= '0' and <= '9') and
                not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
