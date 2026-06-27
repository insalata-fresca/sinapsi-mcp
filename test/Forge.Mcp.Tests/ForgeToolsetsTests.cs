using Forge.Mcp;
using Xunit;

namespace Forge.Mcp.Tests;

/// <summary>
/// FORGE_TOOLSETS is the single knob that lets one Forge.Mcp binary serve a forge that
/// lacks an optional endpoint group (e.g. a Codeberg instance without topics/actions),
/// instead of forking the tool registration. Parsing must be order-independent, tolerant
/// of whitespace, and default to the full surface.
/// </summary>
public sealed class ForgeToolsetsTests
{
    [Fact]
    public void Null_or_empty_spec_enables_everything()
    {
        var a = ForgeToolsets.Parse(null);
        var b = ForgeToolsets.Parse("");
        var c = ForgeToolsets.Parse("   ");
        foreach (var t in new[] { a, b, c })
        {
            Assert.True(t.Topics);
            Assert.True(t.Actions);
        }
    }

    [Fact]
    public void Minus_prefix_disables_the_named_group()
    {
        var t = ForgeToolsets.Parse("-topics");
        Assert.False(t.Topics);
        Assert.True(t.Actions); // unmentioned groups stay on
    }

    [Fact]
    public void Codeberg_style_spec_disables_both_optional_groups()
    {
        var t = ForgeToolsets.Parse("-topics,-actions");
        Assert.False(t.Topics);
        Assert.False(t.Actions);
    }

    [Fact]
    public void Whitespace_and_order_do_not_matter()
    {
        var t = ForgeToolsets.Parse(" -actions ,  -topics ");
        Assert.False(t.Topics);
        Assert.False(t.Actions);
    }

    [Fact]
    public void Explicit_plus_re_enables_a_group()
    {
        var t = ForgeToolsets.Parse("+topics,+actions");
        Assert.True(t.Topics);
        Assert.True(t.Actions);
    }

    [Fact]
    public void Unknown_tokens_are_ignored()
    {
        var t = ForgeToolsets.Parse("-topics,-bogus");
        Assert.False(t.Topics);
        Assert.True(t.Actions);
    }
}
