using Microsoft.EntityFrameworkCore;
using PaymentService.Domain;

namespace PaymentService.Infrastructure;

public sealed class PaymentDbContext : DbContext
{
    public PaymentDbContext(
        DbContextOptions<PaymentDbContext> options)
        : base(options)
    {
    }

    public DbSet<Payment> Payments =>
        Set<Payment>();
    public DbSet<PaymentEventReceipt> EventReceipts => Set<PaymentEventReceipt>();
    public DbSet<PaymentOutboxMessage> OutboxMessages => Set<PaymentOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentEventReceipt>(entity =>
        {
            entity.ToTable("PaymentEventReceipts");
            entity.HasKey(x => x.MessageId);
            entity.Property(x => x.MessageId).HasMaxLength(128);
            entity.Property(x => x.ProcessedAtUtc).IsRequired();
        });

        modelBuilder.Entity<PaymentOutboxMessage>(entity =>
        {
            entity.ToTable("PaymentOutboxMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DestinationName).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Payload).IsRequired();
            entity.Property(x => x.CreatedAtUtc).IsRequired();
            entity.Property(x => x.NextAttemptAtUtc).IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(4000);
            entity.HasIndex(x => new { x.ProcessedAtUtc, x.NextAttemptAtUtc });
        });
    }
}