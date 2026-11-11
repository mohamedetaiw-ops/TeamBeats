using TeamBeats.Modules.Hr.Expenses;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 022 T023: the expense policy shape, line checks, grade limits, conversion and the route by amount (FR-023, US8).</summary>
public sealed class ExpenseRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    private static ExpensePolicyDocument Policy() => ExpenseRules.Default() with
    {
        Currency = "AED",
        Categories =
        [
            new("meals", new() { ["en"] = "Meals", ["ar"] = "الوجبات" }, ExpenseKinds.Amount, true, null, [new("G7", 300m), new(null, 150m)]),
            new("per_diem", new() { ["en"] = "Per diem", ["ar"] = "بدل يومي" }, ExpenseKinds.PerDiem, false, 150m),
            new("mileage", new() { ["en"] = "Mileage", ["ar"] = "المسافة" }, ExpenseKinds.Mileage, false, 0.55m),
        ],
        Tiers = [new(1000m, [Guid.NewGuid()]), new(5000m, [Guid.NewGuid()])],
    };

    private static ExpenseLineInput Line(string category, DateOnly? date = null, decimal? amount = null, string? currency = "AED", int? days = null, decimal? km = null,
        Guid? receipt = null, decimal? tax = null) => new(category, date ?? Today, amount, currency, null, tax, null, receipt, days, km, null);

    [Fact]
    public void The_default_policy_has_no_currency_and_a_full_policy_validates()
    {
        Assert.Null(ExpenseRules.Default().Currency);
        Assert.Contains(ExpenseRules.Validate(ExpenseRules.Default()), e => e.Key == "validation.expenses.currency");
        Assert.Empty(ExpenseRules.Validate(Policy()));
    }

    [Fact]
    public void Tiers_must_rise_and_rates_belong_to_per_diem_and_mileage_only()
    {
        var falling = Policy() with { Tiers = [new(5000m, [Guid.NewGuid()]), new(1000m, [Guid.NewGuid()])] };
        Assert.Contains(ExpenseRules.Validate(falling), e => e.Key == "validation.expenses.tiers");
        var rated = Policy() with { Categories = [new("meals", new() { ["en"] = "Meals", ["ar"] = "الوجبات" }, ExpenseKinds.Amount, true, 10m)] };
        Assert.Contains(ExpenseRules.Validate(rated), e => e.Key == "validation.expenses.category_rate");
        var twoForAll = Policy() with
        {
            Categories = [new("meals", new() { ["en"] = "Meals", ["ar"] = "الوجبات" }, ExpenseKinds.Amount, true, null, [new(null, 1m), new(null, 2m)])],
        };
        Assert.Contains(ExpenseRules.Validate(twoForAll), e => e.Key == "validation.expenses.category_limits");
    }

    [Theory]
    [InlineData("G7", 300)]
    [InlineData("g7", 300)]
    [InlineData("G2", 150)]
    [InlineData(null, 150)]
    public void The_limit_is_the_grade_s_own_else_the_one_for_every_grade(string? grade, int expected) =>
        Assert.Equal(expected, ExpenseRules.LimitFor(Policy().Categories[0], grade));

    [Fact]
    public void Per_diem_and_mileage_are_priced_at_the_category_rate_in_the_policy_currency()
    {
        var errors = new List<(string Field, string Key)>();
        var perDiem = ExpenseRules.Check(Policy(), Line("per_diem", days: 3, currency: "USD"), null, Today, "l", errors)!;
        Assert.Equal((450m, "AED"), (perDiem.Amount, perDiem.Currency));
        var mileage = ExpenseRules.Check(Policy(), Line("mileage", km: 12.5m), null, Today, "l", errors)!;
        Assert.Equal(6.88m, mileage.Amount);
        Assert.Empty(errors);
    }

    [Fact]
    public void Dates_amounts_receipts_and_tax_are_checked()
    {
        var errors = new List<(string Field, string Key)>();
        Assert.Null(ExpenseRules.Check(Policy(), Line("meals", Today.AddDays(1), 10m, receipt: Guid.NewGuid()), null, Today, "a", errors));
        Assert.Null(ExpenseRules.Check(Policy(), Line("meals", Today.AddDays(-61), 10m, receipt: Guid.NewGuid()), null, Today, "b", errors));
        Assert.Null(ExpenseRules.Check(Policy(), Line("meals", amount: 10.005m, receipt: Guid.NewGuid()), null, Today, "c", errors));
        Assert.Null(ExpenseRules.Check(Policy(), Line("meals", amount: 10m), null, Today, "d", errors));
        Assert.Null(ExpenseRules.Check(Policy(), Line("meals", amount: 10m, currency: "aedx", receipt: Guid.NewGuid()), null, Today, "e", errors));
        Assert.Null(ExpenseRules.Check(Policy(), Line("meals", amount: 10m, receipt: Guid.NewGuid(), tax: 11m), null, Today, "f", errors));
        Assert.Null(ExpenseRules.Check(Policy(), Line("per_diem", days: 0), null, Today, "g", errors));
        Assert.Null(ExpenseRules.Check(Policy(), Line("parking", amount: 5m), null, Today, "h", errors));
        Assert.Equal(
        [
            ("a.date", "validation.expenses.date"), ("b.date", "validation.expenses.too_old"), ("c.amount", "validation.expenses.amount"),
            ("d.receiptId", "validation.expenses.receipt_required"), ("e.currency", "validation.expenses.currency"), ("f.tax", "validation.expenses.tax"),
            ("g.days", "validation.expenses.days"), ("h.category", "validation.expenses.category"),
        ], errors);
        Assert.NotNull(ExpenseRules.Check(Policy(), Line("meals", Today.AddDays(-60), 10m, currency: "usd", receipt: Guid.NewGuid()), null, Today, "ok", errors));
    }

    [Fact]
    public void Conversion_rounds_to_the_cent_and_the_route_follows_the_tiers_above_the_total()
    {
        Assert.Equal(442.54m, ExpenseRules.ToPolicyCurrency(120.50m, 3.6725m));
        var policy = Policy();
        Assert.Empty(ExpenseRules.TiersFor(policy, 1000m));
        Assert.Single(ExpenseRules.TiersFor(policy, 1000.01m));
        Assert.Equal(2, ExpenseRules.TiersFor(policy, 6000m).Count);
        Assert.Equal("hr.expense.base", ExpenseRules.RouteKey([]));
        Assert.Equal(ExpenseRules.RouteKey(ExpenseRules.TiersFor(policy, 2000m)), ExpenseRules.RouteKey(ExpenseRules.TiersFor(policy, 3000m)));
        Assert.NotEqual(ExpenseRules.RouteKey(ExpenseRules.TiersFor(policy, 2000m)), ExpenseRules.RouteKey(ExpenseRules.TiersFor(policy, 6000m)));
        Assert.Equal("EX-000042", ExpenseRules.Reference(42));
    }
}
