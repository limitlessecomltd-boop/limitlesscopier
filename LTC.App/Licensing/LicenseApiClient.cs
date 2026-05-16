using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LTC.App.Licensing;

/// <summary>
/// Thin HTTP client wrapping the Limitless activation server at
/// <c>https://api.limitlesscopier.com</c>. Speaks the three customer
/// endpoints: <c>/activate</c>, <c>/heartbeat</c>, <c>/deactivate</c>.
///
/// All three endpoints return HTTP 200 with a JSON body shaped like
/// <see cref="ActivationApiResponse"/>. Errors are encoded as
/// <c>Ok=false</c> plus a stable <c>ErrorCode</c> string the UI can switch
/// on. We rely on that — we don't treat 4xx as a network failure, but we
/// DO treat genuine HTTP failures (timeout, DNS error, 5xx) as
/// <see cref="LicenseApiResult.NetworkFailure"/> so the caller can fall
/// back to the offline-grace cache instead of nagging the user.
///
/// HTTP timeout is intentionally short (10 seconds). Activation usually
/// finishes in &lt;1 s; if it takes longer, something's wrong with either
/// the server or the customer's internet, and we'd rather show the
/// offline-grace banner than freeze the app.
/// </summary>
public sealed class LicenseApiClient : IDisposable
{
    /// <summary>Production server. Hardcoded — same as the public key.
    /// Overridable via env var <c>LIMITLESS_API_URL</c> for dev testing.</summary>
    public const string DefaultBaseUrl = "https://api.limitlesscopier.com";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private bool _disposed;

    public LicenseApiClient(string? baseUrlOverride = null)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(baseUrlOverride)
            ? baseUrlOverride
            : Environment.GetEnvironmentVariable("LIMITLESS_API_URL") ?? DefaultBaseUrl;

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = RequestTimeout,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LimitlessCopier/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>First-time activation. Returns the signed token bytes on
    /// success — caller is responsible for writing them to
    /// <c>activation.dat</c> via <c>ActivationTokenStore</c>.</summary>
    public Task<LicenseApiResult> ActivateAsync(
        string licenseKey, string fingerprint, CancellationToken ct = default)
        => PostAsync("activate", licenseKey, fingerprint, ct);

    /// <summary>Refresh an existing activation. Returns a freshly-signed
    /// token with an extended <c>HeartbeatDueUtc</c>. Caller stores it.</summary>
    public Task<LicenseApiResult> HeartbeatAsync(
        string licenseKey, string fingerprint, CancellationToken ct = default)
        => PostAsync("heartbeat", licenseKey, fingerprint, ct);

    /// <summary>Release this machine's claim on the license so it can be
    /// activated elsewhere. Server requires fingerprint match (so a leaked
    /// key alone can't steal another customer's activation slot).</summary>
    public Task<LicenseApiResult> DeactivateAsync(
        string licenseKey, string fingerprint, CancellationToken ct = default)
        => PostAsync("deactivate", licenseKey, fingerprint, ct);

