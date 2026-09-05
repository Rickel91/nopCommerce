# NMI Payments for nopCommerce 4.60

`Nop.Plugin.Payments.Nmi` accepts card payments through the NMI gateway using the
Classic integration surface:

- **Collect.js** hosted fields tokenize the card in the shopper's browser. The
  script is always loaded from the gateway host (`/token/Collect.js`), never
  self-hosted, and returns a single-use `payment_token`.
- **Payment API** (`/api/transact.php`) receives the token for `sale` or `auth`
  transactions and handles `capture`, `void` and `refund` follow-ons.
- **Query API** (`/api/query.php`) backs the admin "Test connection" button.
- **Transaction webhooks** (`/nmi/webhooks/transactions`) reconcile order state
  from signed merchant-portal webhook events.

Gateway.js (`/js/v1/Gateway.js`) is NMI's companion library for 3-D Secure and
Kount. It does not tokenize cards on its own and is not loaded by this plugin.

## Configuration

| Setting | Purpose |
| --- | --- |
| Use NMI sandbox | Route everything to `sandbox.nmi.com` with sandbox keys. |
| Gateway host | Production host that serves transact.php and Collect.js, e.g. `tbtech.transactiongateway.com`. |
| Public tokenization key | Public security key with the **Tokenization** permission (used by Collect.js). |
| Private API security key | Private security key with the **API** permission (sent as `security_key`). Encrypted at rest. |
| Webhook signing key | Signing key from **Settings > Webhooks** in the merchant portal. Encrypted at rest. |
| Transaction mode | Authorize only, or authorize and capture (sale). |
| Processor ID | Optional multi-MID routing (`processor_id`). |
| Duplicate check seconds | `dup_seconds` window; 0 disables duplicate checking. |

## Build

The project lives under `src/Plugins` and references `Nop.Web` like the bundled
nopCommerce plugins. Build it from the solution:

```powershell
dotnet build src\Plugins\Nop.Plugin.Payments.Nmi\Nop.Plugin.Payments.Nmi.csproj -c Release
```

Output is copied to `src/Presentation/Nop.Web/Plugins/Payments.Nmi`.

## Tests

```powershell
dotnet test src\Tests\Nop.Plugin.Payments.Nmi.Tests\Nop.Plugin.Payments.Nmi.Tests.csproj
```
