using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace LTC.Server.Services;

/// <summary>
/// Resend configuration (env vars: Resend__ApiKey, Resend__FromEmail).
/// We use admin@limitlesscopier.com for license keys (transactional)
/// and support@limitlesscopier.com for any inbound replies.
/// </summary>
public class ResendOptions
{
    public string ApiKey { get; set; } = "";
    public string FromEmail { get; set; } = "admin@limitlesscopier.com";
    public string FromName { get; set; } = "Limitless Trade Copier";
    public string SupportEmail { get; set; } = "support@limitlesscopier.com";
    public string BaseUrl { get; set; } = "https://api.resend.com";
}

/// <summary>
/// Thin Resend API client - just emails.send. We hand-roll the JSON
/// rather than pulling in their SDK because we use exactly one endpoint
/// and the SDK adds dependency surface for no real gain.
/// </summary>
public class EmailService
{
    private readonly HttpClient _http;
    private readonly ResendOptions _opts;
    private readonly ILogger<EmailService> _log;

    public EmailService(
        HttpClient http,
        IOptions<ResendOptions> opts,
        ILogger<EmailService> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;

        if (string.IsNullOrWhiteSpace(_opts.ApiKey))
            _log.LogWarning("Resend:ApiKey not configured - emails will fail to send");

        _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _opts.ApiKey);
    }

    /// <summary>
    /// Send the license-key email to a customer who just completed payment.
    /// </summary>
    public async Task<bool> SendLicenseEmailAsync(
        string toEmail,
        string licenseKey,
        string plan,
        decimal amountUsd,
        CancellationToken ct = default)
    {
        var subject = "Your Limitless Trade Copier license";
        var planLabel = plan switch
        {
            "1month"   => "1 Month",
            "3months"  => "3 Months",
            "yearly"   => "Yearly (12 Months)",
            _ => plan
        };

        // Plain-text fallback for clients that can't render HTML
        var text = $"""
Hi,

Thank you for purchasing Limitless Trade Copier.

Your license key:
    {licenseKey}

Plan: {planLabel}
Amount: ${amountUsd:F2} USD

To activate:
  1. Download and install Limitless Trade Copier from your account dashboard.
  2. On first launch, the License dialog will appear.
  3. Paste the key above and click "Activate".

The key binds to your machine on first use. If you need to move to a different
PC, email support@limitlesscopier.com and we'll release the binding within 24 hours.

Need help? Reply to this email or write to support@limitlesscopier.com.

— The Limitless Team
""";

        // Simple HTML version - inline styles for max email client compatibility
        var html = $$"""
<!DOCTYPE html>
<html>
<head><meta charset="utf-8"></head>
<body style="font-family:-apple-system,Segoe UI,Helvetica,Arial,sans-serif;line-height:1.5;color:#111;background:#f5f5f7;margin:0;padding:0;">
<table cellpadding="0" cellspacing="0" border="0" width="100%" style="padding:32px 16px;">
<tr><td align="center">
<table cellpadding="0" cellspacing="0" border="0" width="540" style="background:#ffffff;border-radius:12px;overflow:hidden;border:1px solid #e6e6ea;">
  <tr><td style="padding:32px 32px 16px 32px;">
    <div style="font-size:22px;font-weight:600;letter-spacing:-0.01em;">Limitless Trade Copier</div>
    <div style="font-size:13px;color:#7a7a82;margin-top:4px;">Order confirmation</div>
  </td></tr>
  <tr><td style="padding:0 32px 8px 32px;">
    <p style="font-size:15px;margin:0 0 16px 0;">Thank you for purchasing <strong>Limitless Trade Copier</strong>. Your license is ready.</p>
  </td></tr>
  <tr><td style="padding:0 32px 24px 32px;">
    <div style="background:#0a0a0c;color:#ff9f1c;border-radius:8px;padding:18px 20px;font-family:ui-monospace,Menlo,Consolas,monospace;font-size:18px;font-weight:600;letter-spacing:0.05em;text-align:center;">
      {{licenseKey}}
    </div>
    <p style="font-size:12px;color:#7a7a82;margin:8px 0 0 0;text-align:center;">Your license key</p>
  </td></tr>
  <tr><td style="padding:0 32px 24px 32px;">
    <table cellpadding="0" cellspacing="0" border="0" width="100%" style="font-size:14px;">
      <tr><td style="padding:6px 0;color:#7a7a82;">Plan</td><td align="right" style="padding:6px 0;font-weight:500;">{{planLabel}}</td></tr>
      <tr><td style="padding:6px 0;color:#7a7a82;">Amount</td><td align="right" style="padding:6px 0;font-weight:500;">${{amountUsd:F2}} USD</td></tr>
      <tr><td style="padding:6px 0;color:#7a7a82;">Payment method</td><td align="right" style="padding:6px 0;font-weight:500;">USDT (TRC20)</td></tr>
    </table>
  </td></tr>
  <tr><td style="padding:0 32px 24px 32px;">
    <div style="font-size:14px;font-weight:600;margin-bottom:8px;">How to activate</div>
    <ol style="font-size:14px;margin:0;padding-left:20px;">
      <li style="margin:4px 0;">Download Limitless Trade Copier from <a href="https://limitlesscopier.com" style="color:#0070bb;">limitlesscopier.com</a>.</li>
      <li style="margin:4px 0;">On first launch, paste the license key above.</li>
      <li style="margin:4px 0;">The key binds to your PC on first use.</li>
    </ol>
  </td></tr>
  <tr><td style="padding:0 32px 32px 32px;border-top:1px solid #e6e6ea;">
    <p style="font-size:12px;color:#7a7a82;margin:24px 0 0 0;">
      Need help or want to move your license to another PC? Email
      <a href="mailto:support@limitlesscopier.com" style="color:#0070bb;">support@limitlesscopier.com</a>.
    </p>
  </td></tr>
</table>
<div style="font-size:11px;color:#a0a0a8;margin-top:16px;">© Limitless Trade Copier</div>
</td></tr>
</table>
</body>
</html>
""";

        return await SendAsync(toEmail, subject, html, text, ct).ConfigureAwait(false);
    }

    private async Task<bool> SendAsync(string to, string subject, string html, string text, CancellationToken ct)
    {
        var from = $"{_opts.FromName} <{_opts.FromEmail}>";
        var body = new
        {
            from,
            to = new[] { to },
            subject,
            html,
            text,
            reply_to = _opts.SupportEmail,
        };

        try
        {
            using var resp = await _http.PostAsJsonAsync("emails", body, ct).ConfigureAwait(false);
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("Resend send failed: {Status} {Body}", resp.StatusCode, raw);
                return false;
            }
            _log.LogInformation("Sent license email to {Email} (Resend response: {Response})", to, raw);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Resend send threw exception");
            return false;
        }
    }
}
