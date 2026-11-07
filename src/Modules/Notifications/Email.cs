using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Notifications;

public sealed record EmailMessage(string To, string FromAddress, string FromName, string Subject, string Text, string Html, string Locale);

public enum EmailOutcome
{
    Sent,

    /// <summary>The server refused the recipient permanently; email to the address is paused (FR-010).</summary>
    HardBounce,

    /// <summary>Worth retrying: the relay is down, busy or refused temporarily.</summary>
    Transient,
}

public sealed record EmailResult(EmailOutcome Outcome, string? Error = null);

/// <summary>The email adapter port (spec 015 FR-004). The relay is the tenant's when set, otherwise the platform's.</summary>
public interface IEmailSender
{
    Task<EmailResult> SendAsync(EmailMessage message, RelaySettings? tenantRelay, Guid tenantId, CancellationToken ct);
}

/// <summary>Platform email configuration under <c>Notifications:Email</c>.</summary>
public sealed class EmailOptions
{
    public const string Section = "Notifications:Email";

    /// <summary><c>Smtp</c> sends; <c>Capture</c> keeps mail for the development mailbox and is refused in Production.</summary>
    public string Mode { get; set; } = "Smtp";

    public string FromAddress { get; set; } = "no-reply@teambeats.localhost";

    public string? SmtpHost { get; set; }

    public int SmtpPort { get; set; } = 587;

    public string? SmtpUsername { get; set; }

    public string? SmtpPassword { get; set; }

    public bool SmtpStartTls { get; set; } = true;

    public static bool IsCapture(IConfiguration configuration) =>
        string.Equals(configuration[$"{Section}:Mode"], "Capture", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// SMTP adapter on the base library's client, which covers STARTTLS and authentication, so no mail library is added.
/// Recipient refusals with a 5xx code are hard bounces; everything else is retried.
/// </summary>
public sealed class SmtpEmailSender(Microsoft.Extensions.Options.IOptions<EmailOptions> options, ISecretStore secrets) : IEmailSender
{
    public async Task<EmailResult> SendAsync(EmailMessage message, RelaySettings? tenantRelay, Guid tenantId, CancellationToken ct)
    {
        var o = options.Value;
        string? host, username, password;
        int port;
        bool startTls;
        if (tenantRelay is not null)
        {
            (host, port, username, startTls) = (tenantRelay.Host, tenantRelay.Port, tenantRelay.Username, tenantRelay.StartTls);
            password = tenantRelay.SecretReference is { } reference ? await secrets.GetTenantSecretAsync(tenantId, reference, ct) : null;
            if (tenantRelay.SecretReference is not null && password is null)
            {
                return new EmailResult(EmailOutcome.Transient, "relay_secret_missing");
            }
        }
        else
        {
            (host, port, username, password, startTls) = (o.SmtpHost, o.SmtpPort, o.SmtpUsername, o.SmtpPassword, o.SmtpStartTls);
        }
        if (string.IsNullOrWhiteSpace(host))
        {
            return new EmailResult(EmailOutcome.Transient, "relay_not_configured");
        }

        using var client = new SmtpClient(host, port) { EnableSsl = startTls, DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = 30_000 };
        if (username is not null)
        {
            client.Credentials = new NetworkCredential(username, password);
        }
        using var mail = new MailMessage
        {
            From = new MailAddress(o.FromAddress, message.FromName),
            Subject = message.Subject,
            Body = message.Text,
            BodyEncoding = System.Text.Encoding.UTF8,
            SubjectEncoding = System.Text.Encoding.UTF8,
        };
        mail.To.Add(new MailAddress(message.To));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Html, System.Text.Encoding.UTF8, "text/html"));
        mail.Headers.Add("Content-Language", message.Locale);
        try
        {
            await client.SendMailAsync(mail, ct);
            return new EmailResult(EmailOutcome.Sent);
        }
        catch (SmtpFailedRecipientException ex) when ((int)ex.StatusCode >= 500 && ex.StatusCode != SmtpStatusCode.MailboxBusy)
        {
            return new EmailResult(EmailOutcome.HardBounce, $"smtp_{(int)ex.StatusCode}");
        }
        catch (SmtpException ex)
        {
            return new EmailResult(EmailOutcome.Transient, $"smtp_{(int)ex.StatusCode}");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return new EmailResult(EmailOutcome.Transient, "smtp_unreachable");
        }
    }
}

/// <summary>
/// Development, test and staging sender: keeps each message in <c>notifications.captured_mail</c> for the development
/// mailbox instead of sending it, so no external mail service is needed. Refused in Production.
/// </summary>
public sealed class CaptureEmailSender(IDbSession session, IClock clock) : IEmailSender
{
    public async Task<EmailResult> SendAsync(EmailMessage message, RelaySettings? tenantRelay, Guid tenantId, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            insert into notifications.captured_mail (tenant_id, id, to_address, from_name, subject, text_body, html_body, captured_at)
            values (platform.current_tenant_id(), @id, @to, @from, @subject, @text, @html, @now)
            """,
            new { id = Ids.New(), to = message.To, from = message.FromName, subject = message.Subject, text = message.Text, html = message.Html, now = clock.UtcNow }, ct);
        return new EmailResult(EmailOutcome.Sent);
    }
}

public sealed record CapturedMail(Guid Id, string To, string FromName, string Subject, string Text, DateTimeOffset CapturedAt);

/// <summary>Reads the development mailbox of the current tenant.</summary>
public sealed class CapturedMailbox(IDbSession session)
{
    public Task<List<CapturedMail>> ListAsync(int limit, CancellationToken ct) =>
        session.QueryAsync(
            "select id, to_address, from_name, subject, text_body, captured_at from notifications.captured_mail order by captured_at desc, id desc limit @limit",
            new { limit = Math.Clamp(limit, 1, 200) },
            r => new CapturedMail(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)),
            ct);
}
