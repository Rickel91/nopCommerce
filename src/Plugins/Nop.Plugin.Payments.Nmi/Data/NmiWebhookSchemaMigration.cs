using FluentMigrator;
using Nop.Data.Extensions;
using Nop.Data.Mapping;
using Nop.Data.Migrations;
using Nop.Plugin.Payments.Nmi.Domain;

namespace Nop.Plugin.Payments.Nmi.Data;

[NopMigration("2026-07-18 23:50:00", "Payments.Nmi webhook schema", MigrationProcessType.Installation)]
public class NmiWebhookSchemaMigration : Migration
{
    public override void Up()
    {
        var tableName = NameCompatibilityManager.GetTableName(typeof(NmiWebhookRecord));
        if (!Schema.Table(tableName).Exists())
            Create.TableFor<NmiWebhookRecord>();

        if (!Schema.Table(tableName).Index("UX_NmiWebhookRecord_EventId").Exists())
        {
            Create.Index("UX_NmiWebhookRecord_EventId")
                .OnTable(tableName)
                .OnColumn(nameof(NmiWebhookRecord.EventId)).Ascending()
                .WithOptions().Unique();
        }

        if (!Schema.Table(tableName).Index("IX_NmiWebhookRecord_ReceivedOnUtc").Exists())
        {
            Create.Index("IX_NmiWebhookRecord_ReceivedOnUtc")
                .OnTable(tableName)
                .OnColumn(nameof(NmiWebhookRecord.ReceivedOnUtc)).Ascending()
                .WithOptions().NonClustered();
        }
    }

    public override void Down()
    {
        Delete.Table(NameCompatibilityManager.GetTableName(typeof(NmiWebhookRecord)));
    }
}
