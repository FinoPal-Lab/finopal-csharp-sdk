using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FinoPal;

public sealed class FinoPalClient : IDisposable
{
    public const string ProductionUrl = "https://payment.finopal.ir";
    public const string SandboxUrl = "https://sandbox.finopal.ir";

    private static readonly Regex CallbackUrlPattern = new("^https?://\\S+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public FinoPalClient(string merchantId, bool sandbox = false, string? baseUrl = null, HttpClient? http = null)
    {
        MerchantId = (merchantId ?? "").Trim();
        if (MerchantId.Length == 0)
        {
            throw new FinoPalValidationException("merchantId الزامی است.");
        }

        var root = string.IsNullOrWhiteSpace(baseUrl)
            ? (sandbox ? SandboxUrl : ProductionUrl)
            : baseUrl.Trim();
        BaseUrl = root.TrimEnd('/');
        _ownsClient = http == null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string MerchantId { get; }
    public string BaseUrl { get; }

    public async Task<PaymentCreated> RequestAsync(PaymentRequest input, CancellationToken cancellationToken = default)
    {
        var amount = RequireAmount(input.Amount);
        var description = (input.Description ?? "").Trim();
        var callbackUrl = (input.CallbackUrl ?? "").Trim();
        var orderId = (input.OrderId ?? "").Trim();

        if (description.Length == 0 || CharacterLength(description) > 255)
        {
            throw new FinoPalValidationException("description الزامی است و حداکثر ۲۵۵ کاراکتر دارد.");
        }
        if (!CallbackUrlPattern.IsMatch(callbackUrl))
        {
            throw new FinoPalValidationException("callback_url باید یک آدرس http(s) معتبر باشد.");
        }
        if (orderId.Length == 0 || orderId.Length > 100)
        {
            throw new FinoPalValidationException("order_id الزامی است و حداکثر ۱۰۰ کاراکتر دارد.");
        }

        var payload = new JsonObject
        {
            ["amount"] = amount,
            ["description"] = description,
            ["callback_url"] = callbackUrl,
            ["order_id"] = orderId,
        };
        if (!string.IsNullOrWhiteSpace(input.Mobile)) payload["mobile"] = input.Mobile;
        if (!string.IsNullOrWhiteSpace(input.Email)) payload["email"] = input.Email;

        var response = await PostAsync("/api/v1/payment/request", payload, cancellationToken);
        var code = ExtractCode(response);
        var data = ExtractData(response);
        var authority = ReadString(data, "authority") ?? "";
        if (code != 100 || authority.Length == 0)
        {
            throw new FinoPalResponseException(ExtractMessage(response, "ایجاد تراکنش ناموفق بود."), code, response);
        }

        return new PaymentCreated(
            authority,
            ReadString(data, "payment_url") ?? RedirectUrl(authority),
            100,
            ExtractMessage(response, "تراکنش ایجاد شد."),
            ReadString(data, "expires_at"));
    }

    public string RedirectUrl(string authority)
    {
        var value = (authority ?? "").Trim();
        if (value.Length == 0)
        {
            throw new FinoPalValidationException("authority خالی است.");
        }
        return BaseUrl + "/api/v1/payment/redirect/" + Uri.EscapeDataString(value);
    }

    public async Task<PaymentVerified> VerifyAsync(string authority, long amount, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync("/api/v1/payment/verify", new JsonObject
        {
            ["authority"] = RequireAuthority(authority),
            ["amount"] = RequireAmount(amount),
        }, cancellationToken);
        var code = ExtractCode(response);
        if (code != 100 && code != 101)
        {
            throw new FinoPalResponseException(ExtractMessage(response, "تأیید تراکنش ناموفق بود."), code, response);
        }

        var data = ExtractData(response);
        return new PaymentVerified(
            code.Value,
            ReadString(data, "ref_id"),
            ReadString(data, "card_pan"),
            ExtractMessage(response, "پرداخت تأیید شد."));
    }

    public async Task<PaymentInquiry> InquireAsync(string authority, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync("/api/v1/payment/inquiry", new JsonObject
        {
            ["authority"] = RequireAuthority(authority),
        }, cancellationToken);
        if (ReadBool(response, "success") != true)
        {
            throw new FinoPalResponseException(ExtractMessage(response, "استعلام تراکنش ناموفق بود."), ExtractCode(response), response);
        }

        var data = ExtractData(response);
        var status = ReadString(data, "status") ?? ReadString(response, "status");
        return new PaymentInquiry(status, status == "successful", ExtractMessage(response, "استعلام انجام شد."), ExtractCode(response));
    }

    public async Task<PaymentReversal> ReverseAsync(string authority, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync("/api/v1/payment/reverse", new JsonObject
        {
            ["authority"] = RequireAuthority(authority),
        }, cancellationToken);
        if (ReadBool(response, "success") != true)
        {
            throw new FinoPalResponseException(ExtractMessage(response, "ریورس تراکنش ناموفق بود."), ExtractCode(response), response);
        }
        return new PaymentReversal(true, ExtractMessage(response, "تراکنش برگشت داده شد."), ExtractCode(response));
    }

    public async Task<FeeQuote> CalculateFeeAsync(long amount, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync("/api/v1/payment/feeCalculation", new JsonObject
        {
            ["amount"] = RequireAmount(amount),
        }, cancellationToken);
        var code = ExtractCode(response);
        if (ReadBool(response, "success") != true && code != 100)
        {
            throw new FinoPalResponseException(ExtractMessage(response, "محاسبه کارمزد ناموفق بود."), code, response);
        }

        var feeType = ReadString(response, "fee_type");
        return new FeeQuote(ReadLong(response, "amount"), ReadLong(response, "fee"), feeType, feeType == "Payer", ExtractMessage(response, "کارمزد محاسبه شد."), code);
    }

    public static bool ShouldVerify(string? status) => string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    private async Task<JsonObject> PostAsync(string path, JsonObject payload, CancellationToken cancellationToken)
    {
        payload["merchant_id"] = MerchantId;
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "finopal-csharp-sdk/1.0");
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (Exception error)
        {
            var detail = error is TaskCanceledException ? "مهلت درخواست به پایان رسید." : error.Message;
            throw new FinoPalTransportException("خطا در اتصال به فینوپال: " + detail, 0);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonObject decoded;
            try
            {
                decoded = JsonNode.Parse(body) as JsonObject ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new FinoPalTransportException("پاسخ فینوپال JSON معتبر نیست.", (int)response.StatusCode);
            }
            if ((int)response.StatusCode >= 500)
            {
                throw new FinoPalTransportException("سرور فینوپال پاسخ ناموفق داد.", (int)response.StatusCode);
            }
            return decoded;
        }
    }

    private static long RequireAmount(long amount)
    {
        if (amount < 1000) throw new FinoPalValidationException("مبلغ باید عدد صحیح و حداقل ۱۰۰۰ ریال باشد.");
        return amount;
    }

    private static string RequireAuthority(string authority)
    {
        var value = (authority ?? "").Trim();
        if (value.Length == 0) throw new FinoPalValidationException("authority الزامی است.");
        return value;
    }

    private static int CharacterLength(string value) => new StringInfo(value).LengthInTextElements;

    private static int? ExtractCode(JsonObject response)
    {
        foreach (var candidate in new JsonNode?[] { response["code"], response["data"]?["code"], response["errors"]?["code"] })
        {
            if (candidate == null || candidate is JsonValue value && value.TryGetValue<bool>(out _)) continue;
            if (candidate is JsonValue number && number.TryGetValue<long>(out var parsed)) return (int)parsed;
            if (candidate is JsonValue text && long.TryParse(text.ToString().Trim(), out parsed)) return (int)parsed;
        }
        return null;
    }

    private static string ExtractMessage(JsonObject response, string fallback)
    {
        foreach (var candidate in new JsonNode?[] { response["message"], response["errors"]?["message"], response["data"]?["message"] })
        {
            if (candidate is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }
        return fallback;
    }

    private static JsonObject ExtractData(JsonObject response) => response["data"] as JsonObject ?? response;

    private static string? ReadString(JsonObject json, string key)
    {
        if (json[key] is not JsonValue value) return null;
        if (!value.TryGetValue<string>(out var text)) text = value.ToString();
        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool? ReadBool(JsonObject json, string key)
    {
        if (json[key] is JsonValue value && value.TryGetValue<bool>(out var parsed)) return parsed;
        return null;
    }

    private static long? ReadLong(JsonObject json, string key)
    {
        if (json[key] is JsonValue value && value.TryGetValue<long>(out var parsed)) return parsed;
        return null;
    }
}

public sealed class PaymentRequest
{
    public long Amount { get; set; }
    public string Description { get; set; } = "";
    public string CallbackUrl { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string? Mobile { get; set; }
    public string? Email { get; set; }
}

public sealed record PaymentCreated(string Authority, string PaymentUrl, int Code, string Message, string? ExpiresAt);
public sealed record PaymentVerified(int Code, string? RefId, string? CardPan, string Message);
public sealed record PaymentInquiry(string? Status, bool IsPaid, string Message, int? Code);
public sealed record PaymentReversal(bool Success, string Message, int? Code);
public sealed record FeeQuote(long? Amount, long? Fee, string? FeeType, bool IsPaidByCustomer, string Message, int? Code);

public class FinoPalValidationException : ArgumentException
{
    public FinoPalValidationException(string message) : base(message) { }
}

public class FinoPalResponseException : Exception
{
    public FinoPalResponseException(string message, int? code, JsonObject details) : base(message)
    {
        Code = code;
        Details = details;
    }

    public int? Code { get; }
    public JsonObject Details { get; }
}

public class FinoPalTransportException : Exception
{
    public FinoPalTransportException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
