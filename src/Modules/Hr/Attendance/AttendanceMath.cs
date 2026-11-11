using TeamBeats.Modules.Hr.Contracts;

namespace TeamBeats.Modules.Hr.Attendance;

public static class PunchDirections
{
    public const string In = "in";
    public const string Out = "out";

    public static readonly IReadOnlyList<string> All = [In, Out];
}

public static class PunchSources
{
    public const string Web = "web";
    public const string Clock = "clock";
}

/// <summary>What a day is against the person's calendar (spec 022 US3 scenario 2).</summary>
public static class AttendanceDayKinds
{
    public const string Working = "working";
    public const string DayOff = "day_off";
    public const string Holiday = "holiday";
    public const string Leave = "leave";
}

/// <summary>The exceptions a day can show (US3 scenario 2). <c>worked_day_off</c> marks work on a holiday or day off (scenario 4).</summary>
public static class AttendanceFlags
{
    public const string Late = "late";
    public const string EarlyLeave = "early_leave";
    public const string MissingPunch = "missing_punch";
    public const string Absent = "absent";
    public const string Remote = "remote";
    public const string WorkedDayOff = "worked_day_off";
}

/// <summary>
/// A working hours rule (FR-010): the day starts and ends at these local times, and arriving later or leaving earlier
/// than the grace minutes allow is an exception. Inside a reduced hours period the day ends that many hours after the
/// start instead (US2 scenario 3).
/// </summary>
public sealed record WorkRule(TimeOnly Start, TimeOnly End, int GraceLateMinutes, int GraceEarlyMinutes)
{
    public static readonly WorkRule Default = new(new TimeOnly(9, 0), new TimeOnly(17, 0), 15, 15);
}

public sealed record Punch(DateTimeOffset At, string Direction, bool Remote, string Source);

public sealed record AttendanceDay(
    DateOnly Date,
    string Kind,
    TimeOnly? FirstIn,
    TimeOnly? LastOut,
    decimal HoursWorked,
    decimal ExpectedHours,
    IReadOnlyList<string> Flags,
    int Punches);

public sealed record AttendanceTotals(int WorkedDays, decimal HoursWorked, decimal ExpectedHours, int Late, int EarlyLeave, int MissingPunch, int Absent, int Remote,
    int WorkedDaysOff);

/// <summary>Pure rules that turn one day's punches into hours and exceptions, so they can be tested without a database.</summary>
public static class AttendanceMath
{
    /// <summary>
    /// One day for one person. <paramref name="punches"/> are that day's punches in local time order; a day still in
    /// progress (today) does not count an open sign in as a missing punch, and only past working days can be absent.
    /// <paramref name="leaveFraction"/> is approved leave on the day (0, 0.5 or 1): it removes the day's exceptions.
    /// </summary>
    public static AttendanceDay Evaluate(DateOnly date, IReadOnlyList<Punch> punches, WorkRule rule, WorkingCalendarSnapshot calendar, decimal leaveFraction,
        DateOnly today)
    {
        var working = calendar.WorkingFraction(date);
        var kind = leaveFraction >= 1m && working > 0 ? AttendanceDayKinds.Leave
            : working > 0 ? AttendanceDayKinds.Working
            : calendar.IsHoliday(date) ? AttendanceDayKinds.Holiday : AttendanceDayKinds.DayOff;
        var ordered = punches.OrderBy(p => p.At).ToList();
        var local = (DateTimeOffset at) => TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, calendar.Zone).DateTime);

        var worked = TimeSpan.Zero;
        DateTimeOffset? open = null;
        var missing = false;
        foreach (var p in ordered)
        {
            if (p.Direction == PunchDirections.In)
            {
                missing |= open is not null;
                open = p.At;
            }
            else if (open is { } start)
            {
                worked += p.At - start;
                open = null;
            }
            else
            {
                missing = true;
            }
        }
        missing |= open is not null && date < today;

        var dayLength = rule.End.ToTimeSpan() - rule.Start.ToTimeSpan();
        if (calendar.ReducedHours(date) is { } reduced)
        {
            dayLength = TimeSpan.FromHours((double)reduced);
        }
        var expected = kind == AttendanceDayKinds.Working ? Round((decimal)dayLength.TotalHours * working * (1m - Math.Min(leaveFraction, 1m))) : 0m;

        var firstIn = ordered.FirstOrDefault(p => p.Direction == PunchDirections.In) is { } first ? local(first.At) : (TimeOnly?)null;
        var lastOut = ordered.LastOrDefault(p => p.Direction == PunchDirections.Out) is { } last ? local(last.At) : (TimeOnly?)null;
        var flags = new List<string>();
        if (kind == AttendanceDayKinds.Working && leaveFraction == 0m && working >= 1m)
        {
            if (firstIn is { } arrived && arrived > rule.Start.AddMinutes(rule.GraceLateMinutes))
            {
                flags.Add(AttendanceFlags.Late);
            }
            var end = rule.Start.Add(dayLength);
            if (lastOut is { } left && open is null && left < end.AddMinutes(-rule.GraceEarlyMinutes))
            {
                flags.Add(AttendanceFlags.EarlyLeave);
            }
            if (ordered.Count == 0 && date < today)
            {
                flags.Add(AttendanceFlags.Absent);
            }
        }
        if (missing && kind != AttendanceDayKinds.Leave)
        {
            flags.Add(AttendanceFlags.MissingPunch);
        }
        if (ordered.Any(p => p.Remote))
        {
            flags.Add(AttendanceFlags.Remote);
        }
        if (kind is AttendanceDayKinds.Holiday or AttendanceDayKinds.DayOff && worked > TimeSpan.Zero)
        {
            flags.Add(AttendanceFlags.WorkedDayOff);
        }
        return new AttendanceDay(date, kind, firstIn, lastOut, Round((decimal)worked.TotalHours), expected, flags, ordered.Count);
    }

    public static AttendanceTotals Totals(IEnumerable<AttendanceDay> days)
    {
        var list = days.ToList();
        int Count(string flag) => list.Count(d => d.Flags.Contains(flag));
        return new AttendanceTotals(list.Count(d => d.HoursWorked > 0), list.Sum(d => d.HoursWorked), list.Sum(d => d.ExpectedHours), Count(AttendanceFlags.Late),
            Count(AttendanceFlags.EarlyLeave), Count(AttendanceFlags.MissingPunch), Count(AttendanceFlags.Absent), Count(AttendanceFlags.Remote),
            Count(AttendanceFlags.WorkedDayOff));
    }

    /// <summary>Great-circle distance in metres, for the optional location check (US3 scenario 1).</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private static decimal Round(decimal hours) => Math.Round(hours, 2, MidpointRounding.AwayFromZero);
}
