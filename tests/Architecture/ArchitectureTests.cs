using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TeamBeats.Api;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using Xunit;

namespace TeamBeats.Architecture.Tests;

/// <summary>Constitution rules that can be checked statically (CLAUDE.md hard rules 2, 3, 5, 6, 8 and spec 006 audit coverage).</summary>
public sealed partial class ArchitectureTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TeamBeats.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> SourceFiles(string relative, string pattern = "*.cs") =>
        Directory.EnumerateFiles(Path.Combine(Root, relative), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string Rel(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

    private static IEnumerable<string> ProjectReferences(string csproj) =>
        XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value));

    [Fact]
    public void Modules_reference_only_building_blocks_and_contracts()
    {
        // Documented exception: development sign-in extends the Identity module it belongs to (ADR-0004).
        var allowed = new HashSet<string> { "TeamBeats.Modules.Identity.DevAuth->TeamBeats.Modules.Identity" };
        var violations = new List<string>();
        foreach (var csproj in SourceFiles("src/Modules", "*.csproj"))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            foreach (var reference in ProjectReferences(csproj))
            {
                var isModuleImplementation = reference.StartsWith("TeamBeats.Modules.", StringComparison.Ordinal) && !reference.EndsWith(".Contracts", StringComparison.Ordinal);
                if (isModuleImplementation && !allowed.Contains($"{name}->{reference}"))
                {
                    violations.Add($"{name} -> {reference}");
                }
            }
        }
        Assert.True(violations.Count == 0, "Module implementation references: " + string.Join(", ", violations));
    }

    [Fact]
    public void Production_api_does_not_contain_development_sign_in()
    {
        Assert.DoesNotContain("TeamBeats.Modules.Identity.DevAuth", ProjectReferences(Path.Combine(Root, "src/Api/TeamBeats.Api.csproj")));
        Assert.DoesNotContain("TeamBeats.DevFakes", ProjectReferences(Path.Combine(Root, "src/Api/TeamBeats.Api.csproj")));
        Assert.DoesNotContain("TeamBeats.DevFakes", ProjectReferences(Path.Combine(Root, "src/Workers/TeamBeats.Workers.csproj")));

        var seen = new HashSet<string>();
        var queue = new Queue<AssemblyName>([typeof(ApiProgram).Assembly.GetName()]);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name.Name!) || !name.Name!.StartsWith("TeamBeats", StringComparison.Ordinal))
            {
                continue;
            }
            Assert.NotEqual("TeamBeats.Modules.Identity.DevAuth", name.Name);
            Assert.NotEqual("TeamBeats.DevFakes", name.Name);
            foreach (var r in Assembly.Load(name).GetReferencedAssemblies())
            {
                queue.Enqueue(r);
            }
        }
    }

    [Fact]
    public void Modules_do_not_query_other_modules_tables()
    {
        var schemas = new Dictionary<string, string>
        {
            ["Tenancy"] = "tenancy", ["Identity"] = "identity", ["Authorization"] = "authz",
            ["Entitlements"] = "entitlements", ["Audit"] = "audit", ["Integrations"] = "integrations",
            ["Experience"] = "experience", ["Notifications"] = "notifications",
        };
        var violations = new List<string>();
        foreach (var (module, own) in schemas)
        {
            foreach (var file in SourceFiles($"src/Modules/{module}"))
            {
                var text = File.ReadAllText(file);
                foreach (var other in schemas.Values.Where(s => s != own))
                {
                    if (Regex.IsMatch(text, $@"\b(from|join|into|update)\s+{other}\.", RegexOptions.IgnoreCase))
                    {
                        violations.Add($"{Rel(file)} touches {other}.*");
                    }
                }
            }
        }
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    /// <summary>
    /// Spec 032 FR-014 and the source's non-negotiable rule: templates, layouts and page code never depend on a concrete
    /// component. Only the registry knows components, and only through manifests registered by their owners.
    /// </summary>
    [Fact]
    public void Templates_layouts_and_page_code_never_name_a_concrete_component()
    {
        var ids = new TeamBeats.Api.DevHost.ReferencePacks().Manifests().Select(m => m.Id).ToList();
        var files = SourceFiles("src/Modules/Experience").Where(f => !Rel(f).StartsWith("src/Modules/Experience/Components/", StringComparison.Ordinal))
            .Concat(Directory.GetFiles(Path.Combine(Root, "src/Modules/Experience/Catalogue"), "*.json"));
        var violations = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            violations.AddRange(ids.Where(id => text.Contains(id, StringComparison.Ordinal)).Select(id => $"{Rel(file)} names {id}"));
            if (text.Contains("componentRef", StringComparison.Ordinal) && Rel(file).EndsWith(".json", StringComparison.Ordinal))
            {
                violations.Add($"{Rel(file)} holds a placement");
            }
        }
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void No_role_name_checks_outside_authorization()
    {
        var roleKeys = typeof(BuiltInRoles).GetFields().Select(f => (string)f.GetValue(null)!).ToList();
        var violations = new List<string>();
        foreach (var file in SourceFiles("src").Where(f => !Rel(f).StartsWith("src/Modules/Authorization/", StringComparison.Ordinal)
                                                        && !Rel(f).StartsWith("src/BuildingBlocks/Authorization.Contracts/", StringComparison.Ordinal)))
        {
            foreach (var (line, i) in File.ReadLines(file).Select((l, i) => (l, i + 1)))
            {
                // Provisioning a built-in role (first tenant admin) is allowed; deciding on a role name is not.
                if (line.Contains("BuiltInRoles.", StringComparison.Ordinal) && !line.Contains("AssignBuiltInRoleAsync", StringComparison.Ordinal))
                {
                    violations.Add($"{Rel(file)}:{i}");
                }
                // Membership type constants share the word "employee" with the role key; they are not role checks.
                var isMembershipTypeConstant = Rel(file) == "src/Modules/Tenancy.Contracts/Contracts.cs" && line.Contains("public const string", StringComparison.Ordinal);
                if (!isMembershipTypeConstant && roleKeys.Any(k => line.Contains($"\"{k}\"", StringComparison.Ordinal)))
                {
                    violations.Add($"{Rel(file)}:{i} (role key literal)");
                }
            }
        }
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void No_package_name_literals_outside_entitlements()
    {
        var violations = SourceFiles("src")
            .Where(f => !Rel(f).StartsWith("src/Modules/Entitlements/", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (File: f, Line: l, No: i + 1)))
            .Where(x => PackageLiteral().IsMatch(x.Line))
            .Select(x => $"{Rel(x.File)}:{x.No}")
            .ToList();
        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void Permission_catalogue_matches_database_seed()
    {
        // Every authorization migration may add permissions to the catalogue; together they must equal the code catalogue.
        var seeded = new HashSet<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "src/Modules/Authorization/Migrations"), "*.sql"))
        {
            var sql = File.ReadAllText(file);
            var start = sql.IndexOf("insert into authz.permission", StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }
            var seedBlock = sql[start..];
            seedBlock = seedBlock[..seedBlock.IndexOf(';')];
            seeded.UnionWith(Regex.Matches(seedBlock, @"\('([a-z._]+)'\)").Select(m => m.Groups[1].Value));
        }
        Assert.Equal(Permissions.All.Order(), seeded.Order());
    }

    [Fact]
    public void Permission_keys_follow_area_dot_action()
    {
        var bad = Permissions.All.Where(k => !Regex.IsMatch(k, "^[a-z][a-z_]*\\.[a-z][a-z_]*$")).ToList();
        Assert.True(bad.Count == 0, "Permission keys must be <area>.<action> (spec 004 FR-013): " + string.Join(", ", bad));
    }

    [Fact]
    public void Permission_and_capability_catalogues_do_not_share_keys()
    {
        var capabilities = typeof(TeamBeats.Entitlements.Contracts.Capabilities)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();
        var shared = Permissions.All.Where(capabilities.Contains).ToList();
        Assert.True(shared.Count == 0, "Keys in both catalogues (spec 004 FR-013, spec 005 FR-010): " + string.Join(", ", shared));
    }

    private static Dictionary<string, string> Catalogue(string locale) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Root, $"src/BuildingBlocks/Localization/catalogues/{locale}.json")))!;

    [Fact]
    public void Arabic_and_English_catalogues_have_the_same_keys_and_no_empty_values()
    {
        var en = Catalogue("en");
        var ar = Catalogue("ar");
        Assert.Empty(en.Keys.Except(ar.Keys));
        Assert.Empty(ar.Keys.Except(en.Keys));
        Assert.DoesNotContain(ar, kv => string.IsNullOrWhiteSpace(kv.Value));
        Assert.DoesNotContain(en, kv => string.IsNullOrWhiteSpace(kv.Value));
    }

    [Fact]
    public void Every_resource_key_used_in_code_exists_in_launch_catalogues()
    {
        var en = Catalogue("en");
        var ar = Catalogue("ar");
        var used = SourceFiles("src")
            .SelectMany(f => ResourceKey().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet();
        var problemCodes = typeof(TeamBeats.Primitives.ProblemCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => "problem." + (string)f.GetValue(null)!);
        used.UnionWith(problemCodes);

        Assert.True(used.Count > 30, $"Expected to find resource keys, found {used.Count}.");
        var missingEn = used.Where(k => !en.ContainsKey(k)).Order().ToList();
        var missingAr = used.Where(k => !ar.ContainsKey(k)).Order().ToList();
        Assert.True(missingEn.Count == 0, "Missing in en: " + string.Join(", ", missingEn));
        Assert.True(missingAr.Count == 0, "Missing in ar: " + string.Join(", ", missingAr));
    }

    [Fact]
    public void Every_audit_action_required_in_R0_is_asserted_by_a_test()
    {
        var tests = string.Concat(SourceFiles("tests").Select(File.ReadAllText));
        var fields = typeof(AuditActions).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral)
            .ToDictionary(f => (string)f.GetValue(null)!, f => f.Name);
        var missing = AuditActions.RequiredInR0.Concat(AuditActions.RequiredInR1)
            .Where(a => !tests.Contains($"AuditActions.{fields[a]}", StringComparison.Ordinal))
            .ToList();
        Assert.True(missing.Count == 0, "Audit actions without a test: " + string.Join(", ", missing));
    }

    [Fact]
    public void No_secrets_in_tracked_configuration()
    {
        var suspicious = SourceFiles("src", "appsettings*.json")
            .Where(f => !Rel(f).StartsWith("src/Api.DevHost/", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"Password\s*=\s*[^;""]+", RegexOptions.IgnoreCase))
            .Select(Rel)
            .ToList();
        Assert.Empty(suspicious);
    }

    [Fact]
    public void Test_account_passwords_are_never_in_tracked_configuration()
    {
        // Spec 003 US7: the staging test account password is a deployment secret (constitution IV).
        var offending = SourceFiles("src", "appsettings*.json")
            .Where(f => File.ReadAllText(f).Contains("TestAccounts", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "\"Password\"\\s*:", RegexOptions.IgnoreCase))
            .Select(Rel)
            .ToList();
        Assert.Empty(offending);
    }

    [Fact]
    public void Production_dockerfiles_never_build_the_development_host()
    {
        foreach (var name in new[] { "api.Dockerfile", "workers.Dockerfile" })
        {
            var text = File.ReadAllText(Path.Combine(Root, "deploy/docker", name));
            Assert.DoesNotContain("Api.DevHost", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ASPNETCORE_ENVIRONMENT=Staging", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Fakes_are_referenced_only_by_the_development_host_and_tests()
    {
        // Fake external systems (docs/testing/fake-systems.md) never reach a production composition (ADR-0004).
        var offenders = SourceFiles("src", "*.csproj")
            .Where(f => !Rel(f).StartsWith("src/Api.DevHost/", StringComparison.Ordinal) && !Rel(f).StartsWith("src/DevFakes/", StringComparison.Ordinal))
            .Where(f => ProjectReferences(f).Contains("TeamBeats.DevFakes"))
            .Select(Rel)
            .ToList();
        Assert.Empty(offenders);
        var fakes = ProjectReferences(Path.Combine(Root, "src/DevFakes/TeamBeats.DevFakes.csproj")).ToList();
        Assert.Empty(fakes);
    }

    [Fact]
    public void Fake_scenarios_hold_only_reserved_example_addresses()
    {
        // CLAUDE.md rule 9: no real customer data. Every address in a scenario is on a reserved example domain.
        foreach (var file in SourceFiles("src/Api.DevHost/scenarios", "*.json"))
        {
            var text = File.ReadAllText(file);
            var addresses = EmailAddress().Matches(text).Select(m => m.Value).ToList();
            Assert.NotEmpty(addresses);
            Assert.All(addresses, a => Assert.True(a.EndsWith(".example", StringComparison.OrdinalIgnoreCase), $"{Rel(file)}: {a}"));
        }
    }

    [GeneratedRegex("[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}")]
    private static partial Regex EmailAddress();

    [GeneratedRegex("\"(workplace|workplace_plus|enterprise_government)\"")]
    private static partial Regex PackageLiteral();

    [GeneratedRegex("\"((?:validation|conflict|warning|admin\\.nav|authz\\.denied)[a-z0-9_.]*[a-z0-9_])\"")]
    private static partial Regex ResourceKey();
}
