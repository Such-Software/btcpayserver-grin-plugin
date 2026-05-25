using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.Grin.Services;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<GrinDbContext>
{
    public GrinDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<GrinDbContext>();
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");
        // Design-time = no DataProtector. Migrations don't need the
        // encryption converter; they just emit raw text columns.
        return new GrinDbContext(builder.Options, dataProtectionProvider: null, designTime: true);
    }
}

public class GrinDbContextFactory : BaseDbContextFactory<GrinDbContext>
{
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public GrinDbContextFactory(
        IOptions<DatabaseOptions> options,
        IDataProtectionProvider dataProtectionProvider)
        : base(options, "BTCPayServer.Plugins.Grin")
    {
        _dataProtectionProvider = dataProtectionProvider;
    }

    public override GrinDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<GrinDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new GrinDbContext(builder.Options, _dataProtectionProvider);
    }
}
