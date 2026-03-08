using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Grin.Migrations;

[DbContext(typeof(GrinDbContext))]
[Migration("20260308000000_Initial")]
public partial class Initial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "BTCPayServer.Plugins.Grin");

        migrationBuilder.CreateTable(
            name: "GrinStoreSettings",
            schema: "BTCPayServer.Plugins.Grin",
            columns: table => new
            {
                StoreId = table.Column<string>(type: "text", nullable: false),
                OwnerApiUrl = table.Column<string>(type: "text", nullable: true),
                WalletPassword = table.Column<string>(type: "text", nullable: true),
                ApiSecret = table.Column<string>(type: "text", nullable: true),
                MinConfirmations = table.Column<int>(type: "integer", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GrinStoreSettings", x => x.StoreId);
            });

        migrationBuilder.CreateTable(
            name: "GrinInvoices",
            schema: "BTCPayServer.Plugins.Grin",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                StoreId = table.Column<string>(type: "text", nullable: true),
                TxSlateId = table.Column<string>(type: "text", nullable: true),
                SlatepackAddress = table.Column<string>(type: "text", nullable: true),
                IssuedSlatepack = table.Column<string>(type: "text", nullable: true),
                AmountNanogrin = table.Column<long>(type: "bigint", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Confirmations = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GrinInvoices", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_GrinInvoices_StoreId",
            schema: "BTCPayServer.Plugins.Grin",
            table: "GrinInvoices",
            column: "StoreId");

        migrationBuilder.CreateIndex(
            name: "IX_GrinInvoices_Status",
            schema: "BTCPayServer.Plugins.Grin",
            table: "GrinInvoices",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_GrinInvoices_TxSlateId",
            schema: "BTCPayServer.Plugins.Grin",
            table: "GrinInvoices",
            column: "TxSlateId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "GrinInvoices", schema: "BTCPayServer.Plugins.Grin");
        migrationBuilder.DropTable(name: "GrinStoreSettings", schema: "BTCPayServer.Plugins.Grin");
        migrationBuilder.DropSchema(name: "BTCPayServer.Plugins.Grin");
    }
}
