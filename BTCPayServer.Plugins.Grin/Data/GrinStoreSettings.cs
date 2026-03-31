using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Grin.Data;

public class GrinStoreSettings
{
    [Key]
    public string StoreId { get; set; }
    public string OwnerApiUrl { get; set; } = "http://127.0.0.1:3420";
    public string WalletPassword { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string NodeApiUrl { get; set; } = "";
    public int MinConfirmations { get; set; } = 10;
    public bool Enabled { get; set; }
    public string WebhookUrl { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
}
