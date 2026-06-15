#nullable enable
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Grin.Payments;

/// <summary>
/// Generates the "payment link" that BTCPay surfaces when displaying
/// a Grin payment prompt — copy-button text, QR code payload, etc.
/// Bitcoin uses BIP21 here; Lightning uses lightning:&lt;invoice&gt;.
/// Grin has no widely-adopted URI scheme, so we surface the
/// slatepack message itself as the "link" — that's what the customer
/// pastes into their wallet, and it's already self-describing
/// (the slatepack format includes amount + sender + recipient).
///
/// If we ever standardize on something like <c>grin:&lt;slatepack&gt;</c>
/// or <c>web+slatepack://...</c>, swap that in here.
/// </summary>
public class GrinPaymentLinkExtension : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; }

    public GrinPaymentLinkExtension()
    {
        PaymentMethodId = GrinPaymentMethodConstants.PaymentMethodId;
    }

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        // The slatepack message lives in PaymentPrompt.Details — but
        // the IPaymentLinkExtension contract is sync + has no access
        // to the handler, so we can't parse Details cleanly here.
        // Returning the destination (slatepack address) is the safe
        // fallback — it round-trips through any "scan to pay" flow
        // even if it isn't a clickable URI in the strictest sense.
        return prompt.Destination;
    }
}
