using System.Globalization;
using System.Text.RegularExpressions;

namespace TeamBeats.Modules.Hr.Expenses;

public static class ExpenseKinds
{
    public const string Amount = "amount";
    public const string PerDiem = "per_diem";
    public const string Mileage = "mileage";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Amount, PerDiem, Mileage };
}

/// <summary>A limit of one category: for one grade, or for every grade without its own limit (<see cref="Grade"/> null).</summary>
public sealed record ExpenseLimit(string? Grade, decimal Amount);

/// <summary>
/// An expense category. An <c>amount</c> line is what the receipt says, in any currency; a <c>per_diem</c> line is days
/// times <see cref="Rate"/> and a <c>mileage</c> line is kilometres times <see cref="Rate"/>, both in the policy currency.
/// </summary>
public sealed record ExpenseCategory(string Key, Dictionary<string, string> Names, string Kind, bool Receipt, decimal? Rate = null, List<ExpenseLimit>? Limits = null);

/// <summary>A claim whose total is above <see cref="Above"/> also needs one of <see cref="Approvers"/> (US8 scenario 5).</summary>
public sealed record ExpenseTier(decimal Above, List<Guid> Approvers);

/// <summary>
/// The tenant's expense policy (FR-023), set by finance. Without a <see cref="Currency"/> no claim can be sent. An empty
/// <see cref="CostCentres"/> list lets people type a cost centre; a list makes them pick one.
/// </summary>
public sealed record ExpensePolicyDocument(string? Currency, int WindowDays, List<ExpenseCategory> Categories, List<ExpenseTier> Tiers, List<string> CostCentres);

/// <summary>One claim line as the person sends it.</summary>
public sealed record ExpenseLineInput(
    string? Category, DateOnly? Date, decimal? Amount, string? Currency, string? Merchant, decimal? Tax, string? Description, Guid? ReceiptId, int? Days, decimal? Km,
    string? Justification);

/// <summary>A line after its own checks: the amount and currency to convert, and the category limit for the person's grade.</summary>
public sealed record PricedLine(ExpenseCategory Category, DateOnly Date, decimal Amount, string Currency, string? Merchant, decimal? Tax, string? Description, Guid? ReceiptId,
    int? Days, decimal? Km, decimal? Limit, string? Justification);

/// <summary>Pure expense rules (spec 022 FR-023 and section 6): the policy shape, each line's checks, limits and the route.</summary>
public static partial class ExpenseRules
{
    public const int MaxCategories = 30;
    public const int MaxLimits = 20;
    public const int MaxTiers = 3;
    public const int MaxTierApprovers = 10;
    public const int MaxCostCentres = 200;
    public const int MaxLines = 50;
    public const int MaxText = 200;
    public const int MaxJustification = 500;
    public const decimal MaxAmount = 1_000_000m;
    public const int MaxDays = 31;
    public const decimal MaxKm = 5000m;
    public const int DefaultWindowDays = 60;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    public static bool IsCurrency(string? value) => value is not null && CurrencyPattern().IsMatch(value);

    private static Dictionary<string, string> Names(string en, string ar) => new(StringComparer.Ordinal) { ["en"] = en, ["ar"] = ar };

    /// <summary>The policy before finance sets one: five receipt categories, no limits, no tiers and no currency yet.</summary>
    public static ExpensePolicyDocument Default() => new(null, DefaultWindowDays,
    [
        new("travel", Names("Travel", "السفر"), ExpenseKinds.Amount, true),
        new("accommodation", Names("Accommodation", "الإقامة"), ExpenseKinds.Amount, true),
        new("meals", Names("Meals", "الوجبات"), ExpenseKinds.Amount, true),
        new("transport", Names("Local transport", "المواصلات المحلية"), ExpenseKinds.Amount, true),
        new("other", Names("Other", "أخرى"), ExpenseKinds.Amount, true),
    ], [], []);

    private static bool PlainText(string? value, int max) => value is not null && value.Length is > 0 && value.Length <= max && !value.Any(char.IsControl);

