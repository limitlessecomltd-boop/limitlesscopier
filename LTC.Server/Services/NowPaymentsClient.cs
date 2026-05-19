using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace LTC.Server.Services;

/// <summary>
/// Configuration injected via Options pattern.
/// Bound from environment variables NowPayments__ApiKey and NowPayments__IpnSecret.
/// </summary>
public class NowPaymentsOptions
{
    public string ApiKey { get; set; } = "";
    public string IpnSecret { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.nowpayments.io/v1";
}

/// <summary>
/// HTTP client for NowPayments REST API.
///
/// Two surfaces:
///   1. CreateInvoiceAsync - calls POST /v1/invoice to get a hosted checkout URL
///   2. VerifyIpnSignature - validates the x-nowpayments-sig header on incoming webhooks
///
/// Signature algorithm (from NowPayments docs):
///   1. Take all params from the webhook body as a JSON object
///   2. Sort keys alphabetically
///   3. Re-serialize the sorted object to JSON (this is the canonical form)
///   4. HMAC-SHA512 with IPN secret as key, canonical JSON as message
///   5. Hex-encode the result
///   6. Compare to x-nowpayments-sig header (case-insensitive)
///
/// The sort step is critical - NowPayments signs THEIR canonical form
/// and we MUST recreate it byte-for-byte. Differences in whitespace,
/// number formatting, etc. will cause signature mismatches.
/// </summary>
public class NowPaymentsClient
{
    private readonly HttpClient _http;
    private readonly NowPaymentsOptions _opts;
    private readonly ILogger<NowPaymentsClient> _log;

    public NowPaymentsClient(
        HttpClient http,
        IOptions<NowPaymentsOptions> opts,
        ILogger<NowPaymentsClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;

        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
            _log.LogWarning("NowPayments:ApiKey is not configured - crypto checkout will fail");
        if (string.IsNullOrWhiteSpace(_opts.IpnSecret))
            _log.LogWarning("NowPayments:IpnSecret is not configured - webhooks cannot be verified");

        _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Create a hosted invoice. Returns the invoice URL the customer should be
    /// redirected to in order to pay.
    /// </summary>
    public async Task<CreateInvoiceResult> CreateInvoiceAsync(
        string orderId,
        decimal amountUsd,
        string customerEmail,
        string orderDescription,
        string payCurrency,            // "usdttrc20" for USDT TRC20
        string ipnCallbackUrl,
        string successUrl,
        string cancelUrl,
        CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "invoice");
        req.Headers.Add("x-api-key", _opts.ApiKey);

        var body = new
        {
            price_amount = amountUsd,
            price_currency = "usd",
            pay_currency = payCurrency,
            order_id = orderId,
            order_description = orderDescription,
            ipn_callback_url = ipnCallbackUrl,
            success_url = successUrl,
            cancel_url = cancelUrl,
            is_fee_paid_by_user = false,
            // customer_email surfaces in NowPayments dashboard but is NOT used for delivery
            customer_email = customerEmail,
        };
        req.Content = JsonContent.Create(body);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogError("NowPayments invoice creation failed: {Status} {Body}", resp.StatusCode, raw);
            throw new InvalidOperationException(
                $"NowPayments returned {(int)resp.StatusCode}: {raw}");
        }

        // NowPayments invoice response shape (camelCase keys in JSON):
        //   id, token_id, order_id, order_description, price_amount, price_currency,
        //   pay_currency, ipn_callback_url, invoice_url, success_url, cancel_url,
        //   created_at, updated_at
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        return new CreateInvoiceResult
        {
            InvoiceId = root.TryGetProperty("id", out var id) ? id.ToString() : "",
            InvoiceUrl = root.TryGetProperty("invoice_url", out var url) ? url.GetString() ?? "" : "",
            RawResponse = raw,
        };
    }

    /// <summary>
    /// Verify the x-nowpayments-sig header on an incoming webhook.
    /// Returns true if the signature matches; false otherwise.
    ///
    /// CRITICAL: bodyJson must be the EXACT raw bytes NowPayments sent,
    /// not a re-serialized version - because we need to extract their
    /// fields and re-sort them to compute the canonical form.
    /// </summary>
    public bool VerifyIpnSignature(string bodyJson, string? receivedSignature)
    {
        if (string.IsNullOrWhiteSpace(_opts.IpnSecret))
        {
            _log.LogError("Cannot verify IPN: IpnSecret not configured");
            return false;
        }
        if (string.IsNullOrWhiteSpace(receivedSignature))
        {
            _log.LogWarning("IPN missing x-nowpayments-sig header");
            return false;
        }

        try
        {
            // Parse the body into a dictionary, sort keys, re-serialize.
            // NowPayments' canonical form is: JSON.stringify(params, Object.keys(params).sort())
            // which produces a JSON object with keys in alphabetical order, default JSON.stringify
            // formatting (no whitespace, no escaping non-ASCII unless needed).
            using var doc = JsonDocument.Parse(bodyJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                _log.LogWarning("IPN body is not a JSON object");
                return false;
            }

            var canonical = BuildCanonicalJson(doc.RootElement);
            var expected = HmacSha512Hex(_opts.IpnSecret, canonical);

            var match = string.Equals(expected, receivedSignature, StringComparison.OrdinalIgnoreCase);
            if (!match)
            {
                _log.LogWarning("IPN signature mismatch. Expected {Expected}, received {Received}",
                    expected, receivedSignature);
            }
            return match;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "IPN signature verification threw");
            return false;
        }
    }

    /// <summary>
    /// Build the canonical JSON form for HMAC signing:
    /// keys sorted alphabetically, default JSON.stringify formatting (no spaces).
    /// Matches JavaScript: JSON.stringify(params, Object.keys(params).sort())
    /// </summary>
    private static string BuildCanonicalJson(JsonElement obj)
    {
        var sortedProps = obj.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append('{');
        for (int i = 0; i < sortedProps.Count; i++)
        {
            if (i > 0) sb.Append(',');
            // Key: always a JSON string
            sb.Append('"').Append(JsonEscape(sortedProps[i].Name)).Append('"').Append(':');
            // Value: serialize per JSON.stringify rules
            AppendValue(sb, sortedProps[i].Value);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendValue(StringBuilder sb, JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append(BuildCanonicalJson(v));
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                int idx = 0;
                foreach (var item in v.EnumerateArray())
                {
                    if (idx++ > 0) sb.Append(',');
                    AppendValue(sb, item);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                sb.Append('"').Append(JsonEscape(v.GetString() ?? "")).Append('"');
                break;
            case JsonValueKind.Number:
                // Use the raw text from the source - preserves int vs float, precision
                sb.Append(v.GetRawText());
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            default:
                sb.Append(v.GetRawText());
                break;
        }
    }

    /// <summary>Minimal JSON string escape - just the chars JS.stringify must escape.</summary>
    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string HmacSha512Hex(string secret, string message)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

public class CreateInvoiceResult
{
    public string InvoiceId { get; set; } = "";
    public string InvoiceUrl { get; set; } = "";
    public string RawResponse { get; set; } = "";
}
