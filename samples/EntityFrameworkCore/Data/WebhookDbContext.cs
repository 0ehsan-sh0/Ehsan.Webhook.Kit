using Microsoft.EntityFrameworkCore;
using WebhookKit.EntityFrameworkCore;

namespace EntityFrameworkCoreSample.Data;

public sealed class WebhookDbContext(DbContextOptions<WebhookDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyWebhookConfiguration();
    }
}
