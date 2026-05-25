using BTCPayServer.Plugins.Grin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Grin.Tests;

/// <summary>
/// Covers <see cref="ReorgDetection.IsReorged"/> — the decision logic
/// extracted from <c>GrinPaymentMonitorService.CheckForReorgs</c>.
///
/// Why these tests exist: chain reorgs are nearly impossible to
/// reproduce manually (you can't force a Grin reorg on demand), and
/// when they DO happen in prod the cost of a bug is "merchant ships
/// against an orphaned payment." This file is the safety net that
/// runs on every CI build.
/// </summary>
public class ReorgDetectionTests
{
    [Fact]
    public void HappyPath_StillConfirmedAboveThreshold_NotReorged()
    {
        // Confirmed in wallet, plenty of confirmations → no reorg.
        Assert.False(ReorgDetection.IsReorged(
            walletReportsConfirmed: true,
            currentConfirmations: 12,
            minConfirmations: 10));
    }

    [Fact]
    public void WalletReportsUnconfirmed_IsReorg()
    {
        // tx.confirmed flipped to false — the transaction fell out
        // of the chain entirely. Doesn't matter what the
        // confirmation count claims; the wallet itself says it's
        // no longer settled.
        Assert.True(ReorgDetection.IsReorged(
            walletReportsConfirmed: false,
            currentConfirmations: 0,
            minConfirmations: 10));

        // Even if currentConfirmations is somehow non-zero (RPC
        // inconsistency), wallet's "not confirmed" wins.
        Assert.True(ReorgDetection.IsReorged(
            walletReportsConfirmed: false,
            currentConfirmations: 5,
            minConfirmations: 10));
    }

    [Fact]
    public void ConfirmationsDroppedBelowThreshold_IsReorg()
    {
        // Confirmation count dropped below MinConfirmations — the
        // output is on a shallower chain now. Still a reorg from the
        // invoice's perspective.
        Assert.True(ReorgDetection.IsReorged(
            walletReportsConfirmed: true,
            currentConfirmations: 9,
            minConfirmations: 10));
    }

    [Fact]
    public void ConfirmationsExactlyAtThreshold_NotReorged()
    {
        // Edge: confirmations == minConfirmations. Original confirm
        // condition is `>=`, so the equality case is still confirmed
        // — must NOT be flagged as a reorg.
        Assert.False(ReorgDetection.IsReorged(
            walletReportsConfirmed: true,
            currentConfirmations: 10,
            minConfirmations: 10));
    }

    [Fact]
    public void ConfirmationsZero_WhileWalletStillConfirmed_IsReorg()
    {
        // Unlikely combination — wallet says confirmed but the
        // output height puts confs at 0. This can happen briefly
        // during a deep reorg when the node has rolled back past
        // the output's old height. Treat as reorg until the chain
        // settles again.
        Assert.True(ReorgDetection.IsReorged(
            walletReportsConfirmed: true,
            currentConfirmations: 0,
            minConfirmations: 10));
    }

    [Fact]
    public void HighMinConfirmationsThreshold_RespectsTheBar()
    {
        // Some stores set MinConfirmations to 100 for high-value
        // items. Confirmation drop from 200 → 99 should still
        // trip reorg detection.
        Assert.True(ReorgDetection.IsReorged(
            walletReportsConfirmed: true,
            currentConfirmations: 99,
            minConfirmations: 100));
        Assert.False(ReorgDetection.IsReorged(
            walletReportsConfirmed: true,
            currentConfirmations: 100,
            minConfirmations: 100));
    }
}
