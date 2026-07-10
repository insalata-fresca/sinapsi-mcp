using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// Slug derivation tests (design §6.3 no-fabrication floor). A rename that yields no valid slug is
/// skipped, never enrolled under a fabricated name.
/// </summary>
public sealed class PersonSlugTests
{
    [Theory]
    [InlineData("Marco", "marco")]
    [InlineData("Marco.m4a", "marco")]
    [InlineData("Ada Lovelace", "ada-lovelace")]
    [InlineData("  Jean-Pierre  ", "jean-pierre")]
    [InlineData("José", "jose")]              // accent folded
    [InlineData("Anna (mum).wav", "anna-mum")]
    public void Slugifies_valid_names(string name, string expected)
    {
        Assert.True(PersonSlug.TrySlugify(name, out var slug));
        Assert.Equal(expected, slug);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData(".m4a")]
    [InlineData("---")]
    public void Rejects_names_that_slugify_empty(string? name)
    {
        Assert.False(PersonSlug.TrySlugify(name, out var slug));
        Assert.Null(slug);
    }
}
