using System.Net;
using System.Net.Mail;
using PayLink.Platform;

namespace PayLink.Infrastructure;

public sealed class SmtpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool Ssl { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "";
}

public interface IEmailSender
{
    /// <summary>Sends a plain-text email. False (and nothing thrown) when email isn't set up or the server refuses it.</summary>
    Task<bool> SendAsync(IReadOnlyCollection<string> to, string subject, string body, CancellationToken ct = default);
}

/// <summary>Email through the SMTP server set in Admin → Platform settings (stored encrypted). Optional: without it, alerts go by webhook only.</summary>
public sealed class SmtpEmailSender(PlatformSettings settings, SecretProtector secrets, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public const string SettingKey = "smtp";

    public async Task<SmtpSettings?> GetAsync() => secrets.UnprotectJson<SmtpSettings>(await settings.GetAsync(SettingKey));

    public async Task SaveAsync(SmtpSettings? smtp) => await settings.SetAsync(SettingKey, smtp is null ? null : secrets.ProtectJson(smtp));

    public async Task<bool> SendAsync(IReadOnlyCollection<string> to, string subject, string body, CancellationToken ct = default)
    {
        if (to.Count == 0 || await GetAsync() is not { Host.Length: > 0 } smtp) return false;
        try
        {
            using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.Ssl, DeliveryMethod = SmtpDeliveryMethod.Network };
            if (!string.IsNullOrEmpty(smtp.User)) client.Credentials = new NetworkCredential(smtp.User, smtp.Password);
            using var message = new MailMessage { From = new MailAddress(smtp.From), Subject = subject, Body = body, IsBodyHtml = false };
            foreach (var address in to) message.To.Add(address);
            await client.SendMailAsync(message, ct);
            return true;
        }
        catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Email to {Count} recipient(s) failed", to.Count);
            return false;
        }
    }
}
