namespace BTCPayServer.Plugins.Grin.Payments;

/// <summary>
/// Store-level config blob for the Grin payment method as seen by
/// BTCPay's invoice flow. Mirrors the operator-editable subset of
/// <see cref="Data.GrinStoreSettings"/> — the live source of truth
/// remains the <c>GrinStoreSettings</c> row (encryption-at-rest,
/// admin UI, monitor settings), and the handler dereferences that
/// at <c>ConfigurePrompt</c> time rather than trusting whatever
/// BTCPay has persisted in its payment-method-config blob.
///
/// We keep this type intentionally minimal: BTCPay only needs to
/// know enough to decide "is Grin enabled for this store" — the
/// actual wallet plumbing lives in <c>GrinStoreSettings</c>.
/// </summary>
public class GrinPaymentMethodConfig
{
    /// <summary>
    /// Mirrors <c>GrinStoreSettings.Enabled</c>. BTCPay's store
    /// settings page can flip Grin on/off independently of the
    /// underlying wallet/RPC config, so the operator can disable
    /// Grin for a store without wiping its wallet credentials.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
