using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BTCPayServer.Plugins.Grin.Migrations;

[DbContext(typeof(GrinDbContext))]
[Migration("20260310000000_AddNodeApiUrl")]
public partial class AddNodeApiUrl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "NodeApiUrl",
            schema: "BTCPayServer.Plugins.Grin",
            table: "GrinStoreSettings",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "NodeApiUrl",
            schema: "BTCPayServer.Plugins.Grin",
            table: "GrinStoreSettings");
    }
}
