using Nop.Services.Configuration;
using Nop.Services.Security;
using Nop.Services.Stores;

namespace Nop.Plugin.Payments.Nmi.Services;

public class NmiWebhookSigningKeyProvider
{
    private readonly IEncryptionService _encryptionService;
    private readonly ISettingService _settingService;
    private readonly IStoreService _storeService;

    public NmiWebhookSigningKeyProvider(IEncryptionService encryptionService,
        ISettingService settingService,
        IStoreService storeService)
    {
        _encryptionService = encryptionService;
        _settingService = settingService;
        _storeService = storeService;
    }

    public async Task<IReadOnlyCollection<NmiWebhookSigningKey>> GetSigningKeysAsync()
    {
        var encryptedKeys = new List<(int StoreId, string Value)>();
        var defaultSettings = await _settingService.LoadSettingAsync<NmiSettings>();
        var stores = await _storeService.GetAllStoresAsync();
        if (stores.Count == 0)
            AddIfPresent(encryptedKeys, 0, defaultSettings.EncryptedWebhookSigningKey);
        foreach (var store in stores)
        {
            var settings = await _settingService.LoadSettingAsync<NmiSettings>(store.Id);
            AddIfPresent(encryptedKeys, store.Id, settings.EncryptedWebhookSigningKey);
        }

        return encryptedKeys
            .Distinct()
            .Select(key => new NmiWebhookSigningKey(key.StoreId, Decrypt(key.Value)))
            .Where(key => !string.IsNullOrWhiteSpace(key.Value))
            .Distinct()
            .ToArray();
    }

    private string Decrypt(string encryptedKey)
    {
        try
        {
            return _encryptionService.DecryptText(encryptedKey);
        }
        catch
        {
            return null;
        }
    }

    private static void AddIfPresent(ICollection<(int StoreId, string Value)> keys, int storeId, string key)
    {
        if (!string.IsNullOrWhiteSpace(key))
            keys.Add((storeId, key));
    }
}

public sealed record NmiWebhookSigningKey(int StoreId, string Value);
