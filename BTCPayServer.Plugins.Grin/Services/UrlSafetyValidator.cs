using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace BTCPayServer.Plugins.Grin.Services;

/// <summary>
/// Save-time guard against the easiest SSRF foot-guns on operator-
/// editable URL fields (<see cref="Data.GrinStoreSettings.WebhookUrl"/>,
/// <see cref="Data.GrinStoreSettings.NodeApiUrl"/>,
/// <see cref="Data.GrinStoreSettings.OwnerApiUrl"/>).
///
/// This is intentionally lighter than a full anti-SSRF posture. The
/// BTCPay-Grin deployment topology routinely has the wallet RPC and
/// the node RPC living on the same docker network as the BTCPay
/// container, i.e. RFC1918 / loopback / link-local addresses are
/// LEGITIMATE for those two fields. Blocking them would break every
/// self-hosted setup. WebhookUrl is more often pointed at a public
/// merchant endpoint, but self-hosted Medusa shops on the same docker
/// network are also legitimate — so even there we don't outright
/// reject private ranges, we just emit a warning.
///
/// What we DO reject:
///   - Non-absolute / unparseable URIs
///   - Schemes other than http / https
///   - URLs with embedded credentials (user:pass@host) — those leak
///     into logs and serve no legitimate purpose for this plugin
///   - Literal IMDS / cloud-metadata addresses (169.254.169.254 + the
///     standard hostnames). These are never a legitimate target and
///     are the single most common SSRF pivot.
///   - Empty / overly-long URLs
///
/// What we WARN about but allow:
///   - http (vs https) on WebhookUrl — not a SSRF issue, but a
///     confidentiality concern (signature + body in clear)
///   - Loopback / RFC1918 / link-local hosts (other than IMDS)
///
/// DNS-rebinding is NOT defended against here — a hostname that
/// resolves clean at save time can resolve to 169.254.169.254 later.
/// True defense requires resolving at use time and pinning the
/// resolved IP, which is a much larger change.
/// </summary>
public static class UrlSafetyValidator
{
    private const int MaxUrlLength = 4096;

    /// <summary>
    /// Hosts that map to cloud-instance metadata services. Matching is
    /// case-insensitive on hostname strings; the literal IPv4
    /// <c>169.254.169.254</c> covers AWS/GCP/Azure/DO/Hetzner/most
    /// providers since they all expose IMDS at that address.
    /// </summary>
    private static readonly HashSet<string> MetadataHostnames = new(StringComparer.OrdinalIgnoreCase)
    {
        "169.254.169.254",
        "metadata.google.internal",
        "metadata.aws.internal",
        "metadata.azure.com",
        // IPv6 IMDS counterpart that AWS publishes
        "fd00:ec2::254",
    };

    public class ValidationResult
    {
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool Ok => Errors.Count == 0;
    }

    /// <summary>
    /// Validate a URL value that an operator pasted into a settings
    /// field. <paramref name="fieldName"/> is used in error messages
    /// so the operator sees which field failed. <paramref name="allowEmpty"/>
    /// is true for optional fields (WebhookUrl when the operator
    /// intentionally clears it).
    /// </summary>
    public static ValidationResult Validate(string url, string fieldName, bool allowEmpty = false)
    {
        var result = new ValidationResult();

        if (string.IsNullOrWhiteSpace(url))
        {
            if (!allowEmpty)
                result.Errors.Add($"{fieldName}: URL is required.");
            return result;
        }

        if (url.Length > MaxUrlLength)
        {
            result.Errors.Add($"{fieldName}: URL exceeds maximum length ({MaxUrlLength}).");
            return result;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            result.Errors.Add($"{fieldName}: not a well-formed absolute URL.");
            return result;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            result.Errors.Add($"{fieldName}: scheme must be http or https (got '{uri.Scheme}').");
            return result;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            result.Errors.Add($"{fieldName}: URL must not contain embedded credentials (user:pass@host).");
            return result;
        }

        // Reject IMDS / cloud-metadata targets unconditionally.
        var host = uri.DnsSafeHost;
        if (MetadataHostnames.Contains(host))
        {
            result.Errors.Add($"{fieldName}: cloud-metadata endpoints are not allowed (host '{host}').");
            return result;
        }

        // Soft warnings — these don't block save but surface in the UI.
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            result.Warnings.Add($"{fieldName}: using http (signatures + bodies are sent in clear). Prefer https.");
        }
        if (IsPrivateOrLoopback(host))
        {
            result.Warnings.Add($"{fieldName}: host '{host}' is private/loopback. Only allow this if the target is intentionally on the same network as BTCPay (e.g. a docker-network sibling).");
        }

        return result;
    }

    private static bool IsPrivateOrLoopback(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        // Bare hostname (not an IP literal) — skip; we can't classify
        // without resolving, and we don't resolve at validate-time
        // (DNS rebinding mitigation is out of scope here).
        if (!IPAddress.TryParse(host, out var ip)) return false;

        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && (bytes[1] & 0xF0) == 16) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 (link-local; IMDS is in here but already blocked)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 (ULA) and fe80::/10 (link-local)
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
        }
        return false;
    }
}
