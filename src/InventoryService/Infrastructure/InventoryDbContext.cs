using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Infrastructure;
public sealed class InventoryDbContext : DbContext
{
    public InventoryDbContext(
        DbContextOptions<InventoryDbContext> options)
        : base(options)
    {
    }

    public DbSet<ProductStock> Products => Set<ProductStock>();
    public DbSet<EmailNotification> EmailNotification => Set<EmailNotification>();
    public DbSet<OrderCreatedReceipt> OrderCreatedReceipts => Set<OrderCreatedReceipt>();
    public DbSet<PaymentInfoReceipt> PaymentInfoReceipts => Set<PaymentInfoReceipt>();
    public DbSet<InventoryReservation> InventoryReservations => Set<InventoryReservation>();
    public DbSet<InventoryEventReceipt> EventReceipts => Set<InventoryEventReceipt>();
    public DbSet<InventoryOutboxMessage> OutboxMessages => Set<InventoryOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderCreatedReceipt>(entity =>
        {
            entity.ToTable("OrderCreatedReceipts");
            entity.HasKey(x => x.MessageId);
            entity.Property(x => x.MessageId).HasMaxLength(128);
            entity.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<PaymentInfoReceipt>(entity =>
        {
            entity.ToTable("PaymentInfoReceipts");
            entity.HasKey(x => x.MessageId);
            entity.Property(x => x.MessageId).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<InventoryReservation>(entity =>
        {
            entity.ToTable("InventoryReservations");
            entity.HasKey(x => new { x.ReservationId, x.ProductId });
            entity.HasIndex(x => x.OrderId);
            entity.Property(x => x.Quantity).IsRequired();
            entity.Property(x => x.CreatedAtUtc).IsRequired();
        });

        modelBuilder.Entity<InventoryEventReceipt>(entity =>
        {
            entity.ToTable("InventoryEventReceipts");
            entity.HasKey(x => x.MessageId);
            entity.Property(x => x.MessageId).HasMaxLength(128);
            entity.Property(x => x.ProcessedAtUtc).IsRequired();
        });

        modelBuilder.Entity<InventoryOutboxMessage>(entity =>
        {
            entity.ToTable("InventoryOutboxMessages");
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
