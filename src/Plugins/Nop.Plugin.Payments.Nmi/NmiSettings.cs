using Nop.Core.Configuration;
using Nop.Plugin.Payments.Nmi.Domain;

namespace Nop.Plugin.Payments.Nmi;

public class NmiSettings : ISettings
{
    /// <summary>
    /// When true, every request goes to NMI's public sandbox (sandbox.nmi.com) and webhooks are expected to be test-mode events.
    /// </summary>
    public bool UseSandbox { get; set; } = true;

    /// <summary>
    /// Production gateway host name that serves transact.php, query.php and Collect.js (no scheme, no path).
    /// </summary>
    public string GatewayHost { get; set; } = NmiDefaults.DefaultGatewayHost;

    /// <summary>
    /// Public key with the Tokenization permission, used only by Collect.js in the browser.
    /// </summary>
    public string PublicTokenizationKey { get; set; }

    /// <summary>
    /// Encrypted private API security key sent as <c>security_key</c> to the Payment API.
    /// </summary>
    public string EncryptedPrivateApiKey { get; set; }

    public string EncryptedWebhookSigningKey { get; set; }
    public NmiTransactionMode TransactionMode { get; set; } = NmiTransactionMode.AuthorizeAndCapture;
    public string ProcessorId { get; set; }
    public int DuplicateCheckSeconds { get; set; } = 120;
    public decimal AdditionalFee { get; set; }
    public bool AdditionalFeePercentage { get; set; }
    public bool EnableDebugLogging { get; set; }
}
