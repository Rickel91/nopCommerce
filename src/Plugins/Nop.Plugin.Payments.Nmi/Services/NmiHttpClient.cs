using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Nop.Plugin.Payments.Nmi.Services.Api;
using Nop.Services.Security;

namespace Nop.Plugin.Payments.Nmi.Services;

/// <summary>
/// Sends requests to the NMI Classic Payment API (transact.php) and Query API (query.php).
/// Requests are form-encoded POSTs authenticated with the merchant's private security key.
/// </summary>
public partial class NmiHttpClient
{
    /// <summary>
    /// JSON options for webhook payloads (the Payment API itself is not JSON).
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?)+$")]
    private static partial Regex GatewayHostPattern();

    private readonly HttpClient _httpClient;
    private readonly IEncryptionService _encryptionService;

    public NmiHttpClient(HttpClient httpClient, IEncryptionService encryptionService)
    {
        _httpClient = httpClient;
        _encryptionService = encryptionService;
    }

    #region Endpoints

    public static bool IsValidGatewayHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var candidate = host.Trim().ToLowerInvariant();
        return candidate.Length <= 253 && GatewayHostPattern().IsMatch(candidate);
    }

    public static Uri GetBaseUri(NmiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return GetBaseUri(settings.UseSandbox, settings.GatewayHost);
    }

    public static Uri GetBaseUri(bool useSandbox, string gatewayHost)
    {
        if (useSandbox)
            return NmiDefaults.SandboxBaseUri;

        var host = string.IsNullOrWhiteSpace(gatewayHost)
            ? NmiDefaults.DefaultGatewayHost
            : gatewayHost.Trim().ToLowerInvariant();
        if (!IsValidGatewayHost(host))
            throw new ArgumentException("The configured NMI gateway host is not a valid host name.", nameof(gatewayHost));

        return new Uri($"https://{host}/");
    }

    public static Uri GetCollectJsUri(NmiSettings settings) => new(GetBaseUri(settings), NmiDefaults.CollectJsPath);

    #endregion

    #region Query API

    /// <summary>
    /// Confirms the private security key is accepted by issuing a minimal Query API request.
    /// </summary>
    public async Task<bool> ValidateCredentialsAsync(NmiSettings settings, CancellationToken cancellationToken = default) =>
        (await CheckCredentialsAsync(settings, cancellationToken)).Accepted;

    /// <summary>
    /// Same as <see cref="ValidateCredentialsAsync"/> but returns a diagnostic detail suitable for logging.
    /// The check sends a void for a transaction id that cannot exist. It never moves money and works for every
    /// account type, including NMI's public demo account, which is barred from the Query API. The gateway answers
    /// "Authentication Failed" for a bad key and "Invalid Transaction ID" (or similar) for an accepted one.
    /// </summary>
    public async Task<NmiCredentialCheckResult> CheckCredentialsAsync(NmiSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = await SendTransactionAsync(settings, new[]
        {
            new KeyValuePair<string, string>("type", NmiTransactionTypes.Void),
            new KeyValuePair<string, string>("transactionid", "0")
        }, cancellationToken);

        if (result.IsAmbiguous || !result.IsSuccess)
        {
            var reason = result.Error?.ErrorCode ?? $"HTTP_{result.StatusCode}";
            return new(false, $"gateway_unreachable={reason}", result.StatusCode, result.ElapsedMilliseconds);
        }

        var response = result.Value;
        var text = Truncate(response.ResponseText ?? "none", 120);
        return response.IsAuthenticationFailure
            ? new(false, $"gateway_error={text}", result.StatusCode, result.ElapsedMilliseconds)
            : new(true, $"accepted; probe_response={text}", result.StatusCode, result.ElapsedMilliseconds);
    }

    #endregion

    #region Payment API

    public Task<NmiApiResult<NmiTransactionResponse>> ProcessPaymentAsync(
        NmiSettings settings,
        NmiPaymentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendTransactionAsync(settings, request.ToFormFields(), cancellationToken);
    }

    public Task<NmiApiResult<NmiTransactionResponse>> CapturePaymentAsync(
        NmiSettings settings,
        string transactionId,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        ValidateTransactionId(transactionId);
        return SendTransactionAsync(settings, new[]
        {
            new KeyValuePair<string, string>("type", NmiTransactionTypes.Capture),
            new KeyValuePair<string, string>("transactionid", transactionId),
            new KeyValuePair<string, string>("amount", NmiFormat.Amount(amount))
        }, cancellationToken);
    }

    public Task<NmiApiResult<NmiTransactionResponse>> VoidPaymentAsync(
        NmiSettings settings,
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        ValidateTransactionId(transactionId);
        return SendTransactionAsync(settings, new[]
        {
            new KeyValuePair<string, string>("type", NmiTransactionTypes.Void),
            new KeyValuePair<string, string>("transactionid", transactionId)
        }, cancellationToken);
    }

    public Task<NmiApiResult<NmiTransactionResponse>> RefundPaymentAsync(
        NmiSettings settings,
        string transactionId,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        ValidateTransactionId(transactionId);
        return SendTransactionAsync(settings, new[]
        {
            new KeyValuePair<string, string>("type", NmiTransactionTypes.Refund),
            new KeyValuePair<string, string>("transactionid", transactionId),
            new KeyValuePair<string, string>("amount", NmiFormat.Amount(amount))
        }, cancellationToken);
    }

    internal async Task<NmiApiResult<NmiTransactionResponse>> SendTransactionAsync(
        NmiSettings settings,
        IEnumerable<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fields);

        var stopwatch = Stopwatch.StartNew();
        var result = await SendTransactionCoreAsync(settings, fields, cancellationToken);
        return result with { ElapsedMilliseconds = stopwatch.ElapsedMilliseconds };
    }

    private async Task<NmiApiResult<NmiTransactionResponse>> SendTransactionCoreAsync(
        NmiSettings settings,
        IEnumerable<KeyValuePair<string, string>> fields,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(GetBaseUri(settings), NmiDefaults.TransactApiPath);
        using var message = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = CreateFormContent(settings, fields)
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Ambiguous("TIMEOUT", "The NMI request timed out before its outcome was known.");
        }
        catch (HttpRequestException exception)
        {
            return Ambiguous("TRANSPORT", $"The NMI response was not received ({exception.GetType().Name}).");
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;
            string content;
            try
            {
                content = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException ||
                                              exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return Ambiguous("TRANSPORT", "The NMI response was interrupted.", statusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new NmiApiResult<NmiTransactionResponse>(null, new NmiErrorResponse
                {
                    Type = "httpError",
                    ErrorCode = $"HTTP_{statusCode}",
                    Message = string.IsNullOrWhiteSpace(content)
                        ? response.ReasonPhrase
                        : NmiRedactor.Redact(Truncate(content, 500))
                }, statusCode, IsAmbiguous: statusCode >= 500);
            }

            if (string.IsNullOrWhiteSpace(content))
                return Ambiguous("EMPTY_RESPONSE", "NMI returned an empty response.", statusCode);
            if (!NmiTransactionResponse.TryParse(content, out var value))
                return Ambiguous("MALFORMED_RESPONSE", "NMI returned an unreadable response.", statusCode);

            return new NmiApiResult<NmiTransactionResponse>(value, null, statusCode);
        }
    }

    #endregion

    #region Utilities

    private FormUrlEncodedContent CreateFormContent(NmiSettings settings, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var securityKey = _encryptionService.DecryptText(settings.EncryptedPrivateApiKey);
        var pairs = new List<KeyValuePair<string, string>> { new("security_key", securityKey) };
        pairs.AddRange(fields.Where(field =>
            !string.IsNullOrEmpty(field.Key) && !string.IsNullOrEmpty(field.Value) &&
            !string.Equals(field.Key, "security_key", StringComparison.OrdinalIgnoreCase)));
        return new FormUrlEncodedContent(pairs);
    }

    private static NmiApiResult<NmiTransactionResponse> Ambiguous(string code, string message, int statusCode = 0) =>
        new(null, new NmiErrorResponse
        {
            Type = "ambiguousError",
            ErrorCode = code,
            Message = message
        }, statusCode, IsAmbiguous: true);

    internal static Uri BuildRequestUri(Uri baseUri, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith('/') ||
            relativePath.StartsWith('\\') || relativePath.Contains('\\') || relativePath.Contains('%') ||
            Uri.TryCreate(relativePath, UriKind.Absolute, out _))
            throw new ArgumentException("Only relative NMI API paths are allowed.", nameof(relativePath));

        var requestUri = new Uri(baseUri, relativePath);
        if (!string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(requestUri.IdnHost, baseUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            requestUri.Port != baseUri.Port || !string.IsNullOrEmpty(requestUri.UserInfo) ||
            !string.IsNullOrEmpty(requestUri.Query) || !string.IsNullOrEmpty(requestUri.Fragment) ||
            !requestUri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            throw new ArgumentException("The NMI API path escaped the configured gateway origin.", nameof(relativePath));

        return requestUri;
    }

    internal static void ValidateTransactionId(string transactionId)
    {
        if (string.IsNullOrWhiteSpace(transactionId) || transactionId.Length > 100 ||
            transactionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("A valid NMI transaction ID is required.", nameof(transactionId));
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    #endregion
}
