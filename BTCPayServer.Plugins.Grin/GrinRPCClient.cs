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
        // 1. Generate ephemeral secp256k1 keypair using NBitcoin
        var privKey = new Key();
        var pubKey = privKey.PubKey;
        var pubKeyHex = Convert.ToHexString(pubKey.ToBytes()).ToLower();

        // 2. Send our pubkey, get server's pubkey back
        var resp = await RpcRaw("init_secure_api", new { ecdh_pubkey = pubKeyHex });
        var serverPubKeyHex = resp.GetProperty("Ok").GetString();

        // 3. ECDH: compute shared secret using NBitcoin
        var serverPubKey = new PubKey(serverPubKeyHex);
        var sharedPubKey = serverPubKey.GetSharedPubkey(privKey);
        _sharedKey = sharedPubKey.ToBytes()[1..33]; // x-coordinate only

        _logger.LogInformation("Grin wallet ECDH key exchange completed");

        // 4. Open wallet to get session token
        var tokenResp = await RpcEncrypted("open_wallet",
            new { name = (string)null, password = _walletPassword });
        _token = tokenResp.GetProperty("Ok").GetString();

        _logger.LogInformation("Grin wallet session opened");
    }

    public async Task<JsonElement> GetSlatepackAddress(int derivationIndex = 0)
    {
        return await RpcEncrypted("get_slatepack_address",
            new { token = _token, derivation_index = derivationIndex });
    }

    public async Task<JsonElement> IssueInvoiceTx(long amountNanogrin, string message)
    {
        return await RpcEncrypted("issue_invoice_tx", new
        {
            token = _token,
            args = new
            {
                amount = amountNanogrin.ToString(),
                message,
                dest_acct_name = (string)null
            }
        });
    }

    public async Task<JsonElement> CreateSlatepackMessage(JsonElement slate, int senderIndex = 0)
    {
        return await RpcEncrypted("create_slatepack_message", new
        {
            token = _token,
            slate,
            sender_index = senderIndex,
            recipients = Array.Empty<string>()
        });
    }

    public async Task<JsonElement> DecodeSlatepack(string slatepackMessage)
    {
        return await RpcEncrypted("slate_from_slatepack_message", new
        {
            token = _token,
            message = slatepackMessage,
            secret_indices = new[] { 0 }
        });
    }

    public async Task<JsonElement> FinalizeTx(JsonElement slate)
    {
        return await RpcEncrypted("finalize_tx", new
        {
            token = _token,
            slate
        });
    }

    public async Task<JsonElement> PostTx(JsonElement slate, bool fluff = false)
    {
        return await RpcEncrypted("post_tx", new
        {
            token = _token,
            slate,
            fluff
        });
    }

    public async Task<JsonElement> RetrieveTxs(string txSlateId = null)
    {
        return await RpcEncrypted("retrieve_txs", new
        {
            token = _token,
            refresh_from_node = true,
            tx_id = (int?)null,
            tx_slate_id = txSlateId
        });
    }

    public async Task<JsonElement> GetSummaryInfo()
    {
        return await RpcEncrypted("retrieve_summary_info", new
        {
            token = _token,
            refresh_from_node = true,
            minimum_confirmations = 1
        });
    }

    public async Task<JsonElement> NodeHeight()
    {
        return await RpcEncrypted("node_height", new
        {
            token = _token
        });
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

        // Decrypt response
        var respNonceHex = encResp.GetProperty("nonce").GetString();
        var respBodyEnc = encResp.GetProperty("body_enc").GetString();

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
        return respJson.GetProperty("result");
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

        return responseJson.GetProperty("result");
    }
}
