using Nop.Core;

namespace Nop.Plugin.Payments.Nmi.Domain;

public class NmiWebhookRecord : BaseEntity
{
    public string EventId { get; set; }
    public string EventType { get; set; }
    public string TransactionId { get; set; }
    public int OrderId { get; set; }
    public int ProcessingStateId { get; set; }
    public string Diagnostic { get; set; }
    public DateTime ReceivedOnUtc { get; set; }
    public DateTime? OccurredOnUtc { get; set; }
    public DateTime? ProcessedOnUtc { get; set; }

    public NmiWebhookProcessingState ProcessingState
    {
        get => (NmiWebhookProcessingState)ProcessingStateId;
        set => ProcessingStateId = (int)value;
    }
}

public enum NmiWebhookProcessingState
{
    Processing = 10,
    Processed = 20,
    Ignored = 30,
    Failed = 40
}
