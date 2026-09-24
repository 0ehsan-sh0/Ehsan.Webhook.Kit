using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using WebhookKit.Abstractions;

namespace WebhookKit.Redis;

/// <summary>Redis-backed webhook store with atomic Lua transitions and lease recovery.</summary>
/// <remarks>Records and raw bodies are serialized by this provider; callers should not place secrets in failure reasons.</remarks>
public sealed class RedisWebhookStore : IWebhookStore
{
    private const string NegativeInfinityScore = "-inf";
    private const string CreateScript = """
        if redis.call('EXISTS', KEYS[1]) ~= 0 or redis.call('EXISTS', KEYS[2]) ~= 0 or redis.call('ZSCORE', KEYS[3], ARGV[1]) ~= false then
            return 0
        end
        redis.call('PSETEX', KEYS[1], ARGV[3], ARGV[1])
        redis.call('PSETEX', KEYS[2], ARGV[4], ARGV[2])
        redis.call('ZADD', KEYS[3], ARGV[5], ARGV[1])
        return 1
        """;
    private const string ClaimScript = """
        local raw = redis.call('GET', KEYS[1])
        if not raw then
            return 0
        end
        local decoded, record = pcall(cjson.decode, raw)
        if not decoded then
            return -1
        end
        local claimable = record.status == 0
        if record.status == 1 then
            local score = redis.call('ZSCORE', KEYS[2], record.id)
            claimable = score and (score == '-inf' or tonumber(score) <= tonumber(ARGV[2]))
        end
        if not claimable then
            return 0
        end
        record.status = 1
        record.processingLeaseOwner = ARGV[1]
        record.processingLeaseExpiresAt = ARGV[5]
        record.attemptCount = (tonumber(record.attemptCount) or 0) + 1
        record.lastAttemptAt = ARGV[4]
        record.processedAt = cjson.null
        record.failedAt = cjson.null
        record.failureReason = cjson.null
        record.failureCode = cjson.null
        redis.call('SET', KEYS[1], cjson.encode(record), 'KEEPTTL')
        redis.call('ZADD', KEYS[2], ARGV[3], record.id)
        return 1
        """;
    private const string ReleaseScript = """
        local raw = redis.call('GET', KEYS[1])
        if not raw then
            return 0
        end
        local decoded, record = pcall(cjson.decode, raw)
        if not decoded then
            return -1
        end
        if record.status ~= 1 or record.processingLeaseOwner ~= ARGV[1] then
            return 0
        end
        record.status = 0
        record.processingLeaseOwner = cjson.null
        record.processingLeaseExpiresAt = cjson.null
        record.processedAt = cjson.null
        record.failedAt = cjson.null
        record.failureReason = cjson.null
        record.failureCode = cjson.null
        redis.call('SET', KEYS[1], cjson.encode(record), 'KEEPTTL')
        redis.call('ZADD', KEYS[2], '-inf', record.id)
        return 1
        """;
    private const string MarkProcessedScript = """
        local raw = redis.call('GET', KEYS[1])
        if not raw then
            return 0
        end
        local decoded, record = pcall(cjson.decode, raw)
        if not decoded then
            return -1
        end
        if record.status ~= 1 or record.processingLeaseOwner ~= ARGV[1] then
            return 0
        end
        record.status = 2
        record.processedAt = ARGV[2]
        record.failedAt = cjson.null
        record.failureReason = cjson.null
        record.failureCode = cjson.null
        record.processingLeaseOwner = cjson.null
        record.processingLeaseExpiresAt = cjson.null
        redis.call('SET', KEYS[1], cjson.encode(record), 'KEEPTTL')
        redis.call('ZREM', KEYS[2], record.id)
        return 1
        """;
    private const string MarkFailedScript = """
        local raw = redis.call('GET', KEYS[1])
        if not raw then
            return 0
        end
        local decoded, record = pcall(cjson.decode, raw)
        if not decoded then
            return -1
        end
        if record.status ~= 1 or record.processingLeaseOwner ~= ARGV[1] then
            return 0
        end
        record.status = 3
        record.failedAt = ARGV[2]
        record.processedAt = cjson.null
        record.failureReason = ARGV[3] ~= '' and ARGV[3] or cjson.null
        record.failureCode = ARGV[4] ~= '' and ARGV[4] or cjson.null
        record.processingLeaseOwner = cjson.null
        record.processingLeaseExpiresAt = cjson.null
        redis.call('SET', KEYS[1], cjson.encode(record), 'KEEPTTL')
        redis.call('ZREM', KEYS[2], record.id)
        return 1
        """;
    private const string UpdateScript = """
        local raw = redis.call('GET', KEYS[1])
        if not raw then
            return -1
        end
        local currentDecoded, current = pcall(cjson.decode, raw)
        if not currentDecoded then
            return -3
        end
        local incomingDecoded, incoming = pcall(cjson.decode, ARGV[1])
        if not incomingDecoded then
            return -3
        end
        if current.id ~= incoming.id or current.provider ~= incoming.provider or current.deduplicationKey ~= incoming.deduplicationKey then
            return -2
        end
        if current.status == 1 and current.processingLeaseOwner ~= incoming.processingLeaseOwner then
            return 0
        end
        if (tonumber(current.attemptCount) or 0) > (tonumber(incoming.attemptCount) or 0) then
            return -2
        end
        if (current.status == 2 or current.status == 3 or current.status == 4 or current.status == 5) and current.status ~= incoming.status then
            return 0
        end
        if current.status == 0 and incoming.status == 1 then
            return 0
        end
        if incoming.status == 1 and current.status ~= 1 then
            return 0
        end
        redis.call('SET', KEYS[1], ARGV[1], 'KEEPTTL')
        if incoming.status == 0 or (incoming.status == 1 and incoming.processingLeaseExpiresAt == cjson.null) then
            redis.call('ZADD', KEYS[2], ARGV[3], incoming.id)
        elseif incoming.status == 1 then
            redis.call('ZADD', KEYS[2], ARGV[3], incoming.id)
        else
            redis.call('ZREM', KEYS[2], incoming.id)
        end
        return 1
        """;
    private const string RecoverScript = """
        local cutoff = tonumber(ARGV[1])
        local recovered = {}
        for index = 1, #ARGV - 1 do
            local id = ARGV[index + 1]
            local raw = redis.call('GET', KEYS[index + 1])
            if not raw then
                redis.call('ZREM', KEYS[1], id)
            else
                local decoded, record = pcall(cjson.decode, raw)
                if not decoded then
                    redis.call('ZREM', KEYS[1], id)
                elseif record.status == 2 or record.status == 3 or record.status == 4 or record.status == 5 then
                    redis.call('ZREM', KEYS[1], id)
                elseif record.status == 0 then
                    table.insert(recovered, raw)
                elseif record.status == 1 then
                    local score = redis.call('ZSCORE', KEYS[1], id)
                    if score and (score == '-inf' or tonumber(score) <= cutoff) then
                        table.insert(recovered, raw)
                    end
                end
            end
        end
        return recovered
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IRedisWebhookDatabase _database;
    private readonly IWebhookClock _clock;
    private readonly string _keyPrefix;
    private readonly long _deduplicationRetentionMilliseconds;
    private readonly long _recordRetentionMilliseconds;

    /// <summary>Creates a store using the database from a Redis connection multiplexer.</summary>
    /// <param name="connectionMultiplexer">The application's Redis connection multiplexer.</param>
    /// <param name="options">Key prefix and retention options.</param>
    /// <param name="clock">Clock used for lease timestamps.</param>
    public RedisWebhookStore(
        IConnectionMultiplexer connectionMultiplexer,
        IOptions<RedisWebhookStoreOptions> options,
        IWebhookClock clock)
        : this(CreateDatabase(connectionMultiplexer), options, clock)
    {
    }

    internal RedisWebhookStore(
        IRedisWebhookDatabase database,
        IOptions<RedisWebhookStoreOptions> options,
        IWebhookClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        var storeOptions = options.Value;
        storeOptions.Validate();
        _database = database;
        _clock = clock;
        _keyPrefix = storeOptions.KeyPrefix;
        _deduplicationRetentionMilliseconds = checked((long)storeOptions.DeduplicationRetention.TotalMilliseconds);
        _recordRetentionMilliseconds = checked((long)storeOptions.RecordRetention.TotalMilliseconds);
    }

    /// <summary>Gets a record by provider and event ID from Redis.</summary>
    /// <param name="provider">Provider name normalized by the store.</param>
    /// <param name="eventId">Provider event ID.</param>
    /// <param name="cancellationToken">Token used to cancel Redis operations.</param>
    /// <returns>A detached record, or <see langword="null"/> when absent.</returns>
    public async ValueTask<WebhookRecord?> GetAsync(
        string provider,
        string eventId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedProvider = NormalizeProvider(provider);
        var validatedEventId = ValidateEventId(eventId);
        var deduplicationKey = BuildDeduplicationKey(normalizedProvider, validatedEventId);
        var webhookId = await _database.GetStringAsync(deduplicationKey, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (webhookId is null)
        {
            return null;
        }

        return await GetByWebhookIdAsync(webhookId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically creates a record and its Redis indexes using Lua.</summary>
    /// <param name="record">Record to serialize and store.</param>
    /// <param name="cancellationToken">Token used to cancel the Redis operation.</param>
    /// <returns><see langword="true"/> when created; <see langword="false"/> for an existing identity.</returns>
    public async ValueTask<bool> TryCreateAsync(WebhookRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dto = ValidateRecord(record);
        var normalizedProvider = NormalizeProvider(dto.Provider);
        var deduplicationKey = BuildDeduplicationKey(normalizedProvider, dto);
        var webhookId = dto.Id;
        var command = new RedisWebhookCommand(
            RedisWebhookOperation.Create,
            CreateScript,
            [deduplicationKey, BuildRecordKey(webhookId), _indexKey],
            [
                webhookId,
                Serialize(dto),
                _deduplicationRetentionMilliseconds.ToString(CultureInfo.InvariantCulture),
                _recordRetentionMilliseconds.ToString(CultureInfo.InvariantCulture),
                GetRecoveryScore(dto)
            ]);
        var result = await _database.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Value == 1;
    }

    /// <summary>Updates a stored record while preserving identity and lease rules.</summary>
    /// <param name="record">Updated record snapshot.</param>
    /// <param name="cancellationToken">Token used to cancel the Redis operation.</param>
    /// <returns>A task that completes when the update is applied or ignored.</returns>
    public async ValueTask UpdateAsync(WebhookRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dto = ValidateRecord(record);
        var command = new RedisWebhookCommand(
            RedisWebhookOperation.Update,
            UpdateScript,
            [BuildRecordKey(dto.Id), _indexKey],
            [Serialize(dto), ((int)dto.Status).ToString(CultureInfo.InvariantCulture), GetRecoveryScore(dto), dto.Id]);
        var result = await _database.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        switch (result.Value)
        {
            case 1:
                return;
            case -1:
                throw new KeyNotFoundException($"Webhook record '{dto.Id}' was not found.");
            case -2:
                throw new InvalidOperationException("The record identity or processing attempt cannot be changed by UpdateAsync.");
            case -3:
                throw new InvalidDataException("The stored webhook record is corrupt.");
            default:
                return;
        }
    }

    /// <summary>Gets a record by its WebhookKit transmission identifier.</summary>
    /// <param name="webhookId">Canonical ULID transmission identifier.</param>
    /// <param name="cancellationToken">Token used to cancel the Redis operation.</param>
    /// <returns>A detached record, or <see langword="null"/> when absent.</returns>
    public async ValueTask<WebhookRecord?> GetByWebhookIdAsync(
        string webhookId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        var json = await _database.GetStringAsync(BuildRecordKey(id), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Deserialize(json);
    }

    /// <summary>Atomically claims a received or expired processing record.</summary>
    /// <param name="webhookId">Canonical ULID transmission identifier.</param>
    /// <param name="leaseOwner">Owner recorded for the lease.</param>
    /// <param name="leaseDuration">Positive lease duration.</param>
    /// <param name="cancellationToken">Token used to cancel the Redis operation.</param>
    /// <returns><see langword="true"/> when the lease was acquired.</returns>
    public async ValueTask<bool> TryClaimAsync(
        string webhookId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        var owner = ValidateLeaseOwner(leaseOwner);
        ValidatePositiveDuration(leaseDuration, nameof(leaseDuration));
        var now = _clock.UtcNow;
        DateTimeOffset expiresAt;
        try
        {
            expiresAt = now.Add(leaseDuration);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var command = new RedisWebhookCommand(
            RedisWebhookOperation.Claim,
            ClaimScript,
            [BuildRecordKey(id), _indexKey],
            [
                owner,
                now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                expiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                now.ToString("O", CultureInfo.InvariantCulture),
                expiresAt.ToString("O", CultureInfo.InvariantCulture)
            ]);
        var result = await ExecuteStateCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return result == 1;
    }

    /// <summary>Releases an owned processing lease.</summary>
    /// <param name="webhookId">Canonical ULID transmission identifier.</param>
    /// <param name="leaseOwner">Current lease owner.</param>
    /// <param name="cancellationToken">Token used to cancel the Redis operation.</param>
    /// <returns><see langword="true"/> when the owned lease was released.</returns>
    public async ValueTask<bool> ReleaseAsync(
        string webhookId,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        var owner = ValidateLeaseOwner(leaseOwner);
        var command = new RedisWebhookCommand(
            RedisWebhookOperation.Release,
            ReleaseScript,
            [BuildRecordKey(id), _indexKey],
            [owner]);
        var result = await ExecuteStateCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return result == 1;
    }

    /// <summary>Marks an owned processing record as processed.</summary>
    /// <param name="webhookId">Canonical ULID transmission identifier.</param>
    /// <param name="leaseOwner">Current lease owner.</param>
    /// <param name="processedAt">Completion timestamp.</param>
    /// <param name="cancellationToken">Token used to cancel the Redis operation.</param>
    /// <returns><see langword="true"/> when the transition was applied.</returns>
    public async ValueTask<bool> MarkProcessedAsync(
        string webhookId,
        string leaseOwner,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        var owner = ValidateLeaseOwner(leaseOwner);
        var command = new RedisWebhookCommand(
            RedisWebhookOperation.MarkProcessed,
            MarkProcessedScript,
            [BuildRecordKey(id), _indexKey],
            [owner, processedAt.ToString("O", CultureInfo.InvariantCulture)]);
        var result = await ExecuteStateCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return result == 1;
    }

    /// <summary>Marks an owned processing record as failed with a normalized safe reason.</summary>
    /// <param name="webhookId">Canonical ULID transmission identifier.</param>
    /// <param name="leaseOwner">Current lease owner.</param>
    /// <param name="failedAt">Failure timestamp.</param>
    /// <param name="failureReason">Optional code-like reason; unsafe values are normalized.</param>
    /// <param name="cancellationToken">Token used to cancel the Redis operation.</param>
    /// <returns><see langword="true"/> when the transition was applied.</returns>
    public async ValueTask<bool> MarkFailedAsync(
        string webhookId,
        string leaseOwner,
        DateTimeOffset failedAt,
        string? failureReason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        var owner = ValidateLeaseOwner(leaseOwner);
        var safeReason = NormalizeFailureReason(failureReason);
        var safeCode = safeReason ?? "processing-failed";
        var command = new RedisWebhookCommand(
            RedisWebhookOperation.MarkFailed,
            MarkFailedScript,
            [BuildRecordKey(id), _indexKey],
            [owner, failedAt.ToString("O", CultureInfo.InvariantCulture), safeReason ?? string.Empty, safeCode]);
        var result = await ExecuteStateCommandAsync(command, cancellationToken).ConfigureAwait(false);
        return result == 1;
    }

    /// <summary>Gets received records and expired leases from the Redis recovery index.</summary>
    /// <param name="now">Current time used for lease expiry.</param>
    /// <param name="expiredLeaseAge">Minimum lease age before recovery.</param>
    /// <param name="limit">Maximum records to return.</param>
    /// <param name="cancellationToken">Token used to cancel Redis operations.</param>
    /// <returns>Detached recoverable records.</returns>
    public async ValueTask<IReadOnlyList<WebhookRecord>> GetRecoverableAsync(
        DateTimeOffset now,
        TimeSpan expiredLeaseAge,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(expiredLeaseAge.Ticks);
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

        var ids = await _database.GetSortedSetRangeByScoreAsync(
            _indexKey,
            double.NegativeInfinity,
            cutoff.ToUnixTimeMilliseconds(),
            limit,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (ids.Count == 0)
        {
            return Array.Empty<WebhookRecord>();
        }

        var validatedIds = ids.Select(ValidateWebhookId).ToArray();
        var values = new List<string>(validatedIds.Length + 1)
        {
            cutoff.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
        };
        values.AddRange(validatedIds);
        var command = new RedisWebhookCommand(
            RedisWebhookOperation.Recover,
            RecoverScript,
            validatedIds.Select(BuildRecordKey).Prepend(_indexKey).ToArray(),
            values);
        var result = await _database.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var records = new List<WebhookRecord>(result.Items.Count);
        foreach (var json in result.Items)
        {
            var record = Deserialize(json);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    private string _indexKey => $"{_keyPrefix}:recoverable";

    private static StackExchangeRedisWebhookDatabase CreateDatabase(IConnectionMultiplexer connectionMultiplexer)
    {
        ArgumentNullException.ThrowIfNull(connectionMultiplexer);
        return new StackExchangeRedisWebhookDatabase(connectionMultiplexer.GetDatabase());
    }

    private async ValueTask<long> ExecuteStateCommandAsync(
        RedisWebhookCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _database.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Value == -1)
        {
            throw new InvalidDataException("The stored webhook record is corrupt.");
        }

        return result.Value;
    }

    private string BuildDeduplicationKey(string normalizedProvider, string eventId)
    {
        return $"{_keyPrefix}:dedup:{HashIdentity(normalizedProvider, eventId)}";
    }

    private string BuildDeduplicationKey(string normalizedProvider, RedisWebhookRecordDto dto)
    {
        if (dto.EventId is not null)
        {
            return BuildDeduplicationKey(normalizedProvider, dto.EventId);
        }

        var bodyHash = dto.DeduplicationKey[(normalizedProvider.Length + 8)..];
        return $"{_keyPrefix}:dedup:{HashIdentity(normalizedProvider, $"sha256:{bodyHash}")}";
    }

    private string BuildRecordKey(string webhookId)
    {
        return $"{_keyPrefix}:record:{webhookId}";
    }

    private static RedisWebhookRecordDto ValidateRecord(WebhookRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = ValidateWebhookId(record.Id);
        var provider = NormalizeProvider(record.Provider);
        ValidateRequired(record.HttpMethod, nameof(record.HttpMethod));
        ValidateRequired(record.RequestPath, nameof(record.RequestPath));
        ArgumentNullException.ThrowIfNull(record.Headers);
        ArgumentOutOfRangeException.ThrowIfNegative(record.AttemptCount);
        if (!Enum.IsDefined(record.Status))
        {
            throw new ArgumentException("The processing status is invalid.", nameof(record));
        }

        if (record.ProcessingLeaseOwner is not null)
        {
            ValidateLeaseOwner(record.ProcessingLeaseOwner);
        }

        var canonicalDeduplicationKey = record.EventId is null
            ? ValidateFallbackDeduplicationKey(provider, record.DeduplicationKey)
            : ValidateEventDeduplicationKey(provider, record.DeduplicationKey, record.EventId);
        return RedisWebhookRecordDto.FromRecord(record, id, canonicalDeduplicationKey);
    }

    private static string ValidateEventDeduplicationKey(string provider, string deduplicationKey, string eventId)
    {
        ValidateRequired(eventId, nameof(eventId));
        var validatedEventId = ValidateEventId(eventId);
        var expectedPrefix = $"{provider}:";
        if (string.IsNullOrWhiteSpace(deduplicationKey) ||
            !deduplicationKey.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(deduplicationKey[expectedPrefix.Length..], validatedEventId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The deduplication key does not match the provider and EventId.", nameof(deduplicationKey));
        }

        return $"{provider}:{validatedEventId}";
    }

    private static string ValidateFallbackDeduplicationKey(string provider, string deduplicationKey)
    {
        var prefix = $"{provider}:sha256:";
        if (string.IsNullOrWhiteSpace(deduplicationKey) ||
            !deduplicationKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            deduplicationKey.Length != prefix.Length + 64)
        {
            throw new ArgumentException("A null EventId requires a provider-scoped SHA-256 deduplication key.", nameof(deduplicationKey));
        }

        var suffix = deduplicationKey[prefix.Length..];
        foreach (var character in suffix)
        {
            if (!char.IsAsciiHexDigitLower(character) && !char.IsAsciiDigit(character))
            {
                throw new ArgumentException("A null EventId requires a provider-scoped SHA-256 deduplication key.", nameof(deduplicationKey));
            }
        }

        return prefix + suffix.ToLowerInvariant();
    }

    private static string ValidateWebhookId(string webhookId)
    {
        ValidateRequired(webhookId, nameof(webhookId));
        if (webhookId.Length != 26 || webhookId[0] is < '0' or > '7')
        {
            throw new ArgumentException("WebhookId must be a canonical 26-character ULID.", nameof(webhookId));
        }

        foreach (var character in webhookId)
        {
            if (!IsUlidCharacter(character))
            {
                throw new ArgumentException("WebhookId must be a canonical 26-character ULID.", nameof(webhookId));
            }
        }

        return webhookId;
    }

    private static bool IsUlidCharacter(char character)
    {
        return character is >= '0' and <= '9' or
            >= 'A' and <= 'H' or
            >= 'J' and <= 'K' or
            >= 'M' and <= 'N' or
            >= 'P' and <= 'R' or
            >= 'T' and <= 'V' or
            >= 'X' and <= 'Z';
    }

    private static string ValidateEventId(string eventId)
    {
        ValidateRequired(eventId, nameof(eventId));
        return eventId;
    }

    private static string ValidateLeaseOwner(string leaseOwner)
    {
        ValidateRequired(leaseOwner, nameof(leaseOwner));
        if (leaseOwner.Length > 256)
        {
            throw new ArgumentException("Lease owner must not exceed 256 characters.", nameof(leaseOwner));
        }

        return leaseOwner;
    }

    private static void ValidatePositiveDuration(TimeSpan duration, string parameterName)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static string NormalizeProvider(string provider)
    {
        ValidateRequired(provider, nameof(provider));
        return provider.Trim().ToLowerInvariant();
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

    private static string HashIdentity(string first, string second)
    {
        var identity = $"{first.Length.ToString(CultureInfo.InvariantCulture)}:{first}{second}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static string Serialize(RedisWebhookRecordDto dto)
    {
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static WebhookRecord? Deserialize(string? json)
    {
        if (json is null)
        {
            return null;
        }

        var dto = JsonSerializer.Deserialize<RedisWebhookRecordDto>(json, JsonOptions);
        return dto?.ToRecord();
    }

    private static string GetRecoveryScore(RedisWebhookRecordDto dto)
    {
        return dto.Status switch
        {
            WebhookProcessingStatus.Received => NegativeInfinityScore,
            WebhookProcessingStatus.Processing when !dto.ProcessingLeaseExpiresAt.HasValue => NegativeInfinityScore,
            WebhookProcessingStatus.Processing => dto.ProcessingLeaseExpiresAt!.Value.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            _ => NegativeInfinityScore
        };
    }

    internal static string? NormalizeFailureReason(string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason))
        {
            return null;
        }

        var candidate = failureReason.Trim();
        return IsSafeCode(candidate) ? candidate : "processing-failed";
    }

    internal static bool IsSafeCode(string value)
    {
        if (value.Length > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}

internal interface IRedisWebhookDatabase
{
    ValueTask<string?> GetStringAsync(string key, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<string>> GetSortedSetRangeByScoreAsync(
        string key,
        double minimumScore,
        double maximumScore,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<RedisWebhookCommandResult> ExecuteAsync(
        RedisWebhookCommand command,
        CancellationToken cancellationToken);
}

internal enum RedisWebhookOperation
{
    Create,
    Claim,
    Release,
    MarkProcessed,
    MarkFailed,
    Update,
    Recover
}

internal sealed record RedisWebhookCommand(
    RedisWebhookOperation Operation,
    string Script,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> Values);

internal sealed record RedisWebhookCommandResult(long Value, IReadOnlyList<string> Items);

internal sealed class StackExchangeRedisWebhookDatabase : IRedisWebhookDatabase
{
    private readonly IDatabase _database;

    public StackExchangeRedisWebhookDatabase(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async ValueTask<string?> GetStringAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _database.StringGetAsync(key).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return value.IsNull ? null : value.ToString();
    }

    public async ValueTask<IReadOnlyList<string>> GetSortedSetRangeByScoreAsync(
        string key,
        double minimumScore,
        double maximumScore,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = await _database.SortedSetRangeByScoreAsync(
            key,
            minimumScore,
            maximumScore,
            Exclude.None,
            Order.Ascending,
            0,
            limit).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return values.Select(static value => value.ToString()).ToArray();
    }

    public async ValueTask<RedisWebhookCommandResult> ExecuteAsync(
        RedisWebhookCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        var keys = command.Keys.Select(static key => (RedisKey)key).ToArray();
        var values = command.Values.Select(static value => (RedisValue)value).ToArray();
        var result = await _database.ScriptEvaluateAsync(command.Script, keys, values).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Operation == RedisWebhookOperation.Recover)
        {
            if (result.IsNull)
            {
                return new RedisWebhookCommandResult(0, Array.Empty<string>());
            }

            var items = (RedisResult[]?)result;
            return items is null
                ? new RedisWebhookCommandResult(0, Array.Empty<string>())
                : new RedisWebhookCommandResult(items.Length, items.Select(static item => item.ToString()).ToArray());
        }

        return new RedisWebhookCommandResult((long)result, Array.Empty<string>());
    }
}

internal sealed class RedisWebhookRecordDto
{
    public string Id { get; init; } = string.Empty;

    public string? CorrelationId { get; init; }

    public string Provider { get; init; } = string.Empty;

    public string? EventId { get; init; }

    public string DeduplicationKey { get; init; } = string.Empty;

    public string? EventType { get; init; }

    public string HttpMethod { get; init; } = string.Empty;

    public string RequestPath { get; init; } = string.Empty;

    public Dictionary<string, string[]> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ContentType { get; init; }

    public long? ContentLength { get; init; }

    public byte[]? RawBody { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    public DateTimeOffset? ProviderTimestamp { get; init; }

    public WebhookProcessingStatus Status { get; init; }

    public int AttemptCount { get; init; }

    public DateTimeOffset? LastAttemptAt { get; init; }

    public string? ProcessingLeaseOwner { get; init; }

    public DateTimeOffset? ProcessingLeaseExpiresAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public DateTimeOffset? FailedAt { get; init; }

    public string? FailureReason { get; init; }

    public string? FailureCode { get; init; }

    public static RedisWebhookRecordDto FromRecord(
        WebhookRecord record,
        string id,
        string deduplicationKey)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in record.Headers)
        {
            ArgumentNullException.ThrowIfNull(header.Value);
            headers[header.Key] = (string[])header.Value.Clone();
        }

        var failureReason = RedisWebhookStore.NormalizeFailureReason(record.FailureReason);
        string? failureCode = null;
        if (record.Status == WebhookProcessingStatus.Failed || failureReason is not null || record.FailureCode is not null)
        {
            var candidate = string.IsNullOrWhiteSpace(record.FailureCode) ? failureReason : record.FailureCode.Trim();
            failureCode = candidate is not null && RedisWebhookStore.IsSafeCode(candidate) ? candidate : "processing-failed";
        }

        return new RedisWebhookRecordDto
        {
            Id = id,
            CorrelationId = record.CorrelationId,
            Provider = record.Provider,
            EventId = record.EventId,
            DeduplicationKey = deduplicationKey,
            EventType = record.EventType,
            HttpMethod = record.HttpMethod,
            RequestPath = record.RequestPath,
            Headers = headers,
            ContentType = record.ContentType,
            ContentLength = record.ContentLength,
            RawBody = record.RawBody?.ToArray(),
            ReceivedAt = record.ReceivedAt,
            ProviderTimestamp = record.ProviderTimestamp,
            Status = record.Status,
            AttemptCount = record.AttemptCount,
            LastAttemptAt = record.LastAttemptAt,
            ProcessingLeaseOwner = record.ProcessingLeaseOwner,
            ProcessingLeaseExpiresAt = record.ProcessingLeaseExpiresAt,
            ProcessedAt = record.ProcessedAt,
            FailedAt = record.FailedAt,
            FailureReason = failureReason,
            FailureCode = failureCode
        };
    }

    public WebhookRecord ToRecord()
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in Headers)
        {
            headers[header.Key] = (string[])header.Value.Clone();
        }

        return new WebhookRecord
        {
            Id = Id,
            CorrelationId = CorrelationId,
            Provider = Provider,
            EventId = EventId,
            DeduplicationKey = DeduplicationKey,
            EventType = EventType,
            HttpMethod = HttpMethod,
            RequestPath = RequestPath,
            Headers = new ReadOnlyDictionary<string, string[]>(headers),
            ContentType = ContentType,
            ContentLength = ContentLength,
            RawBody = RawBody?.ToArray(),
            ReceivedAt = ReceivedAt,
            ProviderTimestamp = ProviderTimestamp,
            Status = Status,
            AttemptCount = AttemptCount,
            LastAttemptAt = LastAttemptAt,
            ProcessingLeaseOwner = ProcessingLeaseOwner,
            ProcessingLeaseExpiresAt = ProcessingLeaseExpiresAt,
            ProcessedAt = ProcessedAt,
            FailedAt = FailedAt,
            FailureReason = FailureReason,
            FailureCode = FailureCode
        };
    }
}
