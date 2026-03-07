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
