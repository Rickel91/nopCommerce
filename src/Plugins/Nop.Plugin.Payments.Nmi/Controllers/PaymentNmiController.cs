using Microsoft.AspNetCore.Mvc;
using Nop.Core;
using Nop.Plugin.Payments.Nmi.Domain;
using Nop.Plugin.Payments.Nmi.Models;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Services;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Messages;
using Nop.Services.Security;
using Nop.Web.Framework;
using Nop.Web.Framework.Controllers;
using Nop.Web.Framework.Mvc.Filters;

namespace Nop.Plugin.Payments.Nmi.Controllers;

[AuthorizeAdmin]
[Area(AreaNames.Admin)]
[AutoValidateAntiforgeryToken]
public class PaymentNmiController : BasePaymentController
{
    #region Fields

    private readonly ICustomerActivityService _customerActivityService;
    private readonly IEncryptionService _encryptionService;
    private readonly ILocalizationService _localizationService;
    private readonly INotificationService _notificationService;
    private readonly IPermissionService _permissionService;
    private readonly ISettingService _settingService;
    private readonly IStoreContext _storeContext;
    private readonly IWebHelper _webHelper;
    private readonly IWorkContext _workContext;
    private readonly NmiHttpClient _nmiHttpClient;
    private readonly NmiLogger _log;

    #endregion

    #region Ctor

    public PaymentNmiController(ICustomerActivityService customerActivityService,
        IEncryptionService encryptionService,
        ILocalizationService localizationService,
        INotificationService notificationService,
        IPermissionService permissionService,
        ISettingService settingService,
        IStoreContext storeContext,
        IWebHelper webHelper,
        IWorkContext workContext,
        NmiHttpClient nmiHttpClient,
        NmiLogger log)
    {
        _customerActivityService = customerActivityService;
        _encryptionService = encryptionService;
        _localizationService = localizationService;
        _notificationService = notificationService;
        _permissionService = permissionService;
        _settingService = settingService;
        _storeContext = storeContext;
        _webHelper = webHelper;
        _workContext = workContext;
        _nmiHttpClient = nmiHttpClient;
        _log = log ?? NmiLogger.None;
    }

    #endregion

    #region Methods

    public async Task<IActionResult> Configure()
    {
        if (!await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods))
            return AccessDeniedView();

