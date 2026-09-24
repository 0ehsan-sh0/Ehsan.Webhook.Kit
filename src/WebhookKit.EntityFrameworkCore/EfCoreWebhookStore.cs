using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebhookKit.Abstractions;

namespace WebhookKit.EntityFrameworkCore;

/// <summary>Entity Framework Core persistence store for webhook records and processing leases.</summary>
/// <typeparam name="TContext">Application DbContext type containing <see cref="WebhookEntity"/>.</typeparam>
public sealed class EfCoreWebhookStore<TContext> : IWebhookStore
    where TContext : DbContext
{
    private static readonly JsonSerializerOptions HeaderJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TContext _context;
    private readonly IWebhookClock _clock;
    private readonly IWebhookUniqueConstraintDetector _uniqueConstraintDetector;

    /// <summary>Creates a store using the default provider-neutral unique-constraint detector.</summary>
    /// <param name="context">The application EF Core context.</param>
    /// <param name="clock">Clock used for lease timestamps.</param>
    public EfCoreWebhookStore(TContext context, IWebhookClock clock)
        : this(context, clock, new ProviderNeutralWebhookUniqueConstraintDetector())
    {
    }

    /// <summary>Creates a store with a custom unique-constraint detector.</summary>
    /// <param name="context">The application EF Core context.</param>
    /// <param name="clock">Clock used for lease timestamps.</param>
    /// <param name="uniqueConstraintDetector">Detector used to translate provider-specific uniqueness errors.</param>
    public EfCoreWebhookStore(
        TContext context,
        IWebhookClock clock,
        IWebhookUniqueConstraintDetector uniqueConstraintDetector)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _uniqueConstraintDetector = uniqueConstraintDetector ?? throw new ArgumentNullException(nameof(uniqueConstraintDetector));
    }

    private DbSet<WebhookEntity> Entities => _context.Set<WebhookEntity>();

    /// <summary>Gets a record by provider and event ID without tracking the entity.</summary>
    /// <param name="provider">Provider name normalized by the store.</param>
    /// <param name="eventId">Provider event ID.</param>
    /// <param name="cancellationToken">Token used to cancel the database query.</param>
    /// <returns>A detached record, or <see langword="null"/> when absent.</returns>
    public async ValueTask<WebhookRecord?> GetAsync(
        string provider,
        string eventId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedProvider = NormalizeProvider(provider);
        var normalizedEventId = ValidateEventId(eventId);
        var key = BuildEventKey(normalizedProvider, normalizedEventId);
        var entity = await Entities.AsNoTracking()
            .SingleOrDefaultAsync(item => item.DeduplicationKey == key, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return entity is null ? null : ToRecord(entity);
    }

    /// <summary>Attempts to insert a record and translates a unique-key conflict to a duplicate result.</summary>
    /// <param name="record">Record to persist.</param>
    /// <param name="cancellationToken">Token used to cancel the database operation.</param>
    /// <returns><see langword="true"/> when inserted; <see langword="false"/> for a uniqueness conflict.</returns>
    public async ValueTask<bool> TryCreateAsync(
        WebhookRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = ToEntity(ValidateRecord(record));
        try
        {
            Entities.Add(entity);
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (DbUpdateException exception)
        {
            Detach(entity);
            if (_uniqueConstraintDetector.IsUniqueConstraintViolation(exception))
            {
                return false;
            }

            throw;
        }
        catch
        {
            Detach(entity);
            throw;
        }
    }

    /// <summary>Persists a record update using relational or non-relational concurrency rules.</summary>
    /// <param name="record">Updated record snapshot.</param>
    /// <param name="cancellationToken">Token used to cancel the database operation.</param>
    /// <returns>A task that completes when the update is applied or ignored.</returns>
    public async ValueTask UpdateAsync(
        WebhookRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var incoming = ValidateRecord(record);
        if (_context.Database.IsRelational())
        {
            await UpdateRelationalAsync(incoming, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await UpdateNonRelationalAsync(incoming, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Gets a record by Webhook ID without tracking the entity.</summary>
    /// <param name="webhookId">Transmission identifier.</param>
    /// <param name="cancellationToken">Token used to cancel the database query.</param>
    /// <returns>A detached record, or <see langword="null"/> when absent.</returns>
    public async ValueTask<WebhookRecord?> GetByWebhookIdAsync(
        string webhookId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        var entity = await Entities.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return entity is null ? null : ToRecord(entity);
    }

    /// <summary>Atomically claims a received or expired processing record using the configured concurrency strategy.</summary>
    /// <param name="webhookId">Transmission identifier.</param>
    /// <param name="leaseOwner">Owner recorded for the lease.</param>
    /// <param name="leaseDuration">Positive lease duration.</param>
    /// <param name="cancellationToken">Token used to cancel the database operation.</param>
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
        ValidateLeaseDuration(leaseDuration);
        var now = _clock.UtcNow;
        var expiresAt = AddLeaseExpiry(now, leaseDuration);

        if (_context.Database.IsRelational())
        {
            var current = await Entities.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (current is null || !IsClaimable(current, now))
            {
                return false;
            }

            var affected = await Entities
                .Where(item => item.Id == id && item.Version == current.Version && item.Status == current.Status)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, WebhookProcessingStatus.Processing)
                        .SetProperty(item => item.ProcessingLeaseOwner, owner)
                        .SetProperty(item => item.ProcessingLeaseExpiresAt, expiresAt)
                        .SetProperty(item => item.ProcessingLeaseExpiresAtTicks, expiresAt.UtcTicks)
                        .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1)
                        .SetProperty(item => item.LastAttemptAt, now)
                        .SetProperty(item => item.ProcessedAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.FailedAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.FailureReason, (string?)null)
                        .SetProperty(item => item.FailureCode, (string?)null)
                        .SetProperty(item => item.Version, item => item.Version + 1),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return affected == 1;
        }

        var entity = await Entities.SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (entity is null || !IsClaimable(entity, now))
        {
            return false;
        }

        entity.Status = WebhookProcessingStatus.Processing;
        entity.ProcessingLeaseOwner = owner;
        entity.ProcessingLeaseExpiresAt = expiresAt;
        entity.ProcessingLeaseExpiresAtTicks = expiresAt.UtcTicks;
        entity.AttemptCount = checked(entity.AttemptCount + 1);
        entity.LastAttemptAt = now;
        entity.ProcessedAt = null;
        entity.FailedAt = null;
        entity.FailureReason = null;
        entity.FailureCode = null;
        entity.Version = checked(entity.Version + 1);
        return await SaveNonRelationalAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases an owned processing lease.</summary>
    /// <param name="webhookId">Transmission identifier.</param>
    /// <param name="leaseOwner">Current lease owner.</param>
    /// <param name="cancellationToken">Token used to cancel the database operation.</param>
    /// <returns><see langword="true"/> when the owned lease was released.</returns>
    public async ValueTask<bool> ReleaseAsync(
        string webhookId,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = ValidateWebhookId(webhookId);
        var owner = ValidateLeaseOwner(leaseOwner);
        if (_context.Database.IsRelational())
        {
            var affected = await Entities
                .Where(item => item.Id == id &&
                    item.Status == WebhookProcessingStatus.Processing &&
                    item.ProcessingLeaseOwner == owner)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, WebhookProcessingStatus.Received)
                        .SetProperty(item => item.ProcessingLeaseOwner, (string?)null)
                        .SetProperty(item => item.ProcessingLeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.ProcessingLeaseExpiresAtTicks, (long?)null)
                        .SetProperty(item => item.ProcessedAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.FailedAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.FailureReason, (string?)null)
                        .SetProperty(item => item.FailureCode, (string?)null)
                        .SetProperty(item => item.Version, item => item.Version + 1),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return affected == 1;
        }

        var entity = await Entities.SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (entity is null || !IsOwnedProcessingRecord(entity, owner))
        {
            return false;
        }

        entity.Status = WebhookProcessingStatus.Received;
        entity.ProcessingLeaseOwner = null;
        entity.ProcessingLeaseExpiresAt = null;
        entity.ProcessingLeaseExpiresAtTicks = null;
        entity.ProcessedAt = null;
        entity.FailedAt = null;
        entity.FailureReason = null;
        entity.FailureCode = null;
        entity.Version = checked(entity.Version + 1);
        return await SaveNonRelationalAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks an owned processing record as processed.</summary>
    /// <param name="webhookId">Transmission identifier.</param>
    /// <param name="leaseOwner">Current lease owner.</param>
    /// <param name="processedAt">Completion timestamp.</param>
    /// <param name="cancellationToken">Token used to cancel the database operation.</param>
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
        if (_context.Database.IsRelational())
        {
            var affected = await Entities
                .Where(item => item.Id == id &&
                    item.Status == WebhookProcessingStatus.Processing &&
                    item.ProcessingLeaseOwner == owner)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, WebhookProcessingStatus.Processed)
                        .SetProperty(item => item.ProcessedAt, processedAt)
                        .SetProperty(item => item.FailedAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.FailureReason, (string?)null)
                        .SetProperty(item => item.FailureCode, (string?)null)
                        .SetProperty(item => item.ProcessingLeaseOwner, (string?)null)
                        .SetProperty(item => item.ProcessingLeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.ProcessingLeaseExpiresAtTicks, (long?)null)
                        .SetProperty(item => item.Version, item => item.Version + 1),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return affected == 1;
        }

        var entity = await Entities.SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (entity is null || !IsOwnedProcessingRecord(entity, owner))
        {
            return false;
        }

        entity.Status = WebhookProcessingStatus.Processed;
        entity.ProcessedAt = processedAt;
        entity.FailedAt = null;
        entity.FailureReason = null;
        entity.FailureCode = null;
        entity.ProcessingLeaseOwner = null;
        entity.ProcessingLeaseExpiresAt = null;
        entity.ProcessingLeaseExpiresAtTicks = null;
        entity.Version = checked(entity.Version + 1);
        return await SaveNonRelationalAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks an owned processing record as failed with a normalized safe reason.</summary>
    /// <param name="webhookId">Transmission identifier.</param>
    /// <param name="leaseOwner">Current lease owner.</param>
    /// <param name="failedAt">Failure timestamp.</param>
    /// <param name="failureReason">Optional code-like reason; unsafe values are normalized.</param>
    /// <param name="cancellationToken">Token used to cancel the database operation.</param>
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
        if (_context.Database.IsRelational())
        {
            var affected = await Entities
                .Where(item => item.Id == id &&
                    item.Status == WebhookProcessingStatus.Processing &&
                    item.ProcessingLeaseOwner == owner)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, WebhookProcessingStatus.Failed)
                        .SetProperty(item => item.FailedAt, failedAt)
                        .SetProperty(item => item.ProcessedAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.FailureReason, safeReason)
                        .SetProperty(item => item.FailureCode, safeCode)
                        .SetProperty(item => item.ProcessingLeaseOwner, (string?)null)
                        .SetProperty(item => item.ProcessingLeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(item => item.ProcessingLeaseExpiresAtTicks, (long?)null)
                        .SetProperty(item => item.Version, item => item.Version + 1),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return affected == 1;
        }

        var entity = await Entities.SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (entity is null || !IsOwnedProcessingRecord(entity, owner))
        {
            return false;
        }

        entity.Status = WebhookProcessingStatus.Failed;
        entity.FailedAt = failedAt;
        entity.ProcessedAt = null;
        entity.FailureReason = safeReason;
        entity.FailureCode = safeCode;
        entity.ProcessingLeaseOwner = null;
        entity.ProcessingLeaseExpiresAt = null;
        entity.ProcessingLeaseExpiresAtTicks = null;
        entity.Version = checked(entity.Version + 1);
        return await SaveNonRelationalAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets received records and expired leases eligible for recovery.</summary>
    /// <param name="now">Current time used for lease expiry.</param>
    /// <param name="expiredLeaseAge">Minimum lease age before recovery.</param>
    /// <param name="limit">Maximum records to return.</param>
    /// <param name="cancellationToken">Token used to cancel the database query.</param>
    /// <returns>Detached records ordered by receipt time.</returns>
    public async ValueTask<IReadOnlyList<WebhookRecord>> GetRecoverableAsync(
        DateTimeOffset now,
        TimeSpan expiredLeaseAge,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(expiredLeaseAge.Ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var cutoff = SubtractRecoveryAge(now, expiredLeaseAge);
        var entities = await Entities.AsNoTracking()
            .Where(item => item.Status == WebhookProcessingStatus.Received ||
                (item.Status == WebhookProcessingStatus.Processing &&
                 (!item.ProcessingLeaseExpiresAtTicks.HasValue || item.ProcessingLeaseExpiresAtTicks <= cutoff.UtcTicks)))
            .OrderBy(item => item.ReceivedAtTicks)
            .ThenBy(item => item.Id)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return entities.Select(ToRecord).ToArray();
    }

    private async ValueTask UpdateRelationalAsync(
        ValidatedRecord incoming,
        CancellationToken cancellationToken)
    {
        var current = await Entities.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == incoming.Id, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (current is null)
        {
            throw new KeyNotFoundException($"Webhook record '{incoming.Id}' was not found.");
        }

        if (!CanUpdate(current, incoming))
        {
            return;
        }

        var headersJson = SerializeHeaders(incoming.Headers);
        var rawBody = incoming.RawBody?.ToArray();
        var receivedAt = incoming.ReceivedAt;
        var receivedAtTicks = receivedAt.UtcTicks;
        var leaseExpiresAtTicks = incoming.ProcessingLeaseExpiresAt.HasValue
            ? incoming.ProcessingLeaseExpiresAt.Value.UtcTicks
            : (long?)null;
        var affected = await Entities
            .Where(item => item.Id == incoming.Id && item.Version == current.Version)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.CorrelationId, incoming.CorrelationId)
                    .SetProperty(item => item.Provider, incoming.Provider)
                    .SetProperty(item => item.EventId, incoming.EventId)
                    .SetProperty(item => item.DeduplicationKey, incoming.DeduplicationKey)
                    .SetProperty(item => item.EventType, incoming.EventType)
                    .SetProperty(item => item.HttpMethod, incoming.HttpMethod)
                    .SetProperty(item => item.RequestPath, incoming.RequestPath)
                    .SetProperty(item => item.HeadersJson, headersJson)
                    .SetProperty(item => item.ContentType, incoming.ContentType)
                    .SetProperty(item => item.ContentLength, incoming.ContentLength)
                    .SetProperty(item => item.RawBody, rawBody)
                    .SetProperty(item => item.ReceivedAt, receivedAt)
                    .SetProperty(item => item.ReceivedAtTicks, receivedAtTicks)
                    .SetProperty(item => item.ProviderTimestamp, incoming.ProviderTimestamp)
                    .SetProperty(item => item.Status, incoming.Status)
                    .SetProperty(item => item.AttemptCount, incoming.AttemptCount)
                    .SetProperty(item => item.LastAttemptAt, incoming.LastAttemptAt)
                    .SetProperty(item => item.ProcessingLeaseOwner, incoming.ProcessingLeaseOwner)
                    .SetProperty(item => item.ProcessingLeaseExpiresAt, incoming.ProcessingLeaseExpiresAt)
                    .SetProperty(item => item.ProcessingLeaseExpiresAtTicks, leaseExpiresAtTicks)
                    .SetProperty(item => item.ProcessedAt, incoming.ProcessedAt)
                    .SetProperty(item => item.FailedAt, incoming.FailedAt)
                    .SetProperty(item => item.FailureReason, incoming.FailureReason)
                    .SetProperty(item => item.FailureCode, incoming.FailureCode)
                    .SetProperty(item => item.Version, item => item.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _ = affected;
    }

    private async ValueTask UpdateNonRelationalAsync(
        ValidatedRecord incoming,
        CancellationToken cancellationToken)
    {
        var current = await Entities.SingleOrDefaultAsync(item => item.Id == incoming.Id, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (current is null)
        {
            throw new KeyNotFoundException($"Webhook record '{incoming.Id}' was not found.");
        }

        if (!CanUpdate(current, incoming))
        {
            return;
        }

        ApplyEntityValues(current, incoming);
        await SaveNonRelationalAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> SaveNonRelationalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    private static bool CanUpdate(WebhookEntity current, ValidatedRecord incoming)
    {
        if (!string.Equals(current.Provider, incoming.Provider, StringComparison.Ordinal) ||
            !string.Equals(current.DeduplicationKey, incoming.DeduplicationKey, StringComparison.Ordinal))
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

    private static void ApplyEntityValues(WebhookEntity target, ValidatedRecord source)
    {
        target.CorrelationId = source.CorrelationId;
        target.Provider = source.Provider;
        target.EventId = source.EventId;
        target.DeduplicationKey = source.DeduplicationKey;
        target.EventType = source.EventType;
        target.HttpMethod = source.HttpMethod;
        target.RequestPath = source.RequestPath;
        target.HeadersJson = SerializeHeaders(source.Headers);
        target.ContentType = source.ContentType;
        target.ContentLength = source.ContentLength;
        target.RawBody = source.RawBody?.ToArray();
        target.ReceivedAt = source.ReceivedAt;
        target.ProviderTimestamp = source.ProviderTimestamp;
        target.ReceivedAtTicks = source.ReceivedAt.UtcTicks;
        target.Status = source.Status;
        target.AttemptCount = source.AttemptCount;
        target.LastAttemptAt = source.LastAttemptAt;
        target.ProcessingLeaseOwner = source.ProcessingLeaseOwner;
        target.ProcessingLeaseExpiresAt = source.ProcessingLeaseExpiresAt;
        target.ProcessingLeaseExpiresAtTicks = source.ProcessingLeaseExpiresAt?.UtcTicks;
        target.ProcessedAt = source.ProcessedAt;
        target.FailedAt = source.FailedAt;
        target.FailureReason = source.FailureReason;
        target.FailureCode = source.FailureCode;
        target.Version = checked(target.Version + 1);
    }

    private static bool IsClaimable(WebhookEntity entity, DateTimeOffset now)
    {
        return entity.Status == WebhookProcessingStatus.Received ||
            (entity.Status == WebhookProcessingStatus.Processing &&
             (!entity.ProcessingLeaseExpiresAt.HasValue || entity.ProcessingLeaseExpiresAt <= now));
    }

    private static bool IsOwnedProcessingRecord(WebhookEntity entity, string owner)
    {
        return entity.Status == WebhookProcessingStatus.Processing &&
            string.Equals(entity.ProcessingLeaseOwner, owner, StringComparison.Ordinal);
    }

    private static bool IsTerminal(WebhookProcessingStatus status)
    {
        return status is WebhookProcessingStatus.Processed or
            WebhookProcessingStatus.Failed or
            WebhookProcessingStatus.Ignored or
            WebhookProcessingStatus.Duplicate;
    }

    private static DateTimeOffset AddLeaseExpiry(DateTimeOffset now, TimeSpan leaseDuration)
    {
        try
        {
            return now.Add(leaseDuration);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }

    private static DateTimeOffset SubtractRecoveryAge(DateTimeOffset now, TimeSpan expiredLeaseAge)
    {
        try
        {
            return now.Subtract(expiredLeaseAge);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static WebhookEntity ToEntity(ValidatedRecord source)
    {
        return new WebhookEntity
        {
            Id = source.Id,
            CorrelationId = source.CorrelationId,
            Provider = source.Provider,
            EventId = source.EventId,
            DeduplicationKey = source.DeduplicationKey,
            EventType = source.EventType,
            HttpMethod = source.HttpMethod,
            RequestPath = source.RequestPath,
            HeadersJson = SerializeHeaders(source.Headers),
            ContentType = source.ContentType,
            ContentLength = source.ContentLength,
            RawBody = source.RawBody?.ToArray(),
            ReceivedAt = source.ReceivedAt,
            ReceivedAtTicks = source.ReceivedAt.UtcTicks,
            ProviderTimestamp = source.ProviderTimestamp,
            Status = source.Status,
            AttemptCount = source.AttemptCount,
            LastAttemptAt = source.LastAttemptAt,
            ProcessingLeaseOwner = source.ProcessingLeaseOwner,
            ProcessingLeaseExpiresAt = source.ProcessingLeaseExpiresAt,
            ProcessingLeaseExpiresAtTicks = source.ProcessingLeaseExpiresAt?.UtcTicks,
            ProcessedAt = source.ProcessedAt,
            FailedAt = source.FailedAt,
            FailureReason = source.FailureReason,
            FailureCode = source.FailureCode,
            Version = 0
        };
    }

    private static WebhookRecord ToRecord(WebhookEntity source)
    {
        return new WebhookRecord
        {
            Id = source.Id,
            CorrelationId = source.CorrelationId,
            Provider = source.Provider,
            EventId = source.EventId,
            DeduplicationKey = source.DeduplicationKey,
            EventType = source.EventType,
            HttpMethod = source.HttpMethod,
            RequestPath = source.RequestPath,
            Headers = DeserializeHeaders(source.HeadersJson),
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
            FailureReason = source.FailureReason,
            FailureCode = source.FailureCode
        };
    }

    private static string SerializeHeaders(IReadOnlyDictionary<string, string[]> headers)
    {
        return JsonSerializer.Serialize(headers, HeaderJsonOptions);
    }

    private static ReadOnlyDictionary<string, string[]> DeserializeHeaders(string json)
    {
        Dictionary<string, string[]>? headers;
        try
        {
            headers = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json, HeaderJsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Stored webhook headers are invalid.", exception);
        }

        if (headers is null)
        {
            throw new InvalidDataException("Stored webhook headers are invalid.");
        }

        var copy = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (header.Value is null)
            {
                throw new InvalidDataException("Stored webhook headers are invalid.");
            }

            copy[header.Key] = header.Value.ToArray();
        }

        return new ReadOnlyDictionary<string, string[]>(copy);
    }

    private static ValidatedRecord ValidateRecord(WebhookRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var id = ValidateRequired(record.Id, nameof(record.Id), 128);
        var provider = NormalizeProvider(record.Provider);
        var httpMethod = ValidateRequired(record.HttpMethod, nameof(record.HttpMethod), 32);
        var requestPath = ValidateRequired(record.RequestPath, nameof(record.RequestPath), 2048);
        if (record.ContentLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record));
        }

        if (!Enum.IsDefined(record.Status))
        {
            throw new ArgumentException("The processing status is invalid.", nameof(record));
        }

        if (record.AttemptCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record));
        }

        var owner = record.ProcessingLeaseOwner is null
            ? null
            : ValidateRequired(record.ProcessingLeaseOwner, nameof(record.ProcessingLeaseOwner), 256);
        var headers = CloneHeaders(record.Headers);
        var key = ValidateDeduplicationKey(provider, record.EventId, record.DeduplicationKey);
        var safeReason = NormalizeFailureReason(record.FailureReason);
        var safeCode = NormalizeFailureCode(record.FailureCode, safeReason, record.Status);
        return new ValidatedRecord(
            id,
            record.CorrelationId,
            provider,
            record.EventId,
            key,
            record.EventType,
            httpMethod,
            requestPath,
            headers,
            record.ContentType,
            record.ContentLength,
            record.RawBody?.ToArray(),
            record.ReceivedAt,
            record.ProviderTimestamp,
            record.Status,
            record.AttemptCount,
            record.LastAttemptAt,
            owner,
            record.ProcessingLeaseExpiresAt,
            record.ProcessedAt,
            record.FailedAt,
            safeReason,
            safeCode);
    }

    private static Dictionary<string, string[]> CloneHeaders(IReadOnlyDictionary<string, string[]> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var copy = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            ValidateRequired(header.Key, nameof(headers), 512);
            ArgumentNullException.ThrowIfNull(header.Value);
            copy[header.Key] = header.Value.ToArray();
        }

        return copy;
    }

    private static string ValidateDeduplicationKey(string provider, string? eventId, string deduplicationKey)
    {
        ValidateRequired(deduplicationKey, nameof(deduplicationKey), 512);
        if (eventId is null)
        {
            var fallbackPrefix = $"{provider}:sha256:";
            if (!deduplicationKey.StartsWith(fallbackPrefix, StringComparison.OrdinalIgnoreCase) ||
                deduplicationKey.Length != fallbackPrefix.Length + 64)
            {
                throw new ArgumentException("A null EventId requires a provider-scoped SHA-256 deduplication key.", nameof(deduplicationKey));
            }

            var suffix = deduplicationKey[fallbackPrefix.Length..];
            foreach (var character in suffix)
            {
                if (!char.IsAsciiHexDigit(character))
                {
                    throw new ArgumentException("A null EventId requires a provider-scoped SHA-256 deduplication key.", nameof(deduplicationKey));
                }
            }

            return fallbackPrefix + suffix.ToLowerInvariant();
        }

        var normalizedEventId = ValidateEventId(eventId);
        var prefix = $"{provider}:";
        if (!deduplicationKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(deduplicationKey[prefix.Length..], normalizedEventId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The deduplication key does not match the provider and EventId.", nameof(deduplicationKey));
        }

        var canonicalKey = prefix + normalizedEventId;
        if (canonicalKey.Length > 512)
        {
            throw new ArgumentException("The deduplication key is too long.", nameof(deduplicationKey));
        }

        return canonicalKey;
    }

    private static string NormalizeProvider(string provider)
    {
        return ValidateRequired(provider, nameof(provider), 128).Trim().ToLowerInvariant();
    }

    private static string BuildEventKey(string provider, string eventId)
    {
        return $"{provider}:{eventId}";
    }

    private static string ValidateEventId(string eventId)
    {
        return ValidateRequired(eventId, nameof(eventId), 512);
    }

    private static string ValidateWebhookId(string webhookId)
    {
        return ValidateRequired(webhookId, nameof(webhookId), 128);
    }

    private static string ValidateLeaseOwner(string leaseOwner)
    {
        return ValidateRequired(leaseOwner, nameof(leaseOwner), 256);
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseDuration.Ticks);
    }

    private static string ValidateRequired(string? value, string parameterName, int maxLength)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must be non-empty.", parameterName);
        }

        if (value.Length > maxLength)
        {
            throw new ArgumentException($"Value must not exceed {maxLength} characters.", parameterName);
        }

        return value;
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

    private static string? NormalizeFailureCode(string? failureCode, string? failureReason, WebhookProcessingStatus status)
    {
        if (status != WebhookProcessingStatus.Failed && failureCode is null && failureReason is null)
        {
            return null;
        }

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
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private void Detach(WebhookEntity entity)
    {
        if (_context.Entry(entity).State != EntityState.Detached)
        {
            _context.Entry(entity).State = EntityState.Detached;
        }
    }

    private sealed record ValidatedRecord(
        string Id,
        string? CorrelationId,
        string Provider,
        string? EventId,
        string DeduplicationKey,
        string? EventType,
        string HttpMethod,
        string RequestPath,
        IReadOnlyDictionary<string, string[]> Headers,
        string? ContentType,
        long? ContentLength,
        byte[]? RawBody,
        DateTimeOffset ReceivedAt,
        DateTimeOffset? ProviderTimestamp,
        WebhookProcessingStatus Status,
        int AttemptCount,
        DateTimeOffset? LastAttemptAt,
        string? ProcessingLeaseOwner,
        DateTimeOffset? ProcessingLeaseExpiresAt,
        DateTimeOffset? ProcessedAt,
        DateTimeOffset? FailedAt,
        string? FailureReason,
        string? FailureCode);
}

/// <summary>Detects provider-specific unique constraint failures for atomic deduplication.</summary>
public interface IWebhookUniqueConstraintDetector
{
    /// <summary>Determines whether a database update failed because a unique key already exists.</summary>
    /// <param name="exception">The update exception to inspect.</param>
    /// <returns><see langword="true"/> when the exception represents a uniqueness conflict.</returns>
    bool IsUniqueConstraintViolation(DbUpdateException exception);
}

/// <summary>Recognizes common SQL Server, PostgreSQL, MySQL, and SQLite uniqueness errors.</summary>
public sealed class ProviderNeutralWebhookUniqueConstraintDetector : IWebhookUniqueConstraintDetector
{
    /// <summary>Determines whether an update exception represents a unique constraint conflict.</summary>
    /// <param name="exception">The update exception to inspect.</param>
    /// <returns><see langword="true"/> when a recognized provider error is found.</returns>
    public bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = (Exception?)exception; current is not null; current = current.InnerException)
        {
            if (IsRecognizedUniqueException(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRecognizedUniqueException(Exception exception)
    {
        var typeName = exception.GetType().FullName ?? exception.GetType().Name;
        if (typeName.Contains("SqliteException", StringComparison.Ordinal))
        {
            var extendedErrorCode = GetInt32(exception, "SqliteExtendedErrorCode");
            return extendedErrorCode is 1555 or 2067 ||
                exception.Message.StartsWith("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
        }

        if (typeName.Contains("PostgresException", StringComparison.Ordinal))
        {
            return string.Equals(GetString(exception, "SqlState"), "23505", StringComparison.Ordinal);
        }

        if (typeName.Contains("MySqlException", StringComparison.Ordinal))
        {
            return GetInt32(exception, "Number") == 1062;
        }

        if (typeName.Contains("SqlException", StringComparison.Ordinal))
        {
            return GetInt32(exception, "Number") is 2601 or 2627;
        }

        return false;
    }

    private static int? GetInt32(Exception exception, string propertyName)
    {
        var property = exception.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        var value = property?.GetValue(exception);
        return value switch
        {
            int integer => integer,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            _ => null
        };
    }

    private static string? GetString(Exception exception, string propertyName)
    {
        var property = exception.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(exception) as string;
    }
}
