using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace LTC.Server.Services;

/// <summary>
/// Sends operational alerts to the operator (you) when the server detects
/// suspicious activity. Uses plain SMTP — no external services needed.
///
/// Configuration in appsettings.json:
///   "Smtp": {
///     "Host": "smtp.gmail.com",
///     "Port": 587,
///     "User": "alerts@yourdomain.com",
///     "Password": "app-specific-password",   // not your real password
///     "From": "alerts@yourdomain.com",
///     "To":   "you@yourdomain.com"
///   }
///
/// If SMTP is not configured (Host empty), this service silently no-ops.
/// That way the server runs fine in dev without any SMTP setup, and you
/// add it on the production VPS when ready.
///
/// Recommended: use Gmail with an App Password (not your real password),
/// or transactional email like Mailgun/Postmark/Sendgrid. Avoid raw
/// SMTP on the server itself — IPs of small VPS providers get
/// blacklisted easily.
/// </summary>
public sealed class EmailAlertService
{
    private readonly EmailConfig _cfg;
    private readonly ILogger<EmailAlertService> _log;

    public EmailAlertService(IConfiguration config, ILogger<EmailAlertService> log)
    {
        _log = log;
        _cfg = new EmailConfig
        {
            Host     = config["Smtp:Host"] ?? "",
            Port     = int.TryParse(config["Smtp:Port"], out var p) ? p : 587,
            User     = config["Smtp:User"] ?? "",
            Password = config["Smtp:Password"] ?? "",
            From     = config["Smtp:From"] ?? "",
            To       = config["Smtp:To"] ?? "",
        };

        if (string.IsNullOrWhiteSpace(_cfg.Host))
        {
            _log.LogWarning("SMTP not configured — email alerts disabled. " +
                            "Set Smtp:Host etc. in appsettings.json to enable.");
        }
        else
        {
            _log.LogInformation("Email alerts will be sent to {To} via {Host}:{Port}",
                _cfg.To, _cfg.Host, _cfg.Port);
        }
    }

    /// <summary>Send an alert. Fire-and-forget; failures are logged
    /// but don't crash the calling request. Async only as far as the
    /// SMTP send.</summary>
    public Task SendAsync(string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(_cfg.Host)) return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                using var client = new SmtpClient(_cfg.Host, _cfg.Port)
                {
                    Credentials = new NetworkCredential(_cfg.User, _cfg.Password),
                    EnableSsl = true,
                };
                using var msg = new MailMessage(_cfg.From, _cfg.To, subject, body);
                client.Send(msg);
                _log.LogInformation("Alert sent: {Subject}", subject);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to send alert email: {Subject}", subject);
            }
        });
    }

    private sealed class EmailConfig
    {
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }
}
