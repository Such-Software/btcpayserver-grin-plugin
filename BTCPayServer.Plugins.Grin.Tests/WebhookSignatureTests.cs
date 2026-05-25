using System.Text;
using BTCPayServer.Plugins.Grin.Services;
using Xunit;

namespace BTCPayServer.Plugins.Grin.Tests;

/// <summary>
/// Locks in the wire format that Medusa's btcpay-sig validator expects.
/// Catching encoding drift here (e.g. uppercase hex, missing
/// "sha256=" prefix) prevents the entire fleet of integrations from
/// silently rejecting half our webhooks the next time someone
/// refactors the dispatch path.
/// </summary>
public class WebhookSignatureTests
{
    [Fact]
    public void Compute_ProducesShaPrefixedLowercaseHex()
    {
        // Known-good vector — HMAC-SHA256 of "hello" with key "key"
        // is 9307b3529d3...c1e3a, lowercase hex.
        var sig = WebhookSignature.Compute("key", Encoding.UTF8.GetBytes("hello"));

        Assert.StartsWith("sha256=", sig);
        var hex = sig.Substring("sha256=".Length);
        Assert.Equal(64, hex.Length); // 32 bytes × 2 hex chars
        Assert.Equal(hex.ToLowerInvariant(), hex); // never uppercase
        Assert.Matches("^[a-f0-9]{64}$", hex);
    }

    [Fact]
    public void Compute_KnownVector_HmacSha256OfHelloWithKeyKey()
    {
        // Reproducible vector — verify with:
        //   echo -n hello | openssl dgst -sha256 -hmac key
        // Locks the encoding so the receiver-side validator can't
        // drift away from our sender-side format.
        var sig = WebhookSignature.Compute("key", Encoding.UTF8.GetBytes("hello"));
        Assert.Equal(
            "sha256=9307b3b915efb5171ff14d8cb55fbcc798c6c0ef1456d66ded1a6aa723a58b7b",
            sig);
    }

    [Fact]
    public void Compute_EmptySecret_ReturnsEmptyString()
    {
        // No secret configured → no signature. Caller is responsible
        // for not adding a "btcpay-sig" header in that case (Medusa
        // would reject "sha256=" with no value as malformed).
        Assert.Equal("", WebhookSignature.Compute("", Encoding.UTF8.GetBytes("hello")));
        Assert.Equal("", WebhookSignature.Compute(null!, Encoding.UTF8.GetBytes("hello")));
    }

    [Fact]
    public void Compute_DifferentBodies_DifferentSignatures()
    {
        // Sanity — different payloads must produce different sigs,
        // else replay attacks would be trivial.
        var a = WebhookSignature.Compute("secret", Encoding.UTF8.GetBytes("{\"event\":\"A\"}"));
        var b = WebhookSignature.Compute("secret", Encoding.UTF8.GetBytes("{\"event\":\"B\"}"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Compute_SameSecretAndBody_IsDeterministic()
    {
        // HMAC must be deterministic; without this the integration
        // can't verify signatures across processes.
        var body = Encoding.UTF8.GetBytes("payload");
        Assert.Equal(
            WebhookSignature.Compute("s", body),
            WebhookSignature.Compute("s", body));
    }
}
