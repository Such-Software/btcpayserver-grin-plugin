using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Grin.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Grin.Services;

public class GrinService
{
    private readonly GrinDbContextFactory _dbContextFactory;

    public GrinService(GrinDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    // Store settings CRUD

    public async Task<GrinStoreSettings> GetStoreSettings(string storeId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.GrinStoreSettings.FindAsync(storeId);
    }

    public async Task SaveStoreSettings(GrinStoreSettings settings)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var existing = await ctx.GrinStoreSettings.FindAsync(settings.StoreId);
        if (existing == null)
        {
            await ctx.GrinStoreSettings.AddAsync(settings);
        }
        else
        {
            existing.OwnerApiUrl = settings.OwnerApiUrl;
            existing.WalletPassword = settings.WalletPassword;
            existing.ApiSecret = settings.ApiSecret;
            existing.MinConfirmations = settings.MinConfirmations;
            existing.Enabled = settings.Enabled;
        }
        await ctx.SaveChangesAsync();
    }

    public async Task<List<GrinStoreSettings>> GetAllEnabledStores()
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.GrinStoreSettings
            .Where(s => s.Enabled)
            .ToListAsync();
    }

    // Invoice CRUD

    public async Task<GrinInvoice> CreateInvoice(string invoiceId, string storeId,
        long amountNanogrin, string slatepackAddress, string issuedSlatepack, string txSlateId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var invoice = new GrinInvoice
        {
            Id = invoiceId,
            StoreId = storeId,
            AmountNanogrin = amountNanogrin,
            SlatepackAddress = slatepackAddress,
            IssuedSlatepack = issuedSlatepack,
            TxSlateId = txSlateId,
            Status = GrinInvoiceStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await ctx.GrinInvoices.AddAsync(invoice);
        await ctx.SaveChangesAsync();
        return invoice;
    }

    public async Task<GrinInvoice> GetInvoice(string invoiceId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.GrinInvoices.FindAsync(invoiceId);
    }

    public async Task<List<GrinInvoice>> GetPendingInvoices()
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.GrinInvoices
            .Where(i => i.Status != GrinInvoiceStatus.Confirmed &&
                        i.Status != GrinInvoiceStatus.Expired)
            .ToListAsync();
    }

    public async Task UpdateInvoiceStatus(string invoiceId, GrinInvoiceStatus status, int confirmations = 0)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var invoice = await ctx.GrinInvoices.FindAsync(invoiceId);
        if (invoice == null) return;

        invoice.Status = status;
        invoice.Confirmations = confirmations;
        if (status == GrinInvoiceStatus.Confirmed)
            invoice.PaidAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync();
    }
}
