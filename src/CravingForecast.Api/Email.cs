using System.Net;
using System.Text.Json;
using Azure;
using Azure.Communication.Email;

namespace CravingForecast.Api;

/// <summary>An email to send. <c>ReplyTo</c>, when set, is where a reply goes instead of the sender.</summary>
public sealed record OutgoingEmail(string To, string Subject, string Text, string Html, string? ReplyTo = null);

public interface IEmailSender
{
    Task SendAsync(OutgoingEmail email, CancellationToken ct = default);
}

/// <summary>
/// Sends through Azure Communication Services Email. Configured with Email:ConnectionString (an Azure app
/// setting, never in the repo) and Email:Sender, the address Azure gives you, like DoNotReply@xxxx.azurecomm.net.
/// </summary>
public sealed class AzureEmailSender(EmailClient client, string sender) : IEmailSender
{
    public async Task SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        var message = new EmailMessage(sender, email.To, new EmailContent(email.Subject) { PlainText = email.Text, Html = email.Html });
        if (email.ReplyTo is { } replyTo) message.ReplyTo.Add(new EmailAddress(replyTo));
        await client.SendAsync(WaitUntil.Started, message, ct);
    }
}

/// <summary>Writes each email to a folder as JSON instead of sending it. Used by the browser walk-through.</summary>
public sealed class FolderEmailSender(string folder) : IEmailSender
{
    public async Task SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(email), ct);
    }
}

/// <summary>With no email service configured, nothing is sent; a warning says so (without the link).</summary>
public sealed class NotConfiguredEmailSender(ILogger<NotConfiguredEmailSender> log) : IEmailSender
{
    public Task SendAsync(OutgoingEmail email, CancellationToken ct = default)
    {
        log.LogWarning("Email isn't set up (Email:ConnectionString), so \"{Subject}\" wasn't sent.", email.Subject);
        return Task.CompletedTask;
    }
}

public static class Emails
{
    /// <summary>A contact-form message, sent to the owner's own inbox; replying answers the person who wrote.</summary>
    public static OutgoingEmail ContactMessage(string to, string from, string message, DateTimeOffset at)
    {
        var html = WebUtility.HtmlEncode(message).Replace("\n", "<br>");
        return new OutgoingEmail(to, $"Salus contact form: message from {from}",
            $"""
             {from} wrote through the Salus contact form on {at:yyyy-MM-dd HH:mm} UTC:

             {message}

             Reply to this email to answer them.
             """,
            $"""
             <div style="font-family:system-ui,-apple-system,'Segoe UI',sans-serif;color:#1E2A24;line-height:1.5">
               <p style="margin:0 0 12px"><b>{WebUtility.HtmlEncode(from)}</b> wrote through the Salus contact form on {at:yyyy-MM-dd HH:mm} UTC:</p>
               <blockquote style="margin:0 0 12px;padding:12px 16px;background:#F4EDE2;border-radius:12px">{html}</blockquote>
               <p style="margin:0;color:#5E5448;font-size:13px">Reply to this email to answer them.</p>
             </div>
             """,
            ReplyTo: from);
    }

    public static OutgoingEmail PasswordReset(string to, string link)
    {
        var safe = WebUtility.HtmlEncode(link);
        return new OutgoingEmail(to, "Reset your Salus password",
            $"""
             Someone, hopefully you, asked to reset the password for your Salus account.

             Choose a new password here. The link works once, for the next hour:
             {link}

             If you didn't ask for this, you can ignore this email. Your password stays the same.

             Salus
             """,
            $"""
             <div style="font-family:system-ui,-apple-system,'Segoe UI',sans-serif;background:#F4EDE2;padding:32px 16px">
               <div style="max-width:480px;margin:0 auto;background:#FFFCF7;border-radius:20px;padding:28px;color:#1E2A24">
                 <p style="margin:0 0 4px;font-size:20px;font-weight:800">Salus</p>
                 <h1 style="margin:0 0 12px;font-size:22px">Reset your password</h1>
                 <p style="margin:0 0 20px;line-height:1.5;color:#4A4238">Someone, hopefully you, asked to reset the password for your Salus account. The button works once, for the next hour.</p>
                 <a href="{safe}" style="display:inline-block;background:#3F6B4E;color:#fff;text-decoration:none;font-weight:700;padding:14px 22px;border-radius:14px">Choose a new password</a>
                 <p style="margin:20px 0 0;font-size:13px;line-height:1.5;color:#5E5448">If you didn't ask for this, you can ignore this email. Your password stays the same.</p>
               </div>
             </div>
             """);
    }
}
