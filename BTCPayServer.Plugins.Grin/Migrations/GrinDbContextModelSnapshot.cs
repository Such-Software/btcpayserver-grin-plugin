using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace BTCPayServer.Plugins.Grin.Migrations;

[DbContext(typeof(GrinDbContext))]
partial class GrinDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("BTCPayServer.Plugins.Grin");

        modelBuilder.HasAnnotation("ProductVersion", "8.0.6")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("BTCPayServer.Plugins.Grin.Data.GrinInvoice", b =>
        {
            b.Property<string>("Id").HasColumnType("text");
            b.Property<long>("AmountNanogrin").HasColumnType("bigint");
            b.Property<int>("Confirmations").HasColumnType("integer");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("IssuedSlatepack").HasColumnType("text");
            b.Property<DateTimeOffset?>("PaidAt").HasColumnType("timestamp with time zone");
            b.Property<string>("SlatepackAddress").HasColumnType("text");
            b.Property<int>("Status").HasColumnType("integer");
            b.Property<string>("StoreId").HasColumnType("text");
            b.Property<string>("TxSlateId").HasColumnType("text");

            b.HasKey("Id");
            b.HasIndex("StoreId");
            b.HasIndex("Status");
            b.HasIndex("TxSlateId");

            b.ToTable("GrinInvoices", "BTCPayServer.Plugins.Grin");
        });

        modelBuilder.Entity("BTCPayServer.Plugins.Grin.Data.GrinStoreSettings", b =>
        {
            b.Property<string>("StoreId").HasColumnType("text");
            b.Property<string>("ApiSecret").HasColumnType("text");
            b.Property<bool>("Enabled").HasColumnType("boolean");
            b.Property<int>("MinConfirmations").HasColumnType("integer");
            b.Property<string>("OwnerApiUrl").HasColumnType("text");
            b.Property<string>("WalletPassword").HasColumnType("text");

            b.HasKey("StoreId");

            b.ToTable("GrinStoreSettings", "BTCPayServer.Plugins.Grin");
        });
    }
}
