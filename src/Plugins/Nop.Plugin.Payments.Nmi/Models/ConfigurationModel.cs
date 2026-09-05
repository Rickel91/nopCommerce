using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;
using Nop.Web.Framework.Models;
using Nop.Web.Framework.Mvc.ModelBinding;

namespace Nop.Plugin.Payments.Nmi.Models;

public record ConfigurationModel : BaseNopModel
{
    public int ActiveStoreScopeConfiguration { get; set; }

    [NopResourceDisplayName("Plugins.Payments.Nmi.Fields.UseSandbox")]
    public bool UseSandbox { get; set; }
    public bool UseSandbox_OverrideForStore { get; set; }

    [NopResourceDisplayName("Plugins.Payments.Nmi.Fields.GatewayHost")]
    public string GatewayHost { get; set; }
    public bool GatewayHost_OverrideForStore { get; set; }

    [NopResourceDisplayName("Plugins.Payments.Nmi.Fields.PublicTokenizationKey")]
    public string PublicTokenizationKey { get; set; }
    public bool PublicTokenizationKey_OverrideForStore { get; set; }

    [DataType(DataType.Password)]
    [NopResourceDisplayName("Plugins.Payments.Nmi.Fields.PrivateApiKey")]
    public string PrivateApiKey { get; set; }
    public bool PrivateApiKey_OverrideForStore { get; set; }

    /// <summary>
    /// Last characters of the stored private key, shown so an administrator can recognise which key is saved.
    /// Null when no key is stored.
    /// </summary>
    public string PrivateApiKeySuffix { get; set; }

    [DataType(DataType.Password)]
    [NopResourceDisplayName("Plugins.Payments.Nmi.Fields.WebhookSigningKey")]
    public string WebhookSigningKey { get; set; }
    public bool WebhookSigningKey_OverrideForStore { get; set; }

    [NopResourceDisplayName("Plugins.Payments.Nmi.Fields.TransactionMode")]
    public int TransactionModeId { get; set; }
    public SelectList TransactionModes { get; set; }
    public bool TransactionModeId_OverrideForStore { get; set; }

    [NopResourceDisplayName("Plugins.Payments.Nmi.Fields.ProcessorId")]
    public string ProcessorId { get; set; }
    public bool ProcessorId_OverrideForStore { get; set; }

    [Range(0, 7_862_400)]
    [NopResourceDisplayName("Plugins.Payments.Nmi.Fields.DuplicateCheckSeconds")]
    public int DuplicateCheckSeconds { get; set; }
    public bool DuplicateCheckSeconds_OverrideForStore { get; set; }

    [Range(0, double.MaxValue)]
    [NopResourceDisplayName("Plugins.Payments.Nmi.Fields.AdditionalFee")]
    public decimal AdditionalFee { get; set; }
    public bool AdditionalFee_OverrideForStore { get; set; }

    [NopResourceDisplayName("Plugins.Payments.Nmi.Fields.AdditionalFeePercentage")]
    public bool AdditionalFeePercentage { get; set; }
    public bool AdditionalFeePercentage_OverrideForStore { get; set; }

    [NopResourceDisplayName("Plugins.Payments.Nmi.Fields.EnableDebugLogging")]
    public bool EnableDebugLogging { get; set; }
    public bool EnableDebugLogging_OverrideForStore { get; set; }

    /// <summary>
    /// Absolute webhook URL shown to the administrator for copying into the NMI merchant portal.
    /// </summary>
    public string WebhookUrl { get; set; }
}
