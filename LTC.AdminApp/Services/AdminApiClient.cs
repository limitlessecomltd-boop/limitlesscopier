using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LTC.AdminApp.Services;

/// <summary>
/// Thin HTTP client around the activation server's <c>/admin/*</c>
/// endpoints. The admin app uses this to mint, list, and revoke licenses
/// — replacing the old offline-only <see cref="LicenseMinter"/> flow.
///
/// The bearer token is loaded from <see cref="AdminSettings"/>; if absent
/// the operator is asked to enter it in the Settings tab. We never
/// hardcode it (compared to the public signing key, which is public-by-design,
/// the admin token is a real secret that should not ship in the binary).
///
/// All endpoint methods return a strongly-typed result with three terminal
/// states:
///   - Success         → server accepted the request
///   - ServerRejected  → server reachable but said no (bad input,
///                       unauthorized, key already revoked, etc.)
///   - NetworkFailure  → couldn't reach server (timeout, DNS, 5xx)
/// </summary>
public sealed class AdminApiClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private bool _disposed;

    public AdminApiClient(AdminSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiUrl))
            throw new ArgumentException("API URL is not configured.", nameof(settings));
        if (string.IsNullOrWhiteSpace(settings.BearerToken))
            throw new ArgumentException("Bearer token is not configured.", nameof(settings));

        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.ApiUrl.TrimEnd('/') + "/"),
            Timeout = RequestTimeout,
        };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", settings.BearerToken);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LimitlessAdmin/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// Issue a new license key. Returns the server-generated key on
    /// success, or an error result the UI can show verbatim.
    /// </summary>
    public async Task<AdminApiResult<IssueKeyResponse>> IssueKeyAsync(
        IssueKeyRequest req, CancellationToken ct = default)
    {
        return await PostAsync<IssueKeyRequest, IssueKeyResponse>(
            "admin/keys/issue", req, ct).ConfigureAwait(false);
    }

    private async Task<AdminApiResult<TResp>> PostAsync<TReq, TResp>(
        string path, TReq body, CancellationToken ct) where TResp : class
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync(path, body, JsonOpts, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return AdminApiResult<TResp>.NetworkFailure(
                $"Server timed out after {RequestTimeout.TotalSeconds:N0}s. " +
                "Check your internet connection.");
        }
        catch (HttpRequestException ex)
        {
            return AdminApiResult<TResp>.NetworkFailure(
                $"Could not reach {_http.BaseAddress}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return AdminApiResult<TResp>.NetworkFailure(
                $"Unexpected error contacting server: {ex.Message}");
        }

        // Server replied — interpret HTTP status code.
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return AdminApiResult<TResp>.ServerRejected(
                "Unauthorized — your admin bearer token is missing or wrong. " +
                "Check the Settings tab.");
        }
        if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Server uses { "error": "..." } shape for 400 responses.
            try
            {
                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var err = await JsonSerializer.DeserializeAsync<ErrorBody>(stream, JsonOpts, ct)
                    .ConfigureAwait(false);
                return AdminApiResult<TResp>.ServerRejected(
                    err?.Error ?? "Server rejected the request (bad request).");
            }
            catch
            {
                return AdminApiResult<TResp>.ServerRejected(
                    "Server rejected the request (bad request).");
            }
        }
        if (!resp.IsSuccessStatusCode)
        {
            return AdminApiResult<TResp>.NetworkFailure(
                $"Server returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        // 2xx — parse the typed response.
        try
        {
            var payload = await resp.Content.ReadFromJsonAsync<TResp>(JsonOpts, ct)
                .ConfigureAwait(false);
            if (payload is null)
            {
                return AdminApiResult<TResp>.NetworkFailure(
                    "Server returned an empty response body.");
            }
            return AdminApiResult<TResp>.Success(payload);
        }
        catch (Exception ex)
        {
            return AdminApiResult<TResp>.NetworkFailure(
                $"Could not parse server response: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    private sealed class ErrorBody
    {
        public string? Error { get; set; }
    }
}

// =====================================================================
// DTOs — kept here (not in their own files) because the AdminApp project
// doesn't need a separate Models folder for what's currently 3 small
// record types.
// =====================================================================

/// <summary>
/// Body for POST /admin/keys/issue. Fields match the server's
/// <c>IssueKeyRequest</c> exactly. <c>Days</c> null = no expiry (lifetime).
/// </summary>
public sealed class IssueKeyRequest
{
    public string Email { get; set; } = "";
    public string Plan { get; set; } = "Lifetime";
    public int? Days { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Server response from POST /admin/keys/issue. <c>LicenseKey</c> is
/// the customer-facing string we display + email to the customer.
/// </summary>
public sealed class IssueKeyResponse
{
    public bool Ok { get; set; }
    public string LicenseKey { get; set; } = "";
    public string Email { get; set; } = "";
    public string Plan { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Three-state result for any admin API call. Either Success with the
/// typed body, or one of the two error kinds with a human-readable
/// message the UI can show as-is.
/// </summary>
public sealed class AdminApiResult<T> where T : class
{
    public AdminApiResultKind Kind { get; private init; }
    public T? Body { get; private init; }
    public string Message { get; private init; } = "";

    public bool IsSuccess => Kind == AdminApiResultKind.Success;

    public static AdminApiResult<T> Success(T body) =>
        new() { Kind = AdminApiResultKind.Success, Body = body, Message = "OK" };

    public static AdminApiResult<T> ServerRejected(string message) =>
        new() { Kind = AdminApiResultKind.ServerRejected, Message = message };

    public static AdminApiResult<T> NetworkFailure(string message) =>
        new() { Kind = AdminApiResultKind.NetworkFailure, Message = message };
}

public enum AdminApiResultKind
{
    Success,
    ServerRejected,
    NetworkFailure,
}
