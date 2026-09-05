using System.Text.Json.Serialization;

namespace Nop.Plugin.Payments.Nmi.Services.Api;

public record NmiWebhookEvent
{
    [JsonPropertyName("event_id")]
    public string EventId { get; init; }

    [JsonPropertyName("event_type")]
    public string EventType { get; init; }

    [JsonPropertyName("event_body")]
    public NmiWebhookEventBody EventBody { get; init; }
}

public record NmiWebhookEventBody
{
    [JsonPropertyName("transaction_id")]
    public string TransactionId { get; init; }

    [JsonPropertyName("transaction_type")]
    public string TransactionType { get; init; }

    [JsonPropertyName("condition")]
    public string Condition { get; init; }

    [JsonPropertyName("order_id")]
    public string OrderId { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; }

    [JsonPropertyName("requested_amount")]
    public string RequestedAmount { get; init; }

    [JsonPropertyName("features")]
    public NmiWebhookFeatures Features { get; init; }

    [JsonPropertyName("action")]
    public NmiWebhookAction Action { get; init; }
}

public record NmiWebhookFeatures
{
    [JsonPropertyName("is_test_mode")]
    public bool IsTestMode { get; init; }
}

public record NmiWebhookAction
{
    [JsonPropertyName("amount")]
    public string Amount { get; init; }

    [JsonPropertyName("action_type")]
    public string ActionType { get; init; }

    [JsonPropertyName("date")]
    public string Date { get; init; }

    [JsonPropertyName("success")]
    public string Success { get; init; }

    [JsonPropertyName("response_text")]
    public string ResponseText { get; init; }

    [JsonPropertyName("response_code")]
    public string ResponseCode { get; init; }
}