    /// <summary>The problems of a policy finance wants to save, as field keys and resource keys.</summary>
    public static List<(string Field, string Key)> Validate(ExpensePolicyDocument? policy)
    {
        var errors = new List<(string, string)>();
        if (policy is null)
        {
            errors.Add(("policy", "validation.expenses.policy"));
            return errors;
        }
        if (!IsCurrency(policy.Currency))
        {
            errors.Add(("currency", "validation.expenses.currency"));
        }
        if (policy.WindowDays is < 1 or > 365)
        {
            errors.Add(("windowDays", "validation.expenses.window"));
        }
        if (policy.Categories is not { Count: > 0 } categories || categories.Count > MaxCategories)
        {
            errors.Add(("categories", "validation.expenses.categories"));
        }
        else
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < categories.Count; i++)
            {
                var c = categories[i];
                var field = $"categories[{i}]";
                if (c is null)
                {
                    errors.Add((field, "validation.expenses.categories"));
                    continue;
                }
                if (c.Key is null || !KeyPattern().IsMatch(c.Key) || !keys.Add(c.Key))
                {
                    errors.Add(($"{field}.key", "validation.expenses.category_key"));
                }
                if (c.Names is null || !TeamBeats.Primitives.Locales.LaunchComplete.All(l => PlainText(c.Names.GetValueOrDefault(l)?.Trim(), 80)) || c.Names.Count > 5)
                {
                    errors.Add(($"{field}.names", "validation.expenses.category_names"));
                }
                if (c.Kind is null || !ExpenseKinds.All.Contains(c.Kind))
                {
                    errors.Add(($"{field}.kind", "validation.expenses.category_kind"));
                }
                else if ((c.Kind == ExpenseKinds.Amount) != (c.Rate is null) || c.Rate is <= 0 or > MaxAmount)
                {
                    errors.Add(($"{field}.rate", "validation.expenses.category_rate"));
                }
                if (c.Limits is { } limits && (limits.Count > MaxLimits || limits.Any(l => l is null || l.Amount is <= 0 or > MaxAmount
                        || (l.Grade is not null && !PlainText(l.Grade, 50)))
                    || limits.Select(l => l?.Grade?.ToUpperInvariant()).Distinct().Count() != limits.Count))
                {
                    errors.Add(($"{field}.limits", "validation.expenses.category_limits"));
                }
            }
        }
        if (policy.Tiers is not { } tiers || tiers.Count > MaxTiers || tiers.Any(t => t is null || t.Above is <= 0 or > MaxAmount
                || t.Approvers is not { Count: > 0 and <= MaxTierApprovers } || t.Approvers.Any(a => a == Guid.Empty) || t.Approvers.Distinct().Count() != t.Approvers.Count)
            || tiers.Zip(tiers.Skip(1)).Any(p => p.Second.Above <= p.First.Above))
        {
            errors.Add(("tiers", "validation.expenses.tiers"));
        }
        if (policy.CostCentres is not { } centres || centres.Count > MaxCostCentres || centres.Any(c => !PlainText(c?.Trim(), 50))
            || centres.Distinct(StringComparer.Ordinal).Count() != centres.Count)
        {
            errors.Add(("costCentres", "validation.expenses.cost_centres"));
        }
        return errors;
    }

    /// <summary>The policy as stored: names and grades trimmed, empty limit lists dropped.</summary>
    public static ExpensePolicyDocument Clean(ExpensePolicyDocument p) => p with
    {
        Categories = p.Categories.Select(c => c with
        {
            Names = c.Names.ToDictionary(n => n.Key, n => n.Value.Trim(), StringComparer.Ordinal),
            Limits = c.Limits is { Count: > 0 } l ? l.Select(x => x with { Grade = x.Grade?.Trim() }).ToList() : null,
        }).ToList(),
        CostCentres = p.CostCentres.Select(c => c.Trim()).ToList(),
    };

    /// <summary>The limit for a grade in a category: the grade's own, else the one for every grade, else none.</summary>
    public static decimal? LimitFor(ExpenseCategory category, string? grade) =>
        category.Limits?.FirstOrDefault(l => l.Grade is not null && grade is not null && string.Equals(l.Grade, grade, StringComparison.OrdinalIgnoreCase))?.Amount
        ?? category.Limits?.FirstOrDefault(l => l.Grade is null)?.Amount;

    /// <summary>
    /// Checks one line on its own: the category, a date inside the policy window and not in the future, an amount and
    /// currency (or days or kilometres for per diem and mileage, priced at the category rate), plain text fields and a
    /// receipt where the category asks for one. Errors use <paramref name="field"/> as the prefix.
    /// </summary>
    public static PricedLine? Check(ExpensePolicyDocument policy, ExpenseLineInput? input, string? grade, DateOnly today, string field, List<(string Field, string Key)> errors)
    {
        if (input is null)
        {
            errors.Add((field, "validation.expenses.line"));
            return null;
        }
        var start = errors.Count;
        var category = policy.Categories.FirstOrDefault(c => c.Key == input.Category);
        if (category is null)
        {
            errors.Add(($"{field}.category", "validation.expenses.category"));
            return null;
        }
        if (input.Date is not { } date || date > today)
        {
            errors.Add(($"{field}.date", "validation.expenses.date"));
        }
        else if (date < today.AddDays(-policy.WindowDays))
        {
            errors.Add(($"{field}.date", "validation.expenses.too_old"));
        }
        decimal amount = 0;
        var currency = policy.Currency!;
        int? days = null;
        decimal? km = null;
        switch (category.Kind)
        {
            case ExpenseKinds.PerDiem:
                if (input.Days is not { } d || d is < 1 or > MaxDays)
                {
                    errors.Add(($"{field}.days", "validation.expenses.days"));
                }
                else
                {
                    days = d;
                    amount = decimal.Round(d * category.Rate!.Value, 2, MidpointRounding.AwayFromZero);
                }
                break;
            case ExpenseKinds.Mileage:
                if (input.Km is not { } k || k <= 0 || k > MaxKm || decimal.Round(k, 1) != k)
                {
                    errors.Add(($"{field}.km", "validation.expenses.km"));
                }
                else
                {
                    km = k;
                    amount = decimal.Round(k * category.Rate!.Value, 2, MidpointRounding.AwayFromZero);
                }
                break;
            default:
                if (input.Amount is not { } a || a <= 0 || a > MaxAmount || decimal.Round(a, 2) != a)
                {
                    errors.Add(($"{field}.amount", "validation.expenses.amount"));
                }
                else
                {
                    amount = a;
                }
                var given = input.Currency?.Trim().ToUpperInvariant();
                if (!IsCurrency(given))
                {
                    errors.Add(($"{field}.currency", "validation.expenses.currency"));
                }
                else
                {
                    currency = given!;
                }
                if (input.Tax is { } tax && (tax < 0 || tax > (input.Amount ?? 0) || decimal.Round(tax, 2) != tax))
                {
                    errors.Add(($"{field}.tax", "validation.expenses.tax"));
                }
                if (category.Receipt && input.ReceiptId is null)
                {
                    errors.Add(($"{field}.receiptId", "validation.expenses.receipt_required"));
                }
                break;
        }
        var merchant = Optional(input.Merchant, MaxText, $"{field}.merchant", errors);
        var description = Optional(input.Description, MaxText, $"{field}.description", errors);
        var justification = Optional(input.Justification, MaxJustification, $"{field}.justification", errors, allowNewLines: true);
        if (errors.Count > start)
        {
            return null;
        }
        return new PricedLine(category, input.Date!.Value, amount, currency, merchant, category.Kind == ExpenseKinds.Amount ? input.Tax : null, description,
            category.Kind == ExpenseKinds.Amount ? input.ReceiptId : null, days, km, LimitFor(category, grade), justification);
    }

    private static string? Optional(string? value, int max, string field, List<(string Field, string Key)> errors, bool allowNewLines = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var text = value.Trim();
        if (text.Length > max || text.Any(c => char.IsControl(c) && !(allowNewLines && c == '\n')))
        {
            errors.Add((field, "validation.expenses.text"));
            return null;
        }
        return text;
    }

    /// <summary>The line in the policy currency at <paramref name="rate"/> (policy currency units for one unit), to the cent.</summary>
    public static decimal ToPolicyCurrency(decimal amount, decimal rate) => decimal.Round(amount * rate, 2, MidpointRounding.AwayFromZero);

    /// <summary>The tiers a total needs, in order (US8 scenario 5: a claim above a tier's amount also needs one of its approvers).</summary>
    public static IReadOnlyList<ExpenseTier> TiersFor(ExpensePolicyDocument policy, decimal total) => policy.Tiers.Where(t => total > t.Above).ToList();

    /// <summary>A stable key for one route shape, so claims with the same steps share a published definition.</summary>
    public static string RouteKey(IReadOnlyList<ExpenseTier> tiers)
    {
        if (tiers.Count == 0)
        {
            return "hr.expense.base";
        }
        var shape = string.Join('|', tiers.Select(t => string.Join(',', t.Approvers.Order())));
        var hash = System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(shape)))[..16];
        return $"hr.expense.{hash}";
    }

    public static string Reference(int number) => "EX-" + number.ToString("D6", CultureInfo.InvariantCulture);
}
