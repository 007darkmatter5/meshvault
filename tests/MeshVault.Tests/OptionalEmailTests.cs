using MeshVault.Web.Components.Account;

namespace MeshVault.Tests;

/// <summary>
/// Registration's email field is labelled optional. [EmailAddress] alone makes
/// it mandatory, because static SSR form mapping binds an untouched text box to
/// "" and that attribute rejects an empty string while skipping null.
/// </summary>
public class OptionalEmailTests
{
    private static bool IsValid(object? value) => new OptionalEmailAddressAttribute().IsValid(value);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Leaving_it_blank_is_allowed(string? given) => Assert.True(IsValid(given));

    [Theory]
    [InlineData("someone@example.com")]
    [InlineData("first.last@sub.example.co.uk")]
    public void A_real_address_is_allowed(string given) => Assert.True(IsValid(given));

    [Theory]
    [InlineData("not an address")]
    [InlineData("missing-at-sign.com")]
    [InlineData("trailing@")]
    public void A_malformed_address_is_still_rejected(string given) => Assert.False(IsValid(given));
}
