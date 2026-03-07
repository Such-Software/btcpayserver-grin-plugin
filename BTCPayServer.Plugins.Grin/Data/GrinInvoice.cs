using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Grin.Data;

public class GrinInvoice
{
    [Key]
    public string Id { get; set; }                  // BTCPay invoice ID

    public string StoreId { get; set; }             // BTCPay store ID
    public string TxSlateId { get; set; }           // Grin tx UUID
    public string SlatepackAddress { get; set; }    // Per-invoice slatepack address
    public string IssuedSlatepack { get; set; }     // Slatepack message shown to customer
    public long AmountNanogrin { get; set; }
    public GrinInvoiceStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public int Confirmations { get; set; }
}

public enum GrinInvoiceStatus
{
    Pending,
    AwaitingResponse,
    Broadcast,
    Confirmed,
    Expired
}
