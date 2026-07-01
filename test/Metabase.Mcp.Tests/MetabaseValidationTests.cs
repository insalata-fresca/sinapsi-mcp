using Metabase.Mcp;
using Xunit;

namespace Metabase.Mcp.Tests;

// -----------------------------------------------------------------------------
// Unit tests for MetabaseValidation: every validator returns null on a good value
// and a specific human-readable reason on a bad one, and NONE of them throw. NUL
// inputs use the C# escape \0, never a literal NUL byte, so this file diffs as TEXT.
// -----------------------------------------------------------------------------

/// <summary>
/// Direct coverage of the input-validation helpers. The tool-guard tests
/// (<see cref="MetabaseToolGuardTests"/>) prove the guards FIRE before any HTTP call;
/// these pin the exact accept/reject behaviour of each validator in isolation.
/// </summary>
public sealed class MetabaseValidationTests
{
    // ── method (request escape hatch) ───────────────────────────────────────────

    [Theory]
    [InlineData("GET")]
    [InlineData("post")]
    [InlineData(" Put ")]
    [InlineData("delete")]
    public void ValidateMethod_accepts_the_four_verbs_case_insensitively(string m)
        => Assert.Null(MetabaseValidation.ValidateMethod(m));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("GET; DROP")]
    public void ValidateMethod_rejects_anything_else(string? m)
        => Assert.NotNull(MetabaseValidation.ValidateMethod(m));

    [Fact]
    public void ValidateMethod_rejects_control_chars()
        => Assert.NotNull(MetabaseValidation.ValidateMethod("GE\0T"));

    // ── path (request escape hatch) ─────────────────────────────────────────────

    [Theory]
    [InlineData("/api/dashboard/2")]
    [InlineData("api/card")]
    [InlineData("/api")]
    public void ValidatePath_accepts_relative_api_paths(string p)
        => Assert.Null(MetabaseValidation.ValidatePath(p));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/etc/passwd")]              // not an /api/ path
    [InlineData("http://evil.example/api")]  // absolute URL — would redirect the API key
    [InlineData("https://evil.example/api")]
    public void ValidatePath_rejects_non_api_or_absolute(string? p)
        => Assert.NotNull(MetabaseValidation.ValidatePath(p));

    [Fact]
    public void ValidatePath_rejects_control_chars()
        => Assert.NotNull(MetabaseValidation.ValidatePath("/api/da\nshboard"));

    [Fact]
    public void ValidatePath_rejects_oversize()
        => Assert.NotNull(MetabaseValidation.ValidatePath("/api/" + new string('x', MetabaseValidation.MaxPathLength)));

    // ── string id (collection id / 'root') ──────────────────────────────────────

    [Theory]
    [InlineData("root")]
    [InlineData("42")]
    public void ValidateStringId_accepts_good(string id)
        => Assert.Null(MetabaseValidation.ValidateStringId("id", id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateStringId_rejects_empty(string? id)
    {
        var e = MetabaseValidation.ValidateStringId("id", id);
        Assert.Equal("id is required", e);
    }

    [Fact]
    public void ValidateStringId_rejects_control_chars()
        => Assert.Contains("control characters", MetabaseValidation.ValidateStringId("id", "a\0b")!);

    // ── name ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateName_accepts_good()
        => Assert.Null(MetabaseValidation.ValidateName("name", "Quarterly revenue"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateName_rejects_empty(string? name)
        => Assert.Equal("name is required", MetabaseValidation.ValidateName("name", name));

    [Fact]
    public void ValidateName_rejects_newline()
        => Assert.Contains("control characters", MetabaseValidation.ValidateName("name", "a\nb")!);

    [Fact]
    public void ValidateName_rejects_oversize()
        => Assert.Contains("too long", MetabaseValidation.ValidateName("name", new string('x', MetabaseValidation.MaxNameLength + 1))!);

    // ── description (optional; newlines allowed) ────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A multi-line\ndescription is fine.")]
    public void ValidateDescription_accepts_optional_and_newlines(string? d)
        => Assert.Null(MetabaseValidation.ValidateDescription(d));

    [Fact]
    public void ValidateDescription_rejects_nul()
        => Assert.Contains("control characters", MetabaseValidation.ValidateDescription("bad\0desc")!);

    // ── engine / display ────────────────────────────────────────────────────────

    [Fact]
    public void ValidateEngine_accepts_postgres()
        => Assert.Null(MetabaseValidation.ValidateEngine("postgres"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateEngine_rejects_empty(string? e)
        => Assert.NotNull(MetabaseValidation.ValidateEngine(e));

    [Fact]
    public void ValidateDisplay_accepts_table()
        => Assert.Null(MetabaseValidation.ValidateDisplay("table"));

    [Fact]
    public void ValidateDisplay_rejects_control()
        => Assert.NotNull(MetabaseValidation.ValidateDisplay("ta\0ble"));

    // ── sql (newlines allowed, other control chars not) ─────────────────────────

    [Fact]
    public void ValidateSql_accepts_multiline_sql()
        => Assert.Null(MetabaseValidation.ValidateSql("SELECT *\nFROM orders\nWHERE id = 1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateSql_rejects_empty(string? sql)
        => Assert.Equal("sql is required", MetabaseValidation.ValidateSql(sql));

    [Fact]
    public void ValidateSql_rejects_nul()
        => Assert.Contains("control characters", MetabaseValidation.ValidateSql("SELECT\0 1")!);

    [Fact]
    public void ValidateSql_rejects_oversize()
        => Assert.Contains("too long", MetabaseValidation.ValidateSql(new string('x', MetabaseValidation.MaxSqlLength + 1))!);

    // ── query / models (search) ─────────────────────────────────────────────────

    [Fact]
    public void ValidateQuery_accepts_good()
        => Assert.Null(MetabaseValidation.ValidateQuery("revenue"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateQuery_rejects_empty(string? q)
        => Assert.Equal("q is required", MetabaseValidation.ValidateQuery(q));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("card,dashboard,table")]
    public void ValidateModels_accepts_optional(string? m)
        => Assert.Null(MetabaseValidation.ValidateModels(m));

    [Fact]
    public void ValidateModels_rejects_control()
        => Assert.NotNull(MetabaseValidation.ValidateModels("card\0dashboard"));

    // ── JSON body/patch ─────────────────────────────────────────────────────────

    [Fact]
    public void ValidateRequiredJson_accepts_well_formed()
        => Assert.Null(MetabaseValidation.ValidateRequiredJson("patch_json", """{"name":"x"}"""));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRequiredJson_rejects_missing(string? j)
        => Assert.Contains("required", MetabaseValidation.ValidateRequiredJson("patch_json", j)!);

    [Fact]
    public void ValidateRequiredJson_rejects_malformed()
        => Assert.Contains("not valid JSON", MetabaseValidation.ValidateRequiredJson("patch_json", "{not json")!);

    [Fact]
    public void ValidateOptionalJson_accepts_null()
        => Assert.Null(MetabaseValidation.ValidateOptionalJson("body_json", null));

    [Fact]
    public void ValidateOptionalJson_rejects_malformed_when_present()
        => Assert.Contains("not valid JSON", MetabaseValidation.ValidateOptionalJson("body_json", "}{")!);

    [Fact]
    public void ValidateOptionalJson_rejects_oversize()
        => Assert.Contains("too long",
            MetabaseValidation.ValidateOptionalJson("body_json", "\"" + new string('x', MetabaseValidation.MaxJsonLength) + "\"")!);
}
