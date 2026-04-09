namespace MeetingsBot.Models;

/// <summary>
/// Strongly typed configuration for the bot application.
/// Bound from the "Bot" section in appsettings.json.
/// </summary>
public class BotOptions
{
    public const string SectionName = "Bot";

    /// <summary>Azure AD Application (client) ID.</summary>
    public string AppId { get; set; } = "";

    /// <summary>Azure AD Application client secret.</summary>
    public string AppSecret { get; set; } = "";

    /// <summary>
    /// Public HTTPS base URL that Teams will call for webhook notifications.
    /// Example: https://bot.yourdomain.ee
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Unique GUID identifying this media platform instance.
    /// Generate once with Guid.NewGuid() and keep stable across restarts.
    /// </summary>
    public string MediaPlatformInstanceId { get; set; } = "";

    /// <summary>
    /// Tenant ID for single-tenant apps. Leave empty for multi-tenant.
    /// </summary>
    public string TenantId { get; set; } = "";

    /// <summary>
    /// Path to the PFX certificate used by the media platform for TLS.
    /// Required for application-hosted media.
    /// </summary>
    public string CertificatePath { get; set; } = "";

    /// <summary>
    /// Password for the PFX certificate.
    /// </summary>
    public string CertificatePassword { get; set; } = "";

    /// <summary>
    /// Thumbprint of the certificate in the Windows cert store (LocalMachine\My).
    /// Used by the media platform for MTLS authentication with Teams media relays.
    /// If set, takes precedence over CertificatePath for the media platform.
    /// Get it with: Get-ChildItem Cert:\LocalMachine\My | Select Thumbprint
    /// </summary>
    public string CertificateThumbprint { get; set; } = "";

    /// <summary>
    /// Public IP address or FQDN of this machine for media traffic.
    /// Teams media relays connect to this address.
    /// </summary>
    public string MediaPublicAddress { get; set; } = "";

    /// <summary>
    /// Port for the media TCP signaling endpoint.
    /// Default: 8445
    /// </summary>
    public int MediaPort { get; set; } = 8445;
}

/// <summary>
/// Configuration for the gRPC ingestion server connection.
/// Bound from the "Ingestion" section in appsettings.json.
/// </summary>
public class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>
    /// gRPC endpoint for the audio ingestion server.
    /// Example: http://gpu-server:50051
    /// </summary>
    public string GrpcEndpoint { get; set; } = "";
}
