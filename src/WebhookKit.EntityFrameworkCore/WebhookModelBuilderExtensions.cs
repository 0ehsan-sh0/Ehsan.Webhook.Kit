using Microsoft.EntityFrameworkCore;
using WebhookKit.Abstractions;

namespace WebhookKit.EntityFrameworkCore;

public static class WebhookModelBuilderExtensions
{
    public static ModelBuilder ApplyWebhookConfiguration(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var entity = modelBuilder.Entity<WebhookEntity>();
        entity.ToTable("WebhookEntities");
        entity.HasKey(item => item.Id);

        entity.Property(item => item.Id).HasMaxLength(128).IsRequired();
        entity.Property(item => item.CorrelationId).HasMaxLength(256);
        entity.Property(item => item.Provider).HasMaxLength(128).IsRequired();
        entity.Property(item => item.EventId).HasMaxLength(512);
        entity.Property(item => item.DeduplicationKey).HasMaxLength(512).IsRequired();
        entity.Property(item => item.EventType).HasMaxLength(256);
        entity.Property(item => item.HttpMethod).HasMaxLength(32).IsRequired();
        entity.Property(item => item.RequestPath).HasMaxLength(2048).IsRequired();
        entity.Property(item => item.HeadersJson).IsRequired();
        entity.Property(item => item.ContentType).HasMaxLength(256);
        entity.Property(item => item.ContentLength);
        entity.Property(item => item.RawBody);
        entity.Property(item => item.ReceivedAt).IsRequired();
        entity.Property(item => item.ReceivedAtTicks).IsRequired();
        entity.Property(item => item.ProviderTimestamp);
        entity.Property(item => item.Status).HasConversion<int>().IsRequired();
        entity.Property(item => item.AttemptCount).IsRequired();
        entity.Property(item => item.LastAttemptAt);
        entity.Property(item => item.ProcessingLeaseOwner).HasMaxLength(256);
        entity.Property(item => item.ProcessingLeaseExpiresAt);
        entity.Property(item => item.ProcessingLeaseExpiresAtTicks);
        entity.Property(item => item.ProcessedAt);
        entity.Property(item => item.FailedAt);
        entity.Property(item => item.FailureReason).HasMaxLength(128);
        entity.Property(item => item.FailureCode).HasMaxLength(128);
        entity.Property(item => item.Version).IsConcurrencyToken().ValueGeneratedNever().IsRequired();

        entity.HasIndex(item => item.DeduplicationKey).IsUnique();
        entity.HasIndex(item => new { item.Status, item.ProcessingLeaseExpiresAt, item.ReceivedAt });
        entity.HasIndex(item => new { item.Status, item.ProcessingLeaseExpiresAtTicks, item.ReceivedAtTicks });
        entity.HasIndex(item => new { item.Status, item.ReceivedAtTicks, item.Id });

        return modelBuilder;
    }
}
