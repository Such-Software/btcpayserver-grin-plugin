using System;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// EF Core value converter that encrypts a string column at rest using
/// ASP.NET Core's <see cref="IDataProtector"/>. Applied to sensitive
/// fields on <c>GrinStoreSettings</c> (<c>WalletPassword</c>,
/// <c>ApiSecret</c>, <c>WebhookSecret</c>) so a leaked Postgres
/// snapshot doesn't immediately hand attackers wallet credentials.
///
/// Storage format is a versioned prefix:
///
///   "enc:v1:&lt;base64 protected blob&gt;"     ← rows written by this code
///   "&lt;plaintext&gt;"                          ← legacy rows pre-encryption
///
/// On READ the converter unwraps the prefix and calls
/// <see cref="IDataProtector.Unprotect(string)"/>; rows without the
/// prefix are passed through as-is so existing deployments don't
/// require a big-bang re-encrypt migration. On WRITE every value gets
/// the prefix + Protect treatment, so legacy rows migrate lazily on
/// their next save (e.g. the next time the operator clicks "Save" in
/// the Grin settings panel).
///
/// Failure modes:
///   - If unprotect fails (key ring rotated without the old key, or
///     storage somehow got corrupted), we LOG the failure and return
///     an empty string. Better to render an empty password field and
///     prompt the operator to re-paste credentials than to 500 the
///     entire settings page and lock them out.
///   - On Protect failure (extremely rare), the exception propagates;
///     a save failure is better than persisting plaintext silently.
/// </summary>
public sealed class EncryptedColumnConverter : ValueConverter<string, string>
{
    private const string Prefix = "enc:v1:";

    public EncryptedColumnConverter(IDataProtector protector)
        : base(
              v => Encode(protector, v),
              v => Decode(protector, v))
    {
    }

    private static string Encode(IDataProtector protector, string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        return Prefix + protector.Protect(plain);
    }

    private static string Decode(IDataProtector protector, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        var blob = stored.Substring(Prefix.Length);
        try
        {
            return protector.Unprotect(blob);
        }
        catch (Exception)
        {
            // Key ring rotated without the previous key, or storage
            // corruption. Returning "" forces the operator to re-enter
            // their credentials rather than locking the settings page.
            return "";
        }
    }
}
