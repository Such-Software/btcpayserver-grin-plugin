using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Grin.Payments;

/// <summary>
/// Single source of truth for the Grin payment-method identifier.
/// BTCPay convention is <c>&lt;cryptoCode&gt;-&lt;paymentType&gt;</c>
/// (e.g. <c>BTC-CHAIN</c>, <c>BTC-LN</c>). Grin is on-chain only — no
/// Lightning, no LNURL — so we use the CHAIN payment type.
///
/// Every handler / extension class that needs the identifier resolves
/// it through here so a future rename (e.g. adding "GRIN-SLATEPACK"
/// as a sibling for some Phase X feature) only edits one constant.
/// </summary>
public static class GrinPaymentMethodConstants
{
    public const string CryptoCode = "GRIN";

    /// <summary>
    /// Number of subunits per GRIN — 1 GRIN = 1,000,000,000 nanogrin.
    /// Matches the wallet RPC's amount encoding (nanogrin everywhere).
    /// </summary>
    public const int Divisibility = 9;

    public static readonly PaymentMethodId PaymentMethodId =
        PaymentTypes.CHAIN.GetPaymentMethodId(CryptoCode);
}
