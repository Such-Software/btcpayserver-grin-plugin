using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Grin.Payments;

/// <summary>
/// Registers Grin's payment-method icon so BTCPay renders it next to
/// the status pill on the Invoices list, the checkout dropdown, and
/// anywhere else the platform asks "what does this payment method
/// look like?". Without an <see cref="ICheckoutModelExtension"/>
/// registered for the GRIN-CHAIN payment method, BTCPay falls back
/// to a blank icon (which is what operators were seeing pre-1.3.1
/// on the Invoices list — settled BTC/LTC rows had logos, GRIN rows
/// were unbranded).
///
/// We deliberately implement only <see cref="Image"/> and the bare
/// interface contract. Bitcoin's extension also injects fee
/// recommendations and BIP21 fallback into the checkout view model,
/// but that's specific to BTCPay's stock Bitcoin checkout view —
/// Grin has its own checkout page (<c>Views/GrinCheckout/</c>) that
/// doesn't go through <c>ModifyCheckoutModel</c>, so we leave it as
/// a no-op.
///
/// The image path is served by BTCPay's plugin static-file middleware
/// from the bundled <c>Resources/img/grin-logo.png</c>.
/// </summary>
public class GrinCheckoutModelExtension : ICheckoutModelExtension
{
    public PaymentMethodId PaymentMethodId => GrinPaymentMethodConstants.PaymentMethodId;

    public string Image => "Resources/img/grin-logo.png";

    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        // No-op: Grin uses its own plugin-hosted checkout view
        // (Views/GrinCheckout/Checkout.cshtml) for the slatepack-paste
        // flow, and external storefronts redirect to their own page
        // via checkout.redirectURL. BTCPay's stock CheckoutModel isn't
        // rendered for Grin invoices in practice.
    }
}
