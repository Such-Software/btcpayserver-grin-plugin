using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.Grin.Migrations
{
    /// <summary>
    /// Persistent outbound-webhook delivery queue. Replaces the
    /// fire-and-forget HTTP POST that lived inside the dispatch path
    /// — a single non-2xx response from Medusa (or a network blip)
    /// used to mean the merchant never learned the order was paid,
    /// and the only recovery was an operator running SQL by hand
    /// (staging 2026-06-15: 3 stuck Grin orders).
    ///
    /// Schema mirrors BTCPay core's <c>InvoiceWebhookDeliveries</c>
    /// shape (Pending/Failed/Delivered/DeadLetter status, attempt
    /// count, next-attempt timestamp) but stays self-contained in
    /// this plugin's schema so the BTCPay core webhook system doesn't
    /// need to know about Grin's separate invoice table.
    ///
    /// Retry schedule (worker reads NextAttemptAt and updates it on
    /// failure): 0s, 30s, 2m, 10m, 1h, 6h, 24h. After 7 attempts the
    /// row transitions to DeadLetter and is left for operator
    /// intervention. Total window ~32h — long enough to survive a
    /// merchant maintenance window or a Cloudflare 5xx weather
    /// event, short enough that DeadLetter rows surface within a
    /// business day.
    /// </summary>
    [DbContext(typeof(GrinDbContext))]
    [Migration("20260615120000_AddWebhookDeliveries")]
    public partial class AddWebhookDeliveries : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GrinWebhookDeliveries",
                schema: "BTCPayServer.Plugins.Grin",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    InvoiceId = table.Column<string>(type: "text", nullable: false),
                    StoreId = table.Column<string>(type: "text", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastResponseCode = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrinWebhookDeliveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GrinWebhookDeliveries_Status_NextAttemptAt",
                schema: "BTCPayServer.Plugins.Grin",
                table: "GrinWebhookDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GrinWebhookDeliveries_InvoiceId_EventType",
                schema: "BTCPayServer.Plugins.Grin",
                table: "GrinWebhookDeliveries",
                columns: new[] { "InvoiceId", "EventType" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GrinWebhookDeliveries",
                schema: "BTCPayServer.Plugins.Grin");
        }
    }
}
