using BTCPayServer.Plugins.Grin.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Grin;

public class GrinDbContext : DbContext
{
    private readonly bool _designTime;

    public GrinDbContext(DbContextOptions<GrinDbContext> options, bool designTime = false)
        : base(options)
    {
        _designTime = designTime;
    }

    public DbSet<GrinInvoice> GrinInvoices { get; set; }
    public DbSet<GrinStoreSettings> GrinStoreSettings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.Grin");

        modelBuilder.Entity<GrinInvoice>(entity =>
        {
            entity.HasIndex(e => e.TxSlateId);
            entity.HasIndex(e => e.StoreId);
            entity.HasIndex(e => e.Status);
        });

        modelBuilder.Entity<GrinStoreSettings>(entity =>
        {
            entity.ToTable("GrinStoreSettings");
        });
    }
}
