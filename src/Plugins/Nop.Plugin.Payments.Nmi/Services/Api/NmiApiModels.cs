using System.Globalization;

namespace Nop.Plugin.Payments.Nmi.Services.Api;

/// <summary>
/// Values of the Payment API <c>type</c> variable used by this plugin.
/// </summary>
public static class NmiTransactionTypes
{
    public const string Sale = "sale";
    public const string Authorize = "auth";
    public const string Capture = "capture";
    public const string Void = "void";
    public const string Refund = "refund";
}

/// <summary>
/// An initial (sale or auth) Payment API transaction built from trusted checkout data plus a Collect.js token.
/// </summary>
public sealed record NmiPaymentRequest
{
    public string TransactionType { get; init; } = NmiTransactionTypes.Sale;
    public decimal Amount { get; init; }
    public string Currency { get; init; }
    public string PaymentToken { get; init; }
    public string ProcessorId { get; init; }
    public int? DuplicateCheckSeconds { get; init; }
    public string Industry { get; init; } = "ecommerce";
    public string OrderId { get; init; }
    public string OrderDescription { get; init; }
    public string IpAddress { get; init; }
    public NmiAddress BillingAddress { get; init; }
    public NmiAddress ShippingAddress { get; init; }

    /// <summary>
    /// Converts the request to Payment API form variables. Empty values are omitted; <c>security_key</c> is added by the client.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToFormFields()
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            Field("type", TransactionType),
            Field("payment_token", PaymentToken),
            Field("amount", NmiFormat.Amount(Amount)),
            Field("currency", Currency),
            Field("orderid", OrderId),
            Field("order_description", OrderDescription),
            Field("ipaddress", IpAddress),
            Field("industry", Industry),
            Field("processor_id", ProcessorId),
            Field("dup_seconds", DuplicateCheckSeconds?.ToString(CultureInfo.InvariantCulture))
        };

        if (BillingAddress is not null)
        {
            fields.Add(Field("first_name", BillingAddress.FirstName));
            fields.Add(Field("last_name", BillingAddress.LastName));
            fields.Add(Field("company", BillingAddress.Company));
            fields.Add(Field("address1", BillingAddress.Address1));
            fields.Add(Field("address2", BillingAddress.Address2));
            fields.Add(Field("city", BillingAddress.City));
            fields.Add(Field("state", BillingAddress.State));
            fields.Add(Field("zip", BillingAddress.Zip));
            fields.Add(Field("country", BillingAddress.Country));
            fields.Add(Field("phone", BillingAddress.Phone));
            fields.Add(Field("fax", BillingAddress.Fax));
            fields.Add(Field("email", BillingAddress.Email));
        }

        if (ShippingAddress is not null)
        {
            fields.Add(Field("shipping_firstname", ShippingAddress.FirstName));
            fields.Add(Field("shipping_lastname", ShippingAddress.LastName));
            fields.Add(Field("shipping_company", ShippingAddress.Company));
            fields.Add(Field("shipping_address1", ShippingAddress.Address1));
            fields.Add(Field("shipping_address2", ShippingAddress.Address2));
            fields.Add(Field("shipping_city", ShippingAddress.City));
            fields.Add(Field("shipping_state", ShippingAddress.State));
            fields.Add(Field("shipping_zip", ShippingAddress.Zip));
            fields.Add(Field("shipping_country", ShippingAddress.Country));
            fields.Add(Field("shipping_email", ShippingAddress.Email));
        }

        return fields.Where(field => !string.IsNullOrEmpty(field.Value)).ToArray();
    }

    private static KeyValuePair<string, string> Field(string name, string value) => new(name, value);
}

public record NmiAddress
{
    public string FirstName { get; init; }
    public string LastName { get; init; }
    public string Company { get; init; }
    public string Address1 { get; init; }
    public string Address2 { get; init; }
    public string City { get; init; }
    public string State { get; init; }
    public string Zip { get; init; }
    public string Country { get; init; }
    public string Email { get; init; }
    public string Phone { get; init; }
    public string Fax { get; init; }
}

/// <summary>
/// The Payment API response, delivered as an ampersand-delimited query string in the HTTP body.
/// </summary>
public sealed record NmiTransactionResponse
{
    /// <summary>1 = approved, 2 = declined, 3 = error.</summary>
    public string Response { get; init; }
    public string ResponseText { get; init; }
    public string AuthorizationCode { get; init; }
    public string TransactionId { get; init; }
    public string AvsResponse { get; init; }
    public string CvvResponse { get; init; }
    public string OrderId { get; init; }
    public string Type { get; init; }
    public string ResponseCode { get; init; }

    public bool IsApproved => Response == "1";
    public bool IsDeclined => Response == "2";
    public bool IsError => Response == "3";

    /// <summary>
    /// True when the gateway rejected the security key rather than the transaction.
    /// </summary>
    public bool IsAuthenticationFailure =>
        IsError && (ResponseCode == "300" &&
                    ResponseText?.Contains("authentication", StringComparison.OrdinalIgnoreCase) == true ||
                    ResponseText?.Contains("security key", StringComparison.OrdinalIgnoreCase) == true);

    public static bool TryParse(string body, out NmiTransactionResponse response)
    {
        response = null;
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var fields = ParseFormBody(body);
        if (!fields.TryGetValue("response", out var code) || code is not ("1" or "2" or "3"))
            return false;

        response = new NmiTransactionResponse
        {
            Response = code,
            ResponseText = Get(fields, "responsetext"),
            AuthorizationCode = Get(fields, "authcode"),
            TransactionId = Get(fields, "transactionid"),
            AvsResponse = Get(fields, "avsresponse"),
            CvvResponse = Get(fields, "cvvresponse"),
            OrderId = Get(fields, "orderid"),
            Type = Get(fields, "type"),
            ResponseCode = Get(fields, "response_code")
        };
        return true;
    }

    internal static IReadOnlyDictionary<string, string> ParseFormBody(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Decode(separator < 0 ? pair : pair[..separator]).Trim();
            var value = separator < 0 ? string.Empty : Decode(pair[(separator + 1)..]).Trim();
            if (key.Length > 0 && !result.ContainsKey(key))
                result[key] = value;
        }

        return result;
    }

    private static string Get(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}

/// <summary>
/// A transport or HTTP-level failure. Gateway declines and errors are reported inside <see cref="NmiTransactionResponse"/>.
/// </summary>
public sealed record NmiErrorResponse
{
    public string Type { get; init; }
    public string ErrorCode { get; init; }
    public string Message { get; init; }
}

public record NmiApiResult<T>(T Value, NmiErrorResponse Error, int StatusCode, bool IsAmbiguous = false)
{
    public bool IsSuccess => !IsAmbiguous && Error is null && StatusCode is >= 200 and < 300;

    /// <summary>
    /// Round-trip time of the gateway call, for diagnostics.
    /// </summary>
    public long ElapsedMilliseconds { get; init; }
}

/// <summary>
/// Outcome of an admin "test connection" check against the Query API.
/// </summary>
public sealed record NmiCredentialCheckResult(bool Accepted, string Detail, int StatusCode, long ElapsedMilliseconds);

public static class NmiFormat
{
    public static string Amount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);
}
