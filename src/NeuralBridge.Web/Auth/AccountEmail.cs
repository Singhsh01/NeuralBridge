using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace NeuralBridge.Web.Auth;

public sealed record OutboxMessage(string To, string Subject, string Body, string? ActionUrl, DateTimeOffset SentAt);

/// <summary>Sends account emails (password reset). <see cref="IsAvailable"/> decides whether the UI offers the flow at all.</summary>
public interface IAccountEmailSender
{
    bool IsAvailable { get; }

    Task SendPasswordResetAsync(string toEmail, string displayName, string resetUrl, CancellationToken cancellationToken = default);
}

/// <summary>Development: keeps the last messages in memory and shows them at /dev/outbox. Never enabled in production by default.</summary>
public sealed class OutboxEmailSender : IAccountEmailSender
{
    private readonly List<OutboxMessage> _messages = [];
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxEmailSender> _logger;

    public OutboxEmailSender(TimeProvider time, ILogger<OutboxEmailSender> logger)
    {
        _time = time;
        _logger = logger;
    }

    public bool IsAvailable => true;

    public IReadOnlyList<OutboxMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.AsEnumerable().Reverse().ToList();
            }
        }
    }

    public Task SendPasswordResetAsync(string toEmail, string displayName, string resetUrl, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _messages.Add(new OutboxMessage(toEmail, "Reset your NeuralBridge password", PasswordResetText(displayName, resetUrl), resetUrl, _time.GetUtcNow()));
            if (_messages.Count > 50)
            {
                _messages.RemoveAt(0);
            }
        }

        // The link is a credential: it is shown in the dev outbox, never written to logs.
        _logger.LogInformation("Password-reset email queued in the development outbox (/dev/outbox)");
        return Task.CompletedTask;
    }

    internal static string PasswordResetText(string displayName, string resetUrl) =>
        $"Hi {displayName},\n\nSomeone asked to reset the password for your NeuralBridge account. If it was you, open this link within 2 hours:\n\n{resetUrl}\n\nIf it wasn't you, ignore this email; your password stays the same.\n";
}

public sealed class SmtpEmailSender : IAccountEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable => true;

    public async Task SendPasswordResetAsync(string toEmail, string displayName, string resetUrl, CancellationToken cancellationToken = default)
    {
        using var message = new MailMessage(_options.From ?? _options.Smtp.Username ?? "no-reply@localhost", toEmail)
        {
            Subject = "Reset your NeuralBridge password",
            Body = OutboxEmailSender.PasswordResetText(displayName, resetUrl),
        };
        using var client = new SmtpClient(_options.Smtp.Host, _options.Smtp.Port) { EnableSsl = _options.Smtp.EnableSsl };
        if (!string.IsNullOrEmpty(_options.Smtp.Username))
        {
            client.Credentials = new NetworkCredential(_options.Smtp.Username, _options.Smtp.Password);
        }

        try
        {
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException ex)
        {
            // Don't reveal delivery problems to the requester (anti-enumeration); operators see the log.
            _logger.LogError("Sending a password-reset email failed: {Status}", ex.StatusCode);
        }
    }
}

public sealed class DisabledEmailSender : IAccountEmailSender
{
    public bool IsAvailable => false;

    public Task SendPasswordResetAsync(string toEmail, string displayName, string resetUrl, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Email is not configured.");
}