    private async Task<LicenseApiResult> PostAsync(
        string path, string licenseKey, string fingerprint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return LicenseApiResult.LocalError("License key is empty.");
        if (string.IsNullOrWhiteSpace(fingerprint))
            return LicenseApiResult.LocalError("Machine fingerprint is empty.");

        var body = new { LicenseKey = licenseKey.Trim(), Fingerprint = fingerprint.Trim() };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync(path, body, JsonOpts, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled; bubble up so the caller sees the cancellation
            // semantics it asked for rather than masking it as "network failure".
            throw;
        }
        catch (TaskCanceledException)
        {
            // HttpClient's own timeout — surface as network failure so caller
            // can hit the offline-grace path.
            return LicenseApiResult.NetworkFailure(
                $"Could not reach {_http.BaseAddress} (timed out after {RequestTimeout.TotalSeconds:N0}s). Check your internet connection.");
        }
        catch (HttpRequestException ex)
        {
            return LicenseApiResult.NetworkFailure(
                $"Could not reach {_http.BaseAddress}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return LicenseApiResult.NetworkFailure(
                $"Unexpected error contacting the activation server: {ex.Message}");
        }

        // Even 5xx is a network failure for our purposes — server is up but
        // misbehaving; caller falls back to cached token if the cache is
        // still within the offline-grace window.
        if (!resp.IsSuccessStatusCode)
        {
            return LicenseApiResult.NetworkFailure(
                $"Activation server returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        ActivationApiResponse? payload;
        try
        {
            payload = await resp.Content.ReadFromJsonAsync<ActivationApiResponse>(JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return LicenseApiResult.NetworkFailure(
                $"Could not parse server response: {ex.Message}");
        }

        if (payload is null)
            return LicenseApiResult.NetworkFailure("Server returned an empty response.");

        if (payload.Ok)
        {
            byte[]? tokenBytes = null;
            if (!string.IsNullOrWhiteSpace(payload.TokenBase64))
            {
                try { tokenBytes = Convert.FromBase64String(payload.TokenBase64); }
                catch (FormatException)
                {
                    return LicenseApiResult.NetworkFailure(
                        "Server returned a malformed token. Try again, or contact support if this persists.");
                }
            }
            return LicenseApiResult.Success(tokenBytes,
                payload.Message ?? "Activation successful.");
        }

        return LicenseApiResult.ServerRejected(
            payload.ErrorCode ?? "server_error",
            payload.Message ?? "The server rejected this request.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    /// <summary>
    /// Wire shape of an <see cref="ActivationApiResponse"/>. Matches the
    /// server's <c>ActivationResponse</c> DTO field-for-field. We keep
    /// our own copy here rather than referencing LTC.Server because
    /// LTC.App should never depend on the server project.
    /// </summary>
    private sealed class ActivationApiResponse
    {
        public bool Ok { get; set; }
        public string? ErrorCode { get; set; }
        public string? Message { get; set; }
        public string? TokenBase64 { get; set; }
    }
}

/// <summary>
/// Result of a call to the activation server. Three terminal states:
///
///   1. <see cref="ResultKind.Success"/>      — server accepted; bytes are
///      a signed activation token ready to persist.
///   2. <see cref="ResultKind.ServerRejected"/> — server reachable but said
///      no. ErrorCode is one of: <c>key_not_found</c>, <c>revoked</c>,
///      <c>expired</c>, <c>fingerprint_mismatch</c>,
///      <c>already_active_elsewhere</c>, <c>not_activated</c>,
///      <c>bad_fingerprint</c>, <c>rate_limited</c>, <c>server_error</c>.
///   3. <see cref="ResultKind.NetworkFailure"/> — couldn't reach server.
///      Caller should consult the offline-grace cache.
///   4. <see cref="ResultKind.LocalError"/>   — input was invalid; no
///      request was sent.
/// </summary>
public sealed class LicenseApiResult
{
    public ResultKind Kind { get; private init; }
    public byte[]? TokenBytes { get; private init; }
    public string? ErrorCode { get; private init; }
    public string Message { get; private init; } = "";

    public bool IsSuccess => Kind == ResultKind.Success;
    public bool IsNetworkFailure => Kind == ResultKind.NetworkFailure;
    public bool IsServerRejection => Kind == ResultKind.ServerRejected;

    public static LicenseApiResult Success(byte[]? tokenBytes, string message) =>
        new() { Kind = ResultKind.Success, TokenBytes = tokenBytes, Message = message };

    public static LicenseApiResult ServerRejected(string errorCode, string message) =>
        new() { Kind = ResultKind.ServerRejected, ErrorCode = errorCode, Message = message };

    public static LicenseApiResult NetworkFailure(string message) =>
        new() { Kind = ResultKind.NetworkFailure, Message = message };

    public static LicenseApiResult LocalError(string message) =>
        new() { Kind = ResultKind.LocalError, Message = message };
}

public enum ResultKind
{
    Success,
    ServerRejected,
    NetworkFailure,
    LocalError,
}
