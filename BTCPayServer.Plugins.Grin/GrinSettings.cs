namespace BTCPayServer.Plugins.Grin;

public class GrinSettings
{
    public string OwnerApiUrl { get; set; } = "http://127.0.0.1:3420";
    public string WalletPassword { get; set; } = "";
    public int MinConfirmations { get; set; } = 10;
    public bool Enabled { get; set; } = false;
}