        var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
        var settings = await _settingService.LoadSettingAsync<NmiSettings>(storeScope);
        return View("~/Plugins/Payments.Nmi/Views/Configure.cshtml", await PrepareModelAsync(settings, storeScope));
    }

    [HttpPost]
    public async Task<IActionResult> Configure(ConfigurationModel model)
    {
        if (!await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods))
            return AccessDeniedView();

        var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
        var settings = await _settingService.LoadSettingAsync<NmiSettings>(storeScope);
        var gatewayHost = string.IsNullOrWhiteSpace(model.GatewayHost)
            ? NmiDefaults.DefaultGatewayHost
            : model.GatewayHost.Trim().ToLowerInvariant();

        if (!NmiHttpClient.IsValidGatewayHost(gatewayHost))
            ModelState.AddModelError(nameof(model.GatewayHost), "Enter a gateway host name only, for example tbtech.transactiongateway.com.");
        if (string.IsNullOrWhiteSpace(model.PublicTokenizationKey))
            ModelState.AddModelError(nameof(model.PublicTokenizationKey), "A public tokenization key is required.");
        if (string.IsNullOrWhiteSpace(settings.EncryptedPrivateApiKey) && NmiSecretProtector.IsUnchanged(model.PrivateApiKey))
            ModelState.AddModelError(nameof(model.PrivateApiKey), "A private API security key is required.");
        if (!ModelState.IsValid)
            return View("~/Plugins/Payments.Nmi/Views/Configure.cshtml", await PrepareModelAsync(settings, storeScope, model));

        var privateKeyChanged = !NmiSecretProtector.IsUnchanged(model.PrivateApiKey);
        var signingKeyChanged = !NmiSecretProtector.IsUnchanged(model.WebhookSigningKey);
        var previous = $"use_sandbox={settings.UseSandbox}; gateway_host={settings.GatewayHost}; " +
                       $"transaction_mode={settings.TransactionMode}; debug_logging={settings.EnableDebugLogging}";

        settings.UseSandbox = model.UseSandbox;
        settings.GatewayHost = gatewayHost;
        settings.PublicTokenizationKey = model.PublicTokenizationKey.Trim();
        settings.EncryptedPrivateApiKey = NmiSecretProtector.Protect(model.PrivateApiKey, settings.EncryptedPrivateApiKey, _encryptionService);
        settings.EncryptedWebhookSigningKey = NmiSecretProtector.Protect(model.WebhookSigningKey, settings.EncryptedWebhookSigningKey, _encryptionService);
        settings.TransactionMode = (NmiTransactionMode)model.TransactionModeId;
        settings.ProcessorId = model.ProcessorId?.Trim();
        settings.DuplicateCheckSeconds = model.DuplicateCheckSeconds;
        settings.AdditionalFee = model.AdditionalFee;
        settings.AdditionalFeePercentage = model.AdditionalFeePercentage;
        settings.EnableDebugLogging = model.EnableDebugLogging;

        await SaveAsync(settings, x => x.UseSandbox, model.UseSandbox_OverrideForStore, storeScope);
        await SaveAsync(settings, x => x.GatewayHost, model.GatewayHost_OverrideForStore, storeScope);
        await SaveAsync(settings, x => x.PublicTokenizationKey, model.PublicTokenizationKey_OverrideForStore, storeScope);
        await SaveAsync(settings, x => x.EncryptedPrivateApiKey, model.PrivateApiKey_OverrideForStore, storeScope);
        await SaveAsync(settings, x => x.EncryptedWebhookSigningKey, model.WebhookSigningKey_OverrideForStore, storeScope);
        await SaveAsync(settings, x => x.TransactionMode, model.TransactionModeId_OverrideForStore, storeScope);
        await SaveAsync(settings, x => x.ProcessorId, model.ProcessorId_OverrideForStore, storeScope);
        await SaveAsync(settings, x => x.DuplicateCheckSeconds, model.DuplicateCheckSeconds_OverrideForStore, storeScope);
        await SaveAsync(settings, x => x.AdditionalFee, model.AdditionalFee_OverrideForStore, storeScope);
        await SaveAsync(settings, x => x.AdditionalFeePercentage, model.AdditionalFeePercentage_OverrideForStore, storeScope);
        await SaveAsync(settings, x => x.EnableDebugLogging, model.EnableDebugLogging_OverrideForStore, storeScope);
        await _settingService.ClearCacheAsync();

        var admin = await _workContext.GetCurrentCustomerAsync();
        var summary = $"NMI settings saved; store_scope={storeScope}; admin={admin?.Email ?? admin?.Id.ToString() ?? "unknown"}; " +
                      $"private_key_status={(privateKeyChanged ? "replaced" : "unchanged")}; " +
                      $"signing_key_status={(signingKeyChanged ? "replaced" : "unchanged")}; " +
                      $"before[{previous}]; after[use_sandbox={settings.UseSandbox}; gateway_host={settings.GatewayHost}; " +
                      $"transaction_mode={settings.TransactionMode}; debug_logging={settings.EnableDebugLogging}].";
        await _log.InformationAsync(summary, admin);
        await _customerActivityService.InsertActivityAsync("EditSettings",
            $"{await _localizationService.GetResourceAsync("ActivityLog.EditSettings")} (NMI Payments)");

        _notificationService.SuccessNotification(await _localizationService.GetResourceAsync("Admin.Plugins.Saved"));
        return await Configure();
    }

    [HttpPost]
    public async Task<IActionResult> TestConnection()
    {
        if (!await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods))
            return AccessDeniedView();

        var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
        var settings = await _settingService.LoadSettingAsync<NmiSettings>(storeScope);
        var admin = await _workContext.GetCurrentCustomerAsync();
        var context = $"store_scope={storeScope}; use_sandbox={settings.UseSandbox}; gateway_host={settings.GatewayHost}; " +
                      $"admin={admin?.Email ?? "unknown"}";

        if (string.IsNullOrWhiteSpace(settings.EncryptedPrivateApiKey))
        {
            await _log.WarningAsync($"connection test skipped; reason=private_key_not_saved; {context}.", customer: admin);
            _notificationService.ErrorNotification("Save a private API security key before testing the connection.");
        }
        else if (!NmiHttpClient.IsValidGatewayHost(settings.UseSandbox ? NmiDefaults.SandboxHost : settings.GatewayHost ?? NmiDefaults.DefaultGatewayHost))
        {
            await _log.WarningAsync($"connection test skipped; reason=invalid_gateway_host; {context}.", customer: admin);
            _notificationService.ErrorNotification("The saved gateway host is invalid.");
        }
        else
        {
            var check = await _nmiHttpClient.CheckCredentialsAsync(settings, HttpContext.RequestAborted);
            var outcome = $"{context}; http_status={check.StatusCode}; elapsed_ms={check.ElapsedMilliseconds}; detail={check.Detail}";
            if (check.Accepted)
            {
                await _log.InformationAsync($"connection test succeeded; {outcome}.", admin);
                _notificationService.SuccessNotification($"NMI accepted the private API security key ({check.ElapsedMilliseconds} ms).");
            }
            else
            {
                await _log.WarningAsync($"connection test failed; {outcome}.", customer: admin);
                _notificationService.ErrorNotification($"NMI rejected the private API security key ({check.Detail}). See the system log for details.");
            }
        }

        return RedirectToAction(nameof(Configure));
    }

    #endregion

    #region Utilities

    private Task SaveAsync<T>(NmiSettings settings, System.Linq.Expressions.Expression<Func<NmiSettings, T>> key,
        bool overrideForStore, int storeScope) =>
        _settingService.SaveSettingOverridablePerStoreAsync(settings, key, overrideForStore, storeScope, false);

    /// <summary>
    /// Returns the last four characters of a stored secret for display next to its masked input, never the secret itself.
    /// </summary>
    private string GetSecretSuffix(string encryptedValue)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue))
            return null;

        try
        {
            var value = _encryptionService.DecryptText(encryptedValue)?.Trim();
            return string.IsNullOrEmpty(value) ? null : value[^Math.Min(4, value.Length)..];
        }
        catch
        {
            return null;
        }
    }

    private Task<bool> ExistsAsync<T>(NmiSettings settings, System.Linq.Expressions.Expression<Func<NmiSettings, T>> key,
        int storeScope) => _settingService.SettingExistsAsync(settings, key, storeScope);

    private async Task<ConfigurationModel> PrepareModelAsync(NmiSettings settings, int storeScope, ConfigurationModel model = null)
    {
        model ??= new ConfigurationModel
        {
            UseSandbox = settings.UseSandbox,
            GatewayHost = settings.GatewayHost,
            PublicTokenizationKey = settings.PublicTokenizationKey,
            TransactionModeId = (int)settings.TransactionMode,
            ProcessorId = settings.ProcessorId,
            DuplicateCheckSeconds = settings.DuplicateCheckSeconds,
            AdditionalFee = settings.AdditionalFee,
            AdditionalFeePercentage = settings.AdditionalFeePercentage,
            EnableDebugLogging = settings.EnableDebugLogging
        };

        // Never echo a submitted or stored secret back into the response.
        model.PrivateApiKey = NmiSecretProtector.Mask(settings.EncryptedPrivateApiKey);
        model.PrivateApiKeySuffix = GetSecretSuffix(settings.EncryptedPrivateApiKey);
        model.WebhookSigningKey = NmiSecretProtector.Mask(settings.EncryptedWebhookSigningKey);
        model.ActiveStoreScopeConfiguration = storeScope;
        model.TransactionModes = await settings.TransactionMode.ToSelectListAsync();
        model.WebhookUrl = $"{_webHelper.GetStoreLocation()}{NmiDefaults.WebhookRoute}";
        if (storeScope <= 0)
            return model;

        model.UseSandbox_OverrideForStore = await ExistsAsync(settings, x => x.UseSandbox, storeScope);
        model.GatewayHost_OverrideForStore = await ExistsAsync(settings, x => x.GatewayHost, storeScope);
        model.PublicTokenizationKey_OverrideForStore = await ExistsAsync(settings, x => x.PublicTokenizationKey, storeScope);
        model.PrivateApiKey_OverrideForStore = await ExistsAsync(settings, x => x.EncryptedPrivateApiKey, storeScope);
        model.WebhookSigningKey_OverrideForStore = await ExistsAsync(settings, x => x.EncryptedWebhookSigningKey, storeScope);
        model.TransactionModeId_OverrideForStore = await ExistsAsync(settings, x => x.TransactionMode, storeScope);
        model.ProcessorId_OverrideForStore = await ExistsAsync(settings, x => x.ProcessorId, storeScope);
        model.DuplicateCheckSeconds_OverrideForStore = await ExistsAsync(settings, x => x.DuplicateCheckSeconds, storeScope);
        model.AdditionalFee_OverrideForStore = await ExistsAsync(settings, x => x.AdditionalFee, storeScope);
        model.AdditionalFeePercentage_OverrideForStore = await ExistsAsync(settings, x => x.AdditionalFeePercentage, storeScope);
        model.EnableDebugLogging_OverrideForStore = await ExistsAsync(settings, x => x.EnableDebugLogging, storeScope);
        return model;
    }

    #endregion
}
