using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;

namespace BTCPayServer.Plugins.Grin.Services;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GrinDbContext>
{
    public GrinDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<GrinDbContext>();
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");
        return new GrinDbContext(builder.Options, true);
    }
}

public class GrinDbContextFactory : BaseDbContextFactory<GrinDbContext>
{
    public GrinDbContextFactory(IOptions<DatabaseOptions> options)
        : base(options, "BTCPayServer.Plugins.Grin")
    {
    }

    public override GrinDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<GrinDbContext>();
        ConfigureBuilder(builder);
        return new GrinDbContext(builder.Options);
    }
}
