using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Policies;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 025 FR-003 and FR-005: clause numbering, stable keys and anchors, draft checks, completeness and compare.</summary>
public sealed class PolicyRulesTests
{
    private static readonly string[] Both = ["en", "ar"];

    private static BodyDocument Para(string text) => new([new Block(BlockTypes.Paragraph, [new Inline(text)])]);

    private static ClauseInput Clause(string? key, int level, string text, bool arabic = true) =>
        new(key, level, arabic ? new Dictionary<string, string> { ["en"] = text, ["ar"] = text + " ع" } : new Dictionary<string, string> { ["en"] = text },
            arabic ? new Dictionary<string, BodyDocument> { ["en"] = Para(text), ["ar"] = Para(text + " ع") } : new Dictionary<string, BodyDocument> { ["en"] = Para(text) });

    private static DraftRequest Draft(params ClauseInput[] clauses) =>
        new(new Dictionary<string, string> { ["en"] = "Code of conduct", ["ar"] = "مدونة السلوك" }, clauses, true, null);

    private static ValidationErrors Errors() => new();

    private static string[] Keys(ValidationErrors errors)
    {
        try
        {
            errors.ThrowIfAny();
            return [];
        }
        catch (AppException e)
        {
            return e.Errors!.Values.SelectMany(v => v).ToArray();
        }
    }

    [Fact]
    public void Clauses_are_numbered_from_their_order_and_levels()
    {
        Assert.Equal(["1", "1.1", "1.2", "1.2.1", "2", "2.1", "3"], ClauseRules.Numbers([1, 2, 2, 3, 1, 2, 1]));
        Assert.Empty(ClauseRules.Numbers([]));
    }

    [Fact]
    public void New_clauses_get_keys_and_anchors_that_follow_the_key()
    {
        var key = ClauseRules.NewKey();
        Assert.True(ClauseRules.IsKey(key));
        Assert.NotEqual(key, ClauseRules.NewKey());
        Assert.Equal("clause-" + key, ClauseRules.Anchor(key));
        Assert.False(ClauseRules.IsKey("Bad Key"));
        Assert.False(ClauseRules.IsKey("abc"));
    }

    [Fact]
    public void A_draft_keeps_the_locales_of_its_titles_in_tenant_order_and_refuses_bad_clauses()
    {
        var errors = Errors();
        var (locales, _, clauses) = ClauseRules.Normalize(Draft(Clause("keep1234", 1, "Scope"), Clause(null, 2, "Gifts")), Both, errors);
        Assert.False(errors.Any);
        Assert.Equal(Both, locales);
        Assert.Equal("keep1234", clauses[0].Key);
        Assert.True(ClauseRules.IsKey(clauses[1].Key));

        errors = Errors();
        ClauseRules.Normalize(Draft(Clause("dup12345", 1, "A"), Clause("dup12345", 3, "B")), Both, errors);
        Assert.Contains("validation.policies.clause_key", Keys(errors));
        Assert.Contains("validation.policies.clause_level", Keys(errors));

        errors = Errors();
        ClauseRules.Normalize(new DraftRequest(new Dictionary<string, string> { ["fr"] = "Code" }, [], null, null), Both, errors);
        Assert.Contains("validation.policies.locale", Keys(errors));

        errors = Errors();
        var image = new ClauseInput(null, 1, new Dictionary<string, string> { ["en"] = "A" },
            new Dictionary<string, BodyDocument> { ["en"] = new([new Block(BlockTypes.Image, AssetId: Guid.NewGuid())]) });
        ClauseRules.Normalize(Draft(image), Both, errors);
        Assert.Contains("validation.policies.clause_block", Keys(errors));
    }

    [Fact]
    public void A_version_is_complete_only_with_every_clause_in_every_locale_and_the_binding_one()
    {
        var errors = Errors();
        var (locales, _, clauses) = ClauseRules.Normalize(Draft(Clause(null, 1, "Scope"), Clause(null, 1, "Gifts", arabic: false)), Both, errors);
        ClauseRules.Complete(locales, clauses, "ar", errors);
        Assert.Contains("validation.policies.clause_incomplete", Keys(errors));

        errors = Errors();
        ClauseRules.Complete(["en"], [], "ar", errors);
        Assert.Contains("validation.policies.no_clauses", Keys(errors));
        Assert.Contains("validation.policies.binding_locale_missing", Keys(errors));
    }

    [Fact]
    public void Compare_marks_each_clause_added_changed_unchanged_or_removed()
    {
        var errors = Errors();
        var (_, _, before) = ClauseRules.Normalize(Draft(Clause("aaaa1111", 1, "Scope"), Clause("bbbb2222", 1, "Gifts"), Clause("cccc3333", 1, "Email")), Both, errors);
        var (_, _, after) = ClauseRules.Normalize(Draft(Clause("aaaa1111", 1, "Scope"), Clause("bbbb2222", 1, "Gifts and hospitality"), Clause(null, 1, "Travel")), Both, errors);
        Assert.False(errors.Any);
        var changes = ClauseRules.Compare(before, after, Both);
        Assert.Equal([ClauseChanges.Unchanged, ClauseChanges.Changed, ClauseChanges.Added, ClauseChanges.Removed], changes.Select(c => c.Change));
        Assert.Equal("cccc3333", changes[3].Key);
        Assert.Null(changes[2].Before);
        Assert.All(ClauseRules.Compare(null, after, Both), c => Assert.Equal(ClauseChanges.Added, c.Change));
    }

    [Fact]
    public void The_attestation_hash_is_sha256_hex()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ClauseRules.Hash("abc"));
    }
}
