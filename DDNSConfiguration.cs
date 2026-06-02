namespace AzureDdns;

public sealed class DDNSConfiguration
{
    public const string SectionName = "DDNS";

    // Default, Environment, ManagedIdentity, ClientSecret
    public string AuthMode { get; set; } = "Default";

    // Service principal settings (for AuthMode=ClientSecret).
    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    // Optional custom path to a mounted secret file with the client secret.
    public string? ClientSecretFile { get; set; }

    // Optional user-assigned managed identity client id.
    public string? ManagedIdentityClientId { get; set; }

    public static readonly string[] DefaultIpLookupUrls =
    [
        "https://api.ipify.org",
        "https://ifconfig.me/ip",
        "https://checkip.amazonaws.com"
    ];

    public string SubscriptionId { get; set; } = string.Empty;

    public string ResourceGroupName { get; set; } = string.Empty;

    public string ZoneName { get; set; } = string.Empty;

    // Use relative names ("@", "www") or FQDNs inside ZoneName ("www.example.com").
    public string[] Hostnames { get; set; } = [];

    public int UpdateIntervalSeconds { get; set; } = 300;

    public long TtlSeconds { get; set; } = 300;

    public string[] IpLookupUrls { get; set; } = DefaultIpLookupUrls;
}