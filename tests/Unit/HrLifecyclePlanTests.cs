using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Hr;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 019 FR-005 to FR-008: how one HR record's new read becomes lifecycle changes.</summary>
public sealed class HrLifecyclePlanTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private static readonly HrPlanClock At = new(Now, Today, d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        start => new DateTimeOffset(start.AddDays(-7).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));

    private static Dictionary<string, string> Rec(string status = "active", string unit = "FIN", string title = "Accountant", string? hired = "2024-03-01",
        string? left = null, string? since = null)
    {
        var d = new Dictionary<string, string>
        {
            [HrFields.RecordId] = "R1", [HrFields.Status] = status, [HrFields.OrgUnitCode] = unit, [HrFields.JobTitle] = title, [HrFields.ManagerEmployeeNumber] = "E-9",
        };
        if (hired is not null) d[HrFields.HireDate] = hired;
        if (left is not null) d[HrFields.TerminationDate] = left;
        if (since is not null) d[HrFields.AssignmentDate] = since;
        return d;
    }

    private static HrRecordPlan Plan(Dictionary<string, string>? before, Dictionary<string, string> next, bool baseline = false, bool left = false,
        params OpenHrChange[] open) => HrLifecycle.Plan(before, next, baseline, left, open, At);

    [Fact]
    public void The_baseline_plans_nothing_but_notice_periods()
    {
        Assert.Empty(Plan(null, Rec(), baseline: true).Add);
        Assert.Empty(Plan(null, Rec("terminated", left: "2025-01-31"), baseline: true).Add);
        var notice = Plan(null, Rec(left: "2026-11-30"), baseline: true).Add.Single();
        Assert.Equal((HrChangeKinds.Termination, new DateOnly(2026, 11, 30), "false"), (notice.Kind, notice.EffectiveDate, notice.To["immediate"]));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), notice.DueAt);
    }

    [Fact]
    public void A_future_assignment_keeps_the_applied_one_until_its_date_and_a_reverted_one_is_cancelled()
    {
        var plan = Plan(Rec(), Rec(unit: "PROC", title: "Buyer", since: "2027-01-01"));
        var change = plan.Add.Single();
        Assert.Equal((HrChangeKinds.Assignment, new DateOnly(2027, 1, 1), "PROC", "FIN"), (change.Kind, change.EffectiveDate, change.To[HrFields.OrgUnitCode], change.From[HrFields.OrgUnitCode]));
        Assert.Equal(("FIN", "Accountant"), (plan.Data[HrFields.OrgUnitCode], plan.Data[HrFields.JobTitle]));

        var open = new OpenHrChange(Guid.NewGuid(), HrChangeKinds.Assignment, HrChangeStatuses.Pending, new DateOnly(2027, 1, 1), change.To);
        Assert.Empty(Plan(Rec(), Rec(unit: "PROC", title: "Buyer", since: "2027-01-01"), open: open).Add);
        Assert.Equal((open.Id, "reverted"), Plan(Rec(), Rec(), open: open).Cancel.Single());
        var today = Plan(Rec(), Rec(unit: "PROC")).Add.Single();
        Assert.Equal((Today, Now), (today.EffectiveDate, today.DueAt));
    }

    [Fact]
    public void Terminations_are_immediate_once_the_last_day_has_passed_and_are_cancelled_when_rescinded()
    {
        var past = Plan(Rec(), Rec("terminated", left: "2026-11-01")).Add.Single();
        Assert.Equal(("true", Now), (past.To["immediate"], past.DueAt));
        var noDate = Plan(Rec(), Rec("terminated")).Add.Single();
        Assert.Equal((Today, "true"), (noDate.EffectiveDate, noDate.To["immediate"]));
        var open = new OpenHrChange(Guid.NewGuid(), HrChangeKinds.Termination, HrChangeStatuses.Pending, new DateOnly(2026, 11, 30), new Dictionary<string, string?>());
        Assert.Empty(Plan(Rec(left: "2026-11-30"), Rec(left: "2026-11-30"), open: open).Add);
        Assert.Equal((open.Id, "rescinded"), Plan(Rec(left: "2026-11-30"), Rec(), open: open).Cancel.Single());
        var moved = Plan(Rec(left: "2026-11-30"), Rec(left: "2026-12-15"), open: open);
        Assert.Equal(((open.Id, "replaced"), new DateOnly(2026, 12, 15)), (moved.Cancel.Single(), moved.Add.Single().EffectiveDate));
        // A person whose access already ended is not terminated again.
        Assert.Empty(Plan(Rec("terminated", left: "2026-10-01"), Rec("terminated", left: "2026-10-01"), left: true).Add);
    }

    [Fact]
    public void Joiners_are_prepared_ahead_and_a_hire_cancelled_before_then_is_dropped()
    {
        var hire = Plan(null, Rec(hired: "2026-12-01")).Add.Single();
        Assert.Equal((HrChangeKinds.Hire, new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero)), (hire.Kind, hire.DueAt));
        var open = new OpenHrChange(Guid.NewGuid(), HrChangeKinds.Hire, HrChangeStatuses.Pending, new DateOnly(2026, 12, 1), new Dictionary<string, string?>());
        var cancelled = Plan(Rec(hired: "2026-12-01"), Rec("terminated", hired: "2026-12-01"), open: open);
        Assert.Equal((open.Id, "cancelled_in_hr"), cancelled.Cancel.Single());
        Assert.Empty(cancelled.Add);
        var later = Plan(Rec(hired: "2026-12-01"), Rec(hired: "2026-12-15"), open: open);
        Assert.Equal(new DateOnly(2026, 12, 15), later.Add.Single().EffectiveDate);
    }

    [Fact]
    public void A_former_employee_active_again_is_a_rehire()
    {
        var rehire = Plan(Rec("terminated", left: "2026-06-30"), Rec(hired: "2026-12-01"), left: true).Add.Single();
        Assert.Equal((HrChangeKinds.Rehire, new DateOnly(2026, 12, 1)), (rehire.Kind, rehire.EffectiveDate));
        // An old termination date before the new hire date is not leaving.
        Assert.False(HrLifecycle.Leaving(Rec(hired: "2026-12-01", left: "2026-06-30")));
        Assert.Equal(HrChangeKinds.Rehire, Plan(Rec("terminated", left: "2026-06-30"), Rec(hired: "2026-11-01", left: "2026-06-30"), left: true).Add.Single().Kind);
    }
}
