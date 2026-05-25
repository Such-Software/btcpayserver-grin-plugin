using System;
using BTCPayServer.Plugins.Grin.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace BTCPayServer.Plugins.Grin.Tests;

/// <summary>
/// Covers the at-rest encryption applied to <c>WalletPassword</c>,
/// <c>ApiSecret</c>, and <c>WebhookSecret</c> on
/// <c>GrinStoreSettings</c>. The converter sits inside EF Core's
/// value-conversion pipeline, but we test it in isolation with an
/// <see cref="EphemeralDataProtectionProvider"/> so a CI run doesn't
/// need a key-ring directory.
/// </summary>
public class EncryptedColumnConverterTests
{
    private static IDataProtector Protector()
    {
        // Ephemeral provider generates a fresh key per process —
        // perfect for tests, never use in production.
        return new EphemeralDataProtectionProvider()
            .CreateProtector("BTCPayServer.Plugins.Grin.Tests");
    }

    [Fact]
    public void RoundTrip_PreservesValue()
    {
        var converter = new EncryptedColumnConverter(Protector());
        const string secret = "hunter2-correct-horse-battery-staple";

        // EF Core uses .ConvertToProvider for write and
        // .ConvertFromProvider for read.
        var stored = (string)converter.ConvertToProvider(secret)!;
        Assert.NotEqual(secret, stored); // actually encrypted, not pass-through
        Assert.StartsWith("enc:v1:", stored);

        var roundTripped = (string)converter.ConvertFromProvider(stored)!;
        Assert.Equal(secret, roundTripped);
    }

    [Fact]
    public void EmptyString_StaysEmpty()
    {
        // Encrypting an empty string is wasted work and produces a
        // non-empty ciphertext (the IV alone is ~16 bytes), which
        // would confuse "is this field set?" checks. Treat as a
        // no-op in both directions.
        var converter = new EncryptedColumnConverter(Protector());
        Assert.Equal("", converter.ConvertToProvider("")!);
        Assert.Equal("", converter.ConvertFromProvider("")!);
    }

    [Fact]
    public void LegacyPlaintext_ReadsThrough_AsIs()
    {
        // Existing deployments have plaintext in the DB. The
        // converter must let those rows through on read without
        // attempting to decrypt — they only get encrypted on the
        // next save.
        var converter = new EncryptedColumnConverter(Protector());
        const string legacy = "this-was-stored-before-encryption";

        var read = (string)converter.ConvertFromProvider(legacy)!;
        Assert.Equal(legacy, read);
    }

    [Fact]
    public void RewriteOfLegacyRow_PicksUpPrefixOnNextSave()
    {
        // Lazy-migration scenario: a legacy plaintext row gets read
        // (passes through), then re-saved (gets encrypted with the
        // prefix). After this round-trip, the stored form looks
        // protected, and reading it back still yields the original.
        var converter = new EncryptedColumnConverter(Protector());
        const string legacy = "legacy-cleartext";

        // Simulate "load row → mutate → save row" cycle. EF doesn't
        // explicitly re-encrypt unless the value changes, but in
        // practice operators editing settings always re-set every
        // field anyway. Test the worst-case: same value, re-saved.
        var read = (string)converter.ConvertFromProvider(legacy)!;
        var saved = (string)converter.ConvertToProvider(read)!;
        Assert.StartsWith("enc:v1:", saved);

        var readBack = (string)converter.ConvertFromProvider(saved)!;
        Assert.Equal(legacy, readBack);
    }

    [Fact]
    public void CorruptedCiphertext_ReturnsEmptyString_DoesNotThrow()
    {
        // Key ring rotated, storage corrupted, etc. — unprotect
        // throws CryptographicException. We swallow it and return
        // "" so the settings page still loads (operator can re-enter
        // the credential), rather than 500-ing the whole panel.
        var converter = new EncryptedColumnConverter(Protector());
        const string bogus = "enc:v1:not-actually-base64-just-garbage";

        var result = (string)converter.ConvertFromProvider(bogus)!;
        Assert.Equal("", result);
    }

    [Fact]
    public void EncryptionIsNonDeterministic()
    {
        // ASP.NET Core DataProtector uses a fresh IV per call.
        // Encrypting the same plaintext twice should produce
        // different ciphertexts — confirms there's no
        // accidentally-determinstic config (which would leak
        // info via DB scans).
        var converter = new EncryptedColumnConverter(Protector());
        var a = (string)converter.ConvertToProvider("same-input")!;
        var b = (string)converter.ConvertToProvider("same-input")!;
        Assert.NotEqual(a, b);
    }
}
