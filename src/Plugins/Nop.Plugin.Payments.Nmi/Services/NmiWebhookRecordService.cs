using LinqToDB;
using Nop.Data;
using Nop.Plugin.Payments.Nmi.Domain;

namespace Nop.Plugin.Payments.Nmi.Services;

public class NmiWebhookRecordService
{
    private readonly INmiWebhookRecordRepository _repository;

    public NmiWebhookRecordService(INmiWebhookRecordRepository repository)
    {
        _repository = repository;
    }

    public async Task<NmiWebhookAcquireResult> AcquireAsync(NmiWebhookRecord record)
    {
        var existing = await _repository.FindByEventIdAsync(record.EventId);
        if (existing is not null)
            return await AcquireExistingAsync(existing);

        try
        {
            await _repository.InsertAsync(record);
            return new(NmiWebhookAcquireStatus.Acquired, record);
        }
        catch
        {
            existing = await _repository.FindByEventIdAsync(record.EventId);
            if (existing is null)
                throw;
            return await AcquireExistingAsync(existing);
        }
    }

    public async Task<DateTime?> GetLatestProcessedOccurrenceAsync(int orderId, int excludingRecordId)
    {
        return await _repository.GetLatestProcessedOccurrenceAsync(orderId, excludingRecordId);
    }

    public Task CompleteAsync(NmiWebhookRecord record,
        NmiWebhookProcessingState state,
        string diagnostic,
        int orderId = 0)
    {
        record.OrderId = orderId > 0 ? orderId : record.OrderId;
        record.ProcessingState = state;
        record.Diagnostic = TrimTo(diagnostic, 1000);
        record.ProcessedOnUtc = DateTime.UtcNow;
        return _repository.UpdateAsync(record);
    }

    public Task<int> DeleteExpiredAsync(DateTime cutoffUtc) =>
        _repository.DeleteExpiredAsync(cutoffUtc);

    private async Task<NmiWebhookAcquireResult> AcquireExistingAsync(NmiWebhookRecord record)
    {
        if (record.ProcessingState is NmiWebhookProcessingState.Processed or NmiWebhookProcessingState.Ignored)
            return new(NmiWebhookAcquireStatus.Completed, record);
        if (record.ProcessingState == NmiWebhookProcessingState.Processing)
            return new(NmiWebhookAcquireStatus.InProgress, record);

        record.ProcessingState = NmiWebhookProcessingState.Processing;
        record.Diagnostic = null;
        record.ProcessedOnUtc = null;
        await _repository.UpdateAsync(record);
        return new(NmiWebhookAcquireStatus.Acquired, record);
    }

    private static string TrimTo(string value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
}

public interface INmiWebhookRecordRepository
{
    Task<NmiWebhookRecord> FindByEventIdAsync(string eventId);
    Task InsertAsync(NmiWebhookRecord record);
    Task UpdateAsync(NmiWebhookRecord record);
    Task<DateTime?> GetLatestProcessedOccurrenceAsync(int orderId, int excludingRecordId);
    Task<int> DeleteExpiredAsync(DateTime cutoffUtc);
}

public class NmiWebhookRecordRepository : INmiWebhookRecordRepository
{
    private readonly IRepository<NmiWebhookRecord> _repository;

    public NmiWebhookRecordRepository(IRepository<NmiWebhookRecord> repository)
    {
        _repository = repository;
    }

    public Task<NmiWebhookRecord> FindByEventIdAsync(string eventId) =>
        _repository.Table.FirstOrDefaultAsync(record => record.EventId == eventId);

    public Task InsertAsync(NmiWebhookRecord record) => _repository.InsertAsync(record, false);

    public Task UpdateAsync(NmiWebhookRecord record) => _repository.UpdateAsync(record, false);

    public Task<DateTime?> GetLatestProcessedOccurrenceAsync(int orderId, int excludingRecordId) =>
        _repository.Table
            .Where(record => record.OrderId == orderId && record.Id != excludingRecordId &&
                             record.ProcessingStateId == (int)NmiWebhookProcessingState.Processed &&
                             record.OccurredOnUtc.HasValue)
            .MaxAsync(record => record.OccurredOnUtc);

    public Task<int> DeleteExpiredAsync(DateTime cutoffUtc) =>
        _repository.DeleteAsync(record => record.ReceivedOnUtc < cutoffUtc);
}

public record NmiWebhookAcquireResult(NmiWebhookAcquireStatus Status, NmiWebhookRecord Record);

public enum NmiWebhookAcquireStatus
{
    Acquired,
    Completed,
    InProgress
}
