using TeamBeats.Modules.Hr.Expenses;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 022 FR-024: what a receipt reading may suggest. Values that cannot be right are dropped, never fixed up.</summary>
public sealed class ExpenseReceiptReaderTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    [Fact]
    public void A_full_reading_is_kept_with_the_currency_in_capitals()
    {
        var s = ExpenseReceiptReader.Parse("""{"merchant":" مطعم البيك ","date":"2026-11-10","amount":42.499,"currency":"sar","tax":5.5}""", Today)!;
        Assert.Equal(new ReceiptSuggestion("مطعم البيك", new DateOnly(2026, 11, 10), 42.50m, "SAR", 5.5m), s);
    }

    [Theory]
    [InlineData("""{"merchant":null,"date":"2026-11-12","amount":-3,"currency":"riyal","tax":null}""")]
    [InlineData("""{"merchant":"","date":"10/11/2026","amount":0,"currency":"SA","tax":-1}""")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    public void Nothing_usable_is_no_suggestion(string json) => Assert.Null(ExpenseReceiptReader.Parse(json, Today));

    [Fact]
    public void Only_the_values_that_could_be_right_are_kept()
    {
        var s = ExpenseReceiptReader.Parse("""{"merchant":"Taxi\u0007 Co","date":"1999-01-01","amount":"40","currency":"AED","tax":50}""", Today)!;
        Assert.Equal(new ReceiptSuggestion("Taxi Co", null, null, "AED", 50m), s);
        var capped = ExpenseReceiptReader.Parse("""{"merchant":"Shop","amount":10,"tax":11}""", Today)!;
        Assert.Null(capped.Tax);
        Assert.Equal(200, ExpenseReceiptReader.Parse($$"""{"merchant":"{{new string('x', 300)}}"}""", Today)!.Merchant!.Length);
    }
}
