using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nop.Plugin.Payments.Nmi.Services;
using Nop.Plugin.Payments.Nmi.Services.Api;

namespace Nop.Plugin.Payments.Nmi.Controllers;

public class NmiWebhookController : Controller
{
    internal const int MaximumBodyLength = 1_048_576;

    private readonly NmiLogger _log;
    private readonly NmiWebhookProcessor _processor;
    private readonly NmiWebhookSigningKeyProvider _signingKeyProvider;

    public NmiWebhookController(NmiLogger log,
        NmiWebhookProcessor processor,
        NmiWebhookSigningKeyProvider signingKeyProvider)
    {
        _log = log ?? NmiLogger.None;
        _processor = processor;
        _signingKeyProvider = signingKeyProvider;
    }

    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [HttpPost]
    [RequestSizeLimit(MaximumBodyLength)]
    public async Task<IActionResult> Transactions()
    {
        var remote = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (Request.ContentLength > MaximumBodyLength)
        {
            await _log.WarningAsync($"webhook rejected; reason=payload_too_large; length={Request.ContentLength}; remote={remote}.");
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var signatureHeaders = Request.Headers[NmiDefaults.WebhookSignatureHeader];
        if (signatureHeaders.Count != 1)
        {
            await _log.WarningAsync($"webhook rejected; reason=signature_header_{(signatureHeaders.Count == 0 ? "missing" : "duplicated")}; remote={remote}.");
            return Unauthorized();
        }

        byte[] rawBody;
        try
        {
            rawBody = await ReadRawBodyAsync(Request.Body, MaximumBodyLength, HttpContext.RequestAborted);
        }
        catch (InvalidDataException)
        {
            await _log.WarningAsync($"webhook rejected; reason=payload_too_large; remote={remote}.");
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var signingKeys = await _signingKeyProvider.GetSigningKeysAsync();
        if (signingKeys.Count == 0)
        {
            await _log.ErrorAsync($"webhook could not be verified because no webhook signing key is configured for any store; remote={remote}. Configure the key under Payment methods > NMI Payments.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var verification = NmiWebhookSignatureVerifier.VerifyForStores(rawBody, signatureHeaders[0], signingKeys,
            DateTimeOffset.UtcNow);
        if (verification.Verification != NmiWebhookSignatureVerification.Valid)
        {
            await _log.WarningAsync(
                $"webhook rejected; reason=signature_{verification.Verification.ToString().ToLowerInvariant()}; " +
                $"body_length={rawBody.Length}; remote={remote}.");
            return Unauthorized();
        }

        NmiWebhookEvent webhookEvent;
        try
        {
            webhookEvent = JsonSerializer.Deserialize<NmiWebhookEvent>(rawBody, NmiHttpClient.SerializerOptions);
        }
        catch (JsonException exception)
        {
            await _log.WarningAsync($"webhook rejected; reason=invalid_json; body_length={rawBody.Length}; remote={remote}.", exception);
            return BadRequest();
        }

        var result = await _processor.ProcessAsync(webhookEvent, verification.AuthorizedStoreIds);
        if (result == NmiWebhookProcessingResult.Malformed)
            await _log.WarningAsync(
                $"webhook rejected; reason=malformed_event; event={webhookEvent?.EventId ?? "unknown"}; " +
                $"type={webhookEvent?.EventType ?? "unknown"}; remote={remote}.");

        return result switch
        {
            NmiWebhookProcessingResult.Accepted => Ok(),
            NmiWebhookProcessingResult.Malformed => BadRequest(),
            NmiWebhookProcessingResult.Unauthorized => Unauthorized(),
            NmiWebhookProcessingResult.InProgress => StatusCode(StatusCodes.Status409Conflict),
            _ => StatusCode(StatusCodes.Status503ServiceUnavailable)
        };
    }

    private static async Task<byte[]> ReadRawBodyAsync(Stream stream,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > maximumLength)
                throw new InvalidDataException("NMI webhook body exceeded the configured limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }
}
