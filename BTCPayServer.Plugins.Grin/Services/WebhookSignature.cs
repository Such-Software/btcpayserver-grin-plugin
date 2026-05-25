using System;
using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// Shared helper so the two webhook dispatch paths
/// (<c>GrinService.DispatchWebhook</c> and
/// <c>GrinPaymentMonitorService.DispatchWebhookAsync</c>) can never
/// drift on signature format. The wire format is:
///
///   <c>btcpay-sig: sha256=&lt;lowercase-hex(HMAC-SHA256(secret, body))&gt;</c>
///
/// — identical to BTCPay's own webhook signing convention so Medusa's
/// receiver-side validator works uniformly across BTC/LTC/XMR/WOW/Grin.
/// Tested in <c>BTCPayServer.Plugins.Grin.Tests.WebhookSignatureTests</c>.
/// </summary>
public static class WebhookSignature
{
    /// <summary>
    /// Returns the full header value ("sha256=&lt;hex&gt;") for the given
    /// secret + body. Returns the empty string when the secret is
    /// missing — callers should check + skip the header in that case
    /// rather than emit a header with no value.
    /// </summary>
    public static string Compute(string secret, byte[] body)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(body);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
