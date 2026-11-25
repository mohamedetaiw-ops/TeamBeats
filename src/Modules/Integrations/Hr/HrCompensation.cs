using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Hr;

/// <summary>
/// The compensation statement (spec 019 R3 US10, FR-023) on the payslip rules: only the signed-in person's own, read
/// live from HR through the HR REST family of packs, after step-up, never cached or stored. There is no way to name
/// another person, so managers, HR officers and administrators cannot read it in TeamBeats. Reads are audited with the
/// number of components and never the amounts. Nothing here is a search source, an agent tool, a connector action or an
/// event, so compensation never reaches search, models or notifications. Letters (US9) read one person's statement for
/// one letter through <see cref="IHrCompensation"/>. It comes with <c>hr.payslips</c> (spec 019 section 6).
/// </summary>
public sealed class HrCompensation(ITenantContext context, IEntitlementService entitlements, IAuditWriter audit, HrRecords records, HrRestPack rest,
    ILogger<HrCompensation> log) : IHrCompensation
{
    /// <summary>The plan includes it and the person's HR connection has a compensation address; says nothing about the figures.</summary>
    public async Task<bool> AvailableAsync(CancellationToken ct) =>
        await entitlements.IsEnabledAsync(Capabilities.HrPayslips, ct) && await records.LinkAsync(context.RequireUser(), ct) is { Connection: { } c }
        && HrRestPack.Parse(c.Settings)?.CompensationPath is not null;

    public async Task<HrCompensationRead> MineAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrPayslips, ct);
        var user = context.RequireUser();
        var read = await ReadAsync(user, ct);
        if (read.Statement is { } statement)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.HrCompensationViewed, "user", user.ToString(), Changes: new { components = statement.Components.Count }), ct);
        }
        return read;
    }

    public async Task<HrCompensationRead> ForLetterAsync(Guid userId, Guid letterId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrPayslips, ct))
        {
            return new HrCompensationRead(HrCompensationRead.Unavailable, null);
        }
        var read = await ReadAsync(userId, ct);
        if (read.Statement is { } statement)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.HrCompensationReadForLetter, "user", userId.ToString(),
                Changes: new { letter = letterId, components = statement.Components.Count }), ct);
        }
        return read;
    }

    private async Task<HrCompensationRead> ReadAsync(Guid userId, CancellationToken ct)
    {
        if (await records.LinkAsync(userId, ct) is not { } link)
        {
            return new HrCompensationRead(HrCompensationRead.NotMatched, null);
        }
        if (link.Connection is not { } connection)
        {
            return new HrCompensationRead(HrCompensationRead.Unavailable, null);
        }
        try
        {
            var (components, failure) = await rest.ReadCompensationAsync(connection, link.Record, ct);
            if (failure is not null)
            {
                log.LogWarning("Compensation could not be read: {ErrorClass}", failure.ErrorClass);
            }
            return components is null
                ? new HrCompensationRead(HrCompensationRead.Unavailable, null)
                : new HrCompensationRead(HrCompensationRead.Ok, HrCompensationStatement.From(components, HrRestPack.Parse(connection.Settings)?.CompensationBasicCode));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new HrCompensationRead(HrCompensationRead.Unavailable, null);
        }
    }
}
