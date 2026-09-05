using Nop.Services.ScheduleTasks;

namespace Nop.Plugin.Payments.Nmi.Services;

public class NmiWebhookCleanupTask : IScheduleTask
{
    private readonly NmiLogger _log;
    private readonly NmiWebhookRecordService _recordService;

    public NmiWebhookCleanupTask(NmiLogger log, NmiWebhookRecordService recordService)
    {
        _log = log ?? NmiLogger.None;
        _recordService = recordService;
    }

    public async Task ExecuteAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-NmiDefaults.WebhookRetentionDays);
        var deleted = await _recordService.DeleteExpiredAsync(cutoff);
        if (deleted > 0)
            await _log.InformationAsync($"webhook cleanup removed {deleted} record(s) received before {cutoff:O}.");
    }
}
