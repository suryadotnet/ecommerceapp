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
    }
}
