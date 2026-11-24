using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Workflow.Contracts;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Approve and reject from Teams (spec 015 US5 scenario 2, spec 016): the same decision as the task inbox, through
/// <see cref="Tasks.DecideAsync"/> as the person who pressed the button, so its candidate, delegation, authorization,
/// closed task and reason rules all apply. A rejection needs a reason, as in the inbox. The decision route has no
/// step-up today, so nothing here asks for one.
/// </summary>
public sealed class TaskQuickActions(IServiceProvider services) : INotificationActionHandler
{
    public const string Name = "services.task";

    public string Handler => Name;

    public string? LabelKey(string action) => action switch
    {
        TaskOutcomes.Approve => "services.quick.approve",
        TaskOutcomes.Reject => "services.quick.reject",
        _ => null,
    };

    public bool NeedsReason(string action) => action == TaskOutcomes.Reject;

    public bool RequiresStepUp(string action) => false;

    public async Task<QuickActionOutcome> RunAsync(string target, string action, string? reason, CancellationToken ct = default)
    {
        if (!Guid.TryParse(target, out var taskId) || LabelKey(action) is null)
        {
            return QuickActionOutcome.Closed;
        }
        // Resolved when pressed: the task inbox itself sends notifications, so it cannot be built with the sender.
        await services.GetRequiredService<Tasks>().DecideAsync(taskId, new TaskDecision(action, reason), ct);
        return QuickActionOutcome.Done;
    }

    /// <summary>The quick actions offered with a "waiting for your approval" notification of a task.</summary>
    public static NotificationActions For(Guid taskId) => new(Name, taskId.ToString("D"), [TaskOutcomes.Approve, TaskOutcomes.Reject]);
}
