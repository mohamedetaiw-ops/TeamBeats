using System.Collections.Concurrent;
using TeamBeats.Modules.Notifications;

namespace TeamBeats.Testing;

/// <summary>Records email instead of sending it. The outcome per address can be set to simulate relay failures and bounces.</summary>
public sealed class TestEmailSender : IEmailSender
{
    private readonly ConcurrentDictionary<string, EmailOutcome> _outcomes = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<SentEmail> Sent { get; } = new();

    public sealed record SentEmail(Guid TenantId, EmailMessage Message, RelaySettings? Relay, EmailOutcome Outcome);

    public void Answer(string address, EmailOutcome outcome) => _outcomes[address] = outcome;

    public IReadOnlyList<SentEmail> To(string address) => Sent.Where(s => string.Equals(s.Message.To, address, StringComparison.OrdinalIgnoreCase)).ToList();

    public Task<EmailResult> SendAsync(EmailMessage message, RelaySettings? tenantRelay, Guid tenantId, CancellationToken ct)
    {
        var outcome = _outcomes.GetValueOrDefault(message.To, EmailOutcome.Sent);
        Sent.Enqueue(new SentEmail(tenantId, message, tenantRelay, outcome));
        return Task.FromResult(new EmailResult(outcome, outcome == EmailOutcome.Sent ? null : outcome == EmailOutcome.HardBounce ? "550 5.1.1 no such user" : "421 try later"));
    }
}
