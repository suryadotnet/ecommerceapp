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
}
