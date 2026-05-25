namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// Pure decision logic for "is this confirmed invoice now reorged?",
/// extracted from <c>GrinPaymentMonitorService.CheckForReorgs</c> so
/// it can be unit-tested without standing up a full RPC client + DB.
/// </summary>
public static class ReorgDetection
{
    /// <summary>
    /// Given the freshly-polled state of a previously-confirmed
    /// invoice's transaction, decide whether a reorg has invalidated
    /// the payment.
    /// </summary>
    /// <param name="walletReportsConfirmed">
    /// <c>tx.confirmed</c> from grin-wallet's <c>retrieve_txs</c> on
    /// THIS poll. False = the tx fell out of the chain entirely.
    /// </param>
    /// <param name="currentConfirmations">
    /// Confirmation count computed from the output height vs node
    /// height on THIS poll. Zero when the tx is unconfirmed.
    /// </param>
    /// <param name="minConfirmations">
    /// Store's threshold for considering an invoice paid (default 10).
    /// </param>
    /// <returns>
    /// True if the invoice should be downgraded back to Broadcast +
    /// an InvoiceInvalid webhook fired. False if the invoice is
    /// still legitimately confirmed.
    /// </returns>
    public static bool IsReorged(
        bool walletReportsConfirmed,
        int currentConfirmations,
        int minConfirmations)
    {
        if (!walletReportsConfirmed) return true;
        if (currentConfirmations < minConfirmations) return true;
        return false;
    }
}
