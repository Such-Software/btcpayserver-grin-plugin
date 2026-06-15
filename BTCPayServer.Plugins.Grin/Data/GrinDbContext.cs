using BTCPayServer.Plugins.Grin.Data;
using BTCPayServer.Plugins.Grin.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Grin;

public class GrinDbContext : DbContext
{
    private readonly bool _designTime;
    private readonly IDataProtector _protector;

    public GrinDbContext(DbContextOptions<GrinDbContext> options, bool designTime = false)
        : this(options, dataProtectionProvider: null, designTime)
    {
    }

    public GrinDbContext(
        DbContextOptions<GrinDbContext> options,
        IDataProtectionProvider dataProtectionProvider,
        bool designTime = false)
        : base(options)
    {
        _designTime = designTime;
        // Encryption purpose string is namespaced so the resulting
        // key ring entry doesn't collide with other plugins'
        // protectors. Changing this string would invalidate every
        // existing encrypted row in the table.
        _protector = dataProtectionProvider?.CreateProtector("BTCPayServer.Plugins.Grin.SecretColumns.v1");
    }

    public DbSet<GrinInvoice> GrinInvoices { get; set; }
    public DbSet<GrinStoreSettings> GrinStoreSettings { get; set; }
    public DbSet<GrinWebhookDelivery> GrinWebhookDeliveries { get; set; }

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

        modelBuilder.Entity<GrinWebhookDelivery>(entity =>
        {
            // Worker query: WHERE Status IN (Pending, Failed)
            // AND NextAttemptAt <= now(). Compound index supports both.
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });
            // For invoice-scoped reporting + the (invoice, eventType)
            // dedup guard at enqueue time.
            entity.HasIndex(e => new { e.InvoiceId, e.EventType });
        });

        modelBuilder.Entity<GrinStoreSettings>(entity =>
        {
            entity.ToTable("GrinStoreSettings");

            // Encrypt-at-rest for sensitive columns. Applied when a
            // protector is available (i.e. runtime). Design-time
            // migrations skip this since they don't have access to
            // the DI key ring — column type stays plain text on disk
            // either way (the converter just wraps the in-memory
            // value with an "enc:v1:" prefix), so migrations don't
            // need to know about it.
            if (_protector != null)
            {
                var converter = new EncryptedColumnConverter(_protector);
                entity.Property(e => e.WalletPassword).HasConversion(converter);
                entity.Property(e => e.ApiSecret).HasConversion(converter);
                entity.Property(e => e.WebhookSecret).HasConversion(converter);
            }
        });
    }
}
