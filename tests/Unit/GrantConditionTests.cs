using System.Diagnostics;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Authorization;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 004 FR-014 to FR-016: condition parsing, evaluation, monotonicity and batch cost.</summary>
public sealed class GrantConditionTests
{
    private static GrantCondition Parse(string json)
    {
        var errors = new ValidationErrors();
        using var doc = JsonDocument.Parse(json);
        var condition = GrantCondition.Parse(doc.RootElement.Clone(), errors);
        Assert.False(errors.Any);
        return condition!;
    }

    private static ConditionTrace Eval(string json, params (string Key, string Value)[] attributes)
    {
        var map = attributes.ToDictionary(a => a.Key, a => a.Value);
        return Parse(json).Evaluate(k => map.TryGetValue(k, out var v) ? v : null);
    }

    [Theory]
    [InlineData("""{"attribute":"job_grade","operator":"gte","value":7}""", "7", true)]
    [InlineData("""{"attribute":"job_grade","operator":"gte","value":7}""", "6.5", false)]
    [InlineData("""{"attribute":"job_grade","operator":"lte","value":7}""", "10", false)]
    [InlineData("""{"attribute":"job_grade","operator":"eq","value":7}""", "7.0", true)]
    [InlineData("""{"attribute":"job_grade","operator":"gte","value":7}""", "senior", false)]
    [InlineData("""{"attribute":"job_title","operator":"eq","value":"Manager"}""", "manager", true)]
    [InlineData("""{"attribute":"job_title","operator":"ne","value":"Manager"}""", "Analyst", true)]
    [InlineData("""{"attribute":"job_title","operator":"not_in","value":["A","B"]}""", "b", false)]
    [InlineData("""{"attribute":"job_title","operator":"in","value":["A","B"]}""", "C", false)]
    public void Comparisons_follow_the_attribute_kind(string json, string actual, bool expected)
    {
        var key = json.Contains("job_grade", StringComparison.Ordinal) ? "job_grade" : "job_title";
        Assert.Equal(expected, Eval(json, (key, actual)).Met);
    }

    [Fact]
    public void Missing_attribute_makes_the_whole_condition_false_even_under_any_or_not_equals()
    {
        var anyGroup = """{"any":[{"attribute":"job_grade","operator":"gte","value":7},{"attribute":"membership_type","operator":"eq","value":"employee"}]}""";
        var trace = Eval(anyGroup, ("membership_type", "employee"));
        Assert.False(trace.Met);
        Assert.Equal(["job_grade"], trace.MissingAttributes);
        Assert.True(trace.Clauses[1].Met);

        Assert.False(Eval("""{"attribute":"office","operator":"ne","value":"Cairo"}""").Met);
        Assert.False(Eval("""{"attribute":"office","operator":"not_in","value":["Cairo"]}""").Met);
    }

    [Fact]
    public void Groups_combine_with_and_or_in_order()
    {
        var json = """{"all":[{"attribute":"membership_type","operator":"eq","value":"employee"},{"any":[{"attribute":"job_grade","operator":"gte","value":8},{"attribute":"office","operator":"eq","value":"Dubai"}]}]}""";
        Assert.True(Eval(json, ("membership_type", "employee"), ("job_grade", "5"), ("office", "dubai")).Met);
        Assert.False(Eval(json, ("membership_type", "employee"), ("job_grade", "5"), ("office", "Cairo")).Met);
        Assert.False(Eval(json, ("membership_type", "contractor"), ("job_grade", "9"), ("office", "Dubai")).Met);
    }

    [Fact]
    public void Canonical_json_round_trips()
    {
        var condition = Parse("""{"all":[{"attribute":"membership_type","operator":"in","value":["Employee","employee","Contractor"]},{"attribute":"mfa_performed","operator":"eq","value":true}]}""");
        var canonical = condition.ToCanonicalJson();
        Assert.Equal("""{"all":[{"attribute":"membership_type","operator":"in","value":["employee","contractor"]},{"attribute":"mfa_performed","operator":"eq","value":true}]}""", canonical);
        Assert.Equal(canonical, GrantCondition.FromStored(canonical)!.ToCanonicalJson());
    }

    [Fact]
    public void Stored_condition_that_no_longer_validates_never_holds()
    {
        var stale = GrantCondition.FromStored("""{"attribute":"retired_attribute","operator":"eq","value":"x"}""");
        Assert.False(stale!.Evaluate(_ => "x").Met);
    }

