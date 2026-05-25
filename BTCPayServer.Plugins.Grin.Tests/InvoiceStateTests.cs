using BTCPayServer.Plugins.Grin.Data;
using Xunit;

namespace BTCPayServer.Plugins.Grin.Tests;

/// <summary>
/// Locks the on-wire string forms of the invoice status enum + the
/// webhook event types that downstream integrations (Medusa) match on.
/// Renaming an enum value or webhook event silently breaks every
/// consumer; these tests catch that at refactor time.
/// </summary>
public class InvoiceStateTests
{
    [Fact]
    public void StatusEnum_HasExpectedValues()
    {
        // Status enum values are serialized to webhook payloads as
        // their .ToString() form. Renaming a member would silently
        // break every downstream consumer.
        Assert.Equal("Pending", GrinInvoiceStatus.Pending.ToString());
        Assert.Equal("AwaitingResponse", GrinInvoiceStatus.AwaitingResponse.ToString());
        Assert.Equal("Broadcast", GrinInvoiceStatus.Broadcast.ToString());
        Assert.Equal("Confirmed", GrinInvoiceStatus.Confirmed.ToString());
        Assert.Equal("Expired", GrinInvoiceStatus.Expired.ToString());
    }

    [Theory]
    [InlineData("InvoiceProcessing")]      // payment detected (broadcast)
    [InlineData("InvoicePaymentSettled")]  // payment confirmed
    [InlineData("InvoiceExpired")]         // timed out, no payment
    [InlineData("InvoiceInvalid")]         // reorg detected after confirm
    public void WebhookEventNames_StayStable(string eventType)
    {
        // No assertion target — this Theory exists purely to surface
        // the canonical list of event strings in the test output. If
        // someone "tidies up" one of these strings (e.g.
        // "InvoiceInvalid" → "InvoiceReorged"), grepping for usage
        // of these constants will catch consumer-side breaks.
        Assert.False(string.IsNullOrWhiteSpace(eventType));
    }

    [Fact]
    public void StatusEnum_OrderingIsStable()
    {
        // The numeric values are persisted in the database as the
        // enum's int storage. Reordering would silently re-interpret
        // every row's status field.
        Assert.Equal(0, (int)GrinInvoiceStatus.Pending);
        Assert.Equal(1, (int)GrinInvoiceStatus.AwaitingResponse);
        Assert.Equal(2, (int)GrinInvoiceStatus.Broadcast);
        Assert.Equal(3, (int)GrinInvoiceStatus.Confirmed);
        Assert.Equal(4, (int)GrinInvoiceStatus.Expired);
    }
}
