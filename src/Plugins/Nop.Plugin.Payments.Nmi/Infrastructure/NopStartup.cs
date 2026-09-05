using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nop.Core.Infrastructure;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Web.Framework.Infrastructure.Extensions;

namespace Nop.Plugin.Payments.Nmi.Infrastructure;

public class NopStartup : INopStartup
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<NmiHttpClient>(client => client.Timeout = TimeSpan.FromSeconds(30)).WithProxy();
        services.AddScoped<NmiLogger>();
        services.AddScoped<NmiPaymentRequestBuilder>();
        services.AddSingleton<NmiPaymentExecutionGuard>();
        services.AddScoped<NmiPaymentProcessor>();
        services.AddScoped<NmiOrderOperationService>();
        services.AddScoped<INmiWebhookRecordRepository, NmiWebhookRecordRepository>();
        services.AddScoped<NmiWebhookRecordService>();
        services.AddScoped<NmiWebhookProcessor>();
        services.AddScoped<NmiWebhookSigningKeyProvider>();
    }

    public void Configure(IApplicationBuilder application)
    {
    }

    public int Order => 3000;
}
