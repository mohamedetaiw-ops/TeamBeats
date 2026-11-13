using Microsoft.Extensions.Configuration;
using TeamBeats.Modules.Tenancy.Contracts;

namespace TeamBeats.Modules.Tenancy;

/// <summary>
/// The hosting regions this installation serves (spec 026 FR-008), from <c>Platform:Regions</c>: <c>Offered</c> lists the
/// region keys and <c>Default</c> names the one a tenant gets when none is chosen. Each region is its own deployment with
/// its own database, object storage, backups and search index, so a tenant can only be created here for a region this
/// installation hosts. Without configuration the installation offers one region, <c>primary</c>.
/// </summary>
public sealed class PlatformRegions : IPlatformRegions
{
    public const string Section = "Platform:Regions";
    public const string Fallback = "primary";

    public PlatformRegions(IReadOnlyList<string> offered, string? defaultRegion)
    {
        var list = offered.Select(r => r.Trim().ToLowerInvariant()).Where(r => r.Length > 0).Distinct().ToList();
        if (list.Count == 0)
        {
            list.Add(Fallback);
        }
        var chosen = string.IsNullOrWhiteSpace(defaultRegion) ? list[0] : defaultRegion.Trim().ToLowerInvariant();
        if (!list.Contains(chosen))
        {
            throw new InvalidOperationException($"{Section}:Default must be one of the offered regions.");
        }
        Offered = list;
        Default = chosen;
    }

    public IReadOnlyList<string> Offered { get; }

    public string Default { get; }

    public static PlatformRegions From(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        return new PlatformRegions(section.GetSection("Offered").Get<string[]>() ?? [], section["Default"]);
    }
}
