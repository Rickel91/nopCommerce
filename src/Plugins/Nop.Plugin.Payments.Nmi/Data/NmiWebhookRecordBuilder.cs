using FluentMigrator.Builders.Create.Table;
using Nop.Data.Mapping.Builders;
using Nop.Plugin.Payments.Nmi.Domain;

namespace Nop.Plugin.Payments.Nmi.Data;

public class NmiWebhookRecordBuilder : NopEntityBuilder<NmiWebhookRecord>
{
    public override void MapEntity(CreateTableExpressionBuilder table)
    {
        table
            .WithColumn(nameof(NmiWebhookRecord.EventId)).AsString(100).NotNullable()
            .WithColumn(nameof(NmiWebhookRecord.EventType)).AsString(100).NotNullable()
            .WithColumn(nameof(NmiWebhookRecord.TransactionId)).AsString(100).Nullable()
            .WithColumn(nameof(NmiWebhookRecord.OrderId)).AsInt32().NotNullable()
            .WithColumn(nameof(NmiWebhookRecord.ProcessingStateId)).AsInt32().NotNullable()
            .WithColumn(nameof(NmiWebhookRecord.Diagnostic)).AsString(1000).Nullable()
            .WithColumn(nameof(NmiWebhookRecord.ReceivedOnUtc)).AsDateTime2().NotNullable()
            .WithColumn(nameof(NmiWebhookRecord.OccurredOnUtc)).AsDateTime2().Nullable()
            .WithColumn(nameof(NmiWebhookRecord.ProcessedOnUtc)).AsDateTime2().Nullable();
    }
}
