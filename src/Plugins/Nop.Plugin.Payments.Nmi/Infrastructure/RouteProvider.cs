using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Nop.Web.Framework.Mvc.Routing;

namespace Nop.Plugin.Payments.Nmi.Infrastructure;

public class RouteProvider : IRouteProvider
{
    public void RegisterRoutes(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapControllerRoute(
            name: NmiDefaults.WebhookRouteName,
            pattern: NmiDefaults.WebhookRoute,
            defaults: new { controller = "NmiWebhook", action = "Transactions" });
    }

    public int Priority => 0;
}