    /// <summary>
    /// Spec 004 section 14: adding any condition to any grant never adds an allowed decision. Random grants, ACLs,
    /// users, targets and conditions; each decision with conditions must imply the same decision without them.
    /// </summary>
    [Fact]
    public void Adding_conditions_never_adds_an_allowed_decision()
    {
        var random = new Random(4004);
        Guid[] units = [.. Enumerable.Range(0, 6).Select(_ => Guid.NewGuid())];
        string[] permissions = ["content.view", "content.publish", "workflow.approve"];
        string[] conditions =
        [
            """{"attribute":"job_grade","operator":"gte","value":5}""",
            """{"attribute":"membership_type","operator":"eq","value":"employee"}""",
            """{"attribute":"classification","operator":"in","value":["public","internal"]}""",
            """{"any":[{"attribute":"office","operator":"eq","value":"Dubai"},{"attribute":"job_grade","operator":"lte","value":3}]}""",
            """{"attribute":"network_zone","operator":"eq","value":"office"}""",
            $$"""{"attribute":"owning_org_unit","operator":"in","value":["{{units[0]}}","{{units[1]}}"]}""",
        ];

        for (var i = 0; i < 2000; i++)
        {
            var user = Guid.NewGuid();
            var groups = new HashSet<Guid> { Guid.NewGuid() };
            var member = new HashSet<Guid> { units[random.Next(units.Length)] };
            var attributes = new Dictionary<string, string>();
            if (random.Next(2) == 0) { attributes["job_grade"] = random.Next(1, 10).ToString(System.Globalization.CultureInfo.InvariantCulture); }
            if (random.Next(2) == 0) { attributes["membership_type"] = random.Next(2) == 0 ? "employee" : "contractor"; }
            if (random.Next(2) == 0) { attributes["office"] = random.Next(2) == 0 ? "Dubai" : "Cairo"; }

            var plain = new List<Grant>();
            var conditional = new List<Grant>();
            for (var k = random.Next(0, 4); k > 0; k--)
            {
                var scope = (ScopeType)random.Next(3);
                var scopeId = scope == ScopeType.Tenant ? (Guid?)null : units[random.Next(units.Length)];
                var covered = scopeId is { } s ? new HashSet<Guid> { s } : new HashSet<Guid>();
                var grant = new Grant(Guid.NewGuid(), "role", scope, scopeId, null,
                    new HashSet<string> { permissions[random.Next(permissions.Length)] }, covered, "direct");
                plain.Add(grant);
                conditional.Add(random.Next(3) == 0 ? grant : grant with { Condition = Parse(conditions[random.Next(conditions.Length)]) });
            }

            var resource = new ResourceDescriptor("doc", Guid.NewGuid(), units[random.Next(units.Length)],
                random.Next(4) == 0 ? ResourceVisibility.Tenant : ResourceVisibility.Restricted,
                random.Next(3) == 0 ? null : ConditionAttributes.Classifications[random.Next(4)]);
            var plainAcl = new List<AclEntry>();
            var conditionalAcl = new List<AclEntry>();
            for (var k = random.Next(0, 3); k > 0; k--)
            {
                var (type, id) = random.Next(3) switch
                {
                    0 => ("user", user),
                    1 => ("group", groups.First()),
                    _ => ("org_unit", units[random.Next(units.Length)]),
                };
                var entry = new AclEntry("doc", resource.ResourceId, type, id, permissions[random.Next(permissions.Length)]);
                plainAcl.Add(entry);
                conditionalAcl.Add(random.Next(3) == 0 ? entry : entry with { Condition = Parse(conditions[random.Next(conditions.Length)]) });
            }

            var rules = random.Next(4) == 0 ? new List<AppliedRule> { new(member.First(), "members_view_descendant_restricted", units.ToHashSet()) } : [];
            var without = new EffectiveGrants(user, true, plain, groups, member, member, rules, attributes);
            var with = without with { Grants = conditional };
            AuthzTarget target = random.Next(3) switch
            {
                0 => AuthzTarget.Tenant,
                1 => AuthzTarget.OrgUnit(units[random.Next(units.Length)]),
                _ => AuthzTarget.Resource(resource),
            };

            foreach (var permission in permissions)
            {
                if (Evaluator.Evaluate(with, permission, target, conditionalAcl).Allowed)
                {
                    Assert.True(Evaluator.Evaluate(without, permission, target, plainAcl).Allowed, $"iteration {i}");
                }
                var c = Evaluator.Coverage(with, permission);
                var p = Evaluator.Coverage(without, permission);
                Assert.True(!c.WholeTenant || p.WholeTenant);
                Assert.True(p.WholeTenant || c.Units.IsSubsetOf(p.Units));
            }
        }
    }

    /// <summary>FR-016: conditions stay within the batch target of 1,000 resources in under 50 ms (spec 004 section 15).</summary>
    [Fact]
    public void Batch_of_1000_conditional_decisions_stays_within_target()
    {
        var unit = Guid.NewGuid();
        var condition = Parse("""{"all":[{"attribute":"membership_type","operator":"eq","value":"employee"},{"any":[{"attribute":"job_grade","operator":"gte","value":5},{"attribute":"classification","operator":"in","value":["public","internal"]}]}]}""");
        var grants = new EffectiveGrants(Guid.NewGuid(), true,
            [new Grant(Guid.NewGuid(), "content_editor", ScopeType.OrgSubtree, unit, null, new HashSet<string> { "content.view" }, new HashSet<Guid> { unit }, "direct", condition)],
            new HashSet<Guid>(), new HashSet<Guid> { unit }, new HashSet<Guid> { unit }, [],
            new Dictionary<string, string> { ["membership_type"] = "employee", ["job_grade"] = "4" });
        var resources = Enumerable.Range(0, 1000)
            .Select(i => new ResourceDescriptor("doc", Guid.NewGuid(), unit, ResourceVisibility.Restricted, ConditionAttributes.Classifications[i % 4]))
            .ToList();
        var acl = resources.Select(r => new AclEntry("doc", r.ResourceId, "org_unit", unit, "content.view", condition)).ToLookup(a => a.ResourceId);

        int Run() => resources.Count(r => Evaluator.Evaluate(grants, "content.view", AuthzTarget.Resource(r), acl[r.ResourceId].ToList()).Allowed);
        Assert.Equal(500, Run());

        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var sw = Stopwatch.StartNew();
            Run();
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        Assert.True(timings[(int)(timings.Count * 0.95) - 1] < 50, $"p95 {timings[(int)(timings.Count * 0.95) - 1]} ms");
    }
}
