using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.Grin;

public class GrinRPCClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GrinRPCClient> _logger;
    private byte[] _sharedKey;
    private string _token;
    private string _ownerApiUrl;
    private string _walletPassword;
    private string _apiSecret;
    private int _requestId;
    private bool _sessionActive;

    public GrinRPCClient(ILogger<GrinRPCClient> logger)
    {
        _logger = logger;
        _http = new HttpClient();
    }

    public void Configure(string ownerApiUrl, string walletPassword, string apiSecret)
    {
        _ownerApiUrl = ownerApiUrl.TrimEnd('/');
        _walletPassword = walletPassword;
        _apiSecret = apiSecret;
    }

    public async Task InitSession()
    {
        _sessionActive = false;

        // 1. Generate ephemeral secp256k1 keypair using NBitcoin
        var privKey = new Key();
        var pubKey = privKey.PubKey;
        var pubKeyHex = Convert.ToHexString(pubKey.ToBytes()).ToLower();

        // 2. Send our pubkey, get server's pubkey back
        var resp = await RpcRaw("init_secure_api", new { ecdh_pubkey = pubKeyHex });

        if (!resp.TryGetProperty("Ok", out var okEcdh))
            throw new Exception($"ECDH init failed: {resp}");
        var serverPubKeyHex = okEcdh.GetString();

        // 3. ECDH: compute shared secret using NBitcoin
        var serverPubKey = new PubKey(serverPubKeyHex);
        var sharedPubKey = serverPubKey.GetSharedPubkey(privKey);
        _sharedKey = sharedPubKey.ToBytes()[1..33]; // x-coordinate only

        _logger.LogInformation("Grin wallet ECDH key exchange completed");

        // 4. Open wallet to get session token
        var tokenResp = await RpcEncrypted("open_wallet",
            new { name = (string)null, password = _walletPassword });

        if (!tokenResp.TryGetProperty("Ok", out var okToken))
            throw new Exception($"open_wallet failed: {tokenResp}");
        _token = okToken.GetString();
        _sessionActive = true;

        _logger.LogInformation("Grin wallet session opened");
    }

    public async Task<JsonElement> GetSlatepackAddress(int derivationIndex = 0)
    {
        return await WithAutoReconnect(() => RpcEncrypted("get_slatepack_address",
            new { token = _token, derivation_index = derivationIndex }));
    }

    public async Task<JsonElement> IssueInvoiceTx(long amountNanogrin, string message)
    {
        return await WithAutoReconnect(() => RpcEncrypted("issue_invoice_tx", new
        {
            token = _token,
            args = new
            {
                amount = amountNanogrin.ToString(),
                message,
                dest_acct_name = (string)null
            }
        }));
    }

    public async Task<JsonElement> CreateSlatepackMessage(JsonElement slate, int senderIndex = 0)
    {
        return await WithAutoReconnect(() => RpcEncrypted("create_slatepack_message", new
        {
            token = _token,
            slate,
            sender_index = senderIndex,
            recipients = Array.Empty<string>()
        }));
    }

    public async Task<JsonElement> DecodeSlatepack(string slatepackMessage)
    {
        return await WithAutoReconnect(() => RpcEncrypted("slate_from_slatepack_message", new
        {
            token = _token,
            message = slatepackMessage,
            secret_indices = new[] { 0 }
        }));
    }

    public async Task<JsonElement> FinalizeTx(JsonElement slate)
    {
        return await WithAutoReconnect(() => RpcEncrypted("finalize_tx", new
        {
            token = _token,
            slate
        }));
    }

    public async Task<JsonElement> PostTx(JsonElement finalizedSlate, bool fluff = false)
    {
        // post_tx takes the finalized slate; wallet extracts the transaction internally
        return await WithAutoReconnect(() => RpcEncrypted("post_tx", new
        {
            token = _token,
            slate = finalizedSlate,
            fluff
        }));
    }

    public async Task<JsonElement> RetrieveTxs(string txSlateId = null)
    {
        return await WithAutoReconnect(() => RpcEncrypted("retrieve_txs", new
        {
            token = _token,
            refresh_from_node = true,
            tx_id = (int?)null,
            tx_slate_id = txSlateId
        }));
    }

    public async Task<JsonElement> GetSummaryInfo()
    {
        return await WithAutoReconnect(() => RpcEncrypted("retrieve_summary_info", new
        {
            token = _token,
            refresh_from_node = true,
            minimum_confirmations = 1
        }));
    }

    public async Task<JsonElement> NodeHeight()
    {
        return await WithAutoReconnect(() => RpcEncrypted("node_height", new
        {
            token = _token
        }));
    }

    public async Task<JsonElement> CancelTx(string txSlateId)
    {
        return await WithAutoReconnect(() => RpcEncrypted("cancel_tx", new
        {
            token = _token,
            tx_slate_id = txSlateId
        }));
    }

    /// <summary>
    /// Wraps an RPC call with auto-reconnect on session expiry.
    /// On CryptographicException (stale shared key) or session-related errors,
    /// re-establishes the ECDH session and retries once.
    /// </summary>
    private async Task<JsonElement> WithAutoReconnect(Func<Task<JsonElement>> rpcCall)
    {
        try
        {
            return await rpcCall();
        }
        catch (Exception ex) when (IsSessionError(ex))
        {
            _logger.LogWarning("Grin wallet session expired, reconnecting...");
            await InitSession();
            return await rpcCall();
        }
    }

    private static bool IsSessionError(Exception ex)
    {
        // AES-GCM decryption failure = shared key is stale (wallet restarted)
        if (ex is CryptographicException)
            return true;

        // Grin RPC errors indicating session problems
        var msg = ex.Message;
        if (msg.Contains("InvalidSecretKey") || msg.Contains("InvalidToken") ||
            msg.Contains("session") || msg.Contains("nonce/body_enc"))
            return true;

        return false;
    }

    private async Task<JsonElement> RpcEncrypted(string method, object paramObj)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var innerRequest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            method,
            @params = paramObj,
            id = ++_requestId
        });

        // Encrypt with AES-256-GCM
        using var aes = new AesGcm(_sharedKey, 16);
        var ciphertext = new byte[innerRequest.Length];
        var tag = new byte[16];
        aes.Encrypt(nonce, innerRequest, ciphertext, tag);

        // Body is base64(ciphertext + tag)
        var bodyEnc = Convert.ToBase64String(ciphertext.Concat(tag).ToArray());
        var nonceHex = Convert.ToHexString(nonce).ToLower();

        // Send as encrypted_request_v3
        var encResp = await RpcRaw("encrypted_request_v3",
            new { nonce = nonceHex, body_enc = bodyEnc });

        // Decrypt response — unwrap "Ok" envelope if present
        var encData = encResp;
        if (encResp.TryGetProperty("Ok", out var okEnvelope))
            encData = okEnvelope;

        if (!encData.TryGetProperty("nonce", out var nonceEl) ||
            !encData.TryGetProperty("body_enc", out var bodyEncEl))
        {
            throw new Exception($"Encrypted response missing nonce/body_enc: {encResp}");
        }
        var respNonceHex = nonceEl.GetString();
        var respBodyEnc = bodyEncEl.GetString();

        var respNonce = Convert.FromHexString(respNonceHex);
        var respCombined = Convert.FromBase64String(respBodyEnc);
        var respCiphertext = respCombined[..^16];
        var respTag = respCombined[^16..];

        var respPlaintext = new byte[respCiphertext.Length];
        using var aes2 = new AesGcm(_sharedKey, 16);
        aes2.Decrypt(respNonce, respCiphertext, respTag, respPlaintext);

        var respJson = JsonSerializer.Deserialize<JsonElement>(respPlaintext);
        if (respJson.TryGetProperty("error", out var error))
        {
            throw new Exception($"Grin RPC error: {error}");
        }
        if (!respJson.TryGetProperty("result", out var encResult))
        {
            throw new Exception($"Grin RPC: unexpected response: {respJson}");
        }
        return encResult;
    }

    private async Task<JsonElement> RpcRaw(string method, object paramObj)
    {
        var request = new
        {
            jsonrpc = "2.0",
            id = ++_requestId,
            method,
            @params = paramObj
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Add Basic Auth if we have a secret
        if (!string.IsNullOrEmpty(_apiSecret))
        {
            var authBytes = Encoding.UTF8.GetBytes($"grin:{_apiSecret}");
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        }

        var response = await _http.PostAsync($"{_ownerApiUrl}/v3/owner", content);
        var responseBody = await response.Content.ReadAsStringAsync();
        var responseJson = JsonSerializer.Deserialize<JsonElement>(responseBody);

        if (responseJson.TryGetProperty("error", out var error))
        {
            throw new Exception($"Grin RPC error: {error}");
        }

        if (!responseJson.TryGetProperty("result", out var result))
        {
            throw new Exception($"Grin RPC: unexpected response: {responseJson}");
        }

        return result;
    }
}
