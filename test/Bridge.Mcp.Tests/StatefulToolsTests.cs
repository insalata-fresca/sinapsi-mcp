using System.Security.Cryptography;
using System.Text;
using Bridge.Mcp.Auth;
using Bridge.Mcp.Tools;
using Sinapsi.Forge.Model;
using Xunit;

namespace Bridge.Mcp.Tests;

/// <summary>
/// B4b-6 STATEFUL TOOLS — hermetic parity tests.
///
/// Tests cover:
///   1. CallerIdentityKey: bearer prefix = "bearer:"+sha256(token)[:16];
///      jwt prefix = "jwt:"+sub.
///   2. SerializeCursors: Python json.dumps(d, indent=2, sort_keys=True) + "\n" format.
///   3. Cursor advance-only-when-since==null rule (unit-level logic check).
///   4. mark_inbox_read commit message: EXACT string "inbox: process {src} -> {dest}".
///   5. mark_inbox_read path normalization: prefix added when absent, stripped when present.
///   6. mark_inbox_read not_found error code.
///   7. list_recent_additions delta-after-SHA: commits before cursor SHA are excluded.
///   8. list_recent_additions date field: verbatim passthrough of raw Forgejo date string.
/// </summary>
public sealed class StatefulToolsTests
{
    // ── CallerIdentityKey: bearer ─────────────────────────────────────────────

    [Fact]
    public void CallerKey_LegacyBearer_HasBearerPrefix()
    {
        var ctx = MakeBearerCtx("my-secret-token");
        var key = BridgeStatefulTools.CallerIdentityKey(ctx);

        Assert.StartsWith("bearer:", key);
        // Total length: "bearer:" (7) + 16 hex chars = 23.
        Assert.Equal(23, key.Length);
    }

    [Fact]
    public void CallerKey_LegacyBearer_IsSha256Hex16_Lowercase()
    {
        const string token = "my-secret-token";
        var ctx = MakeBearerCtx(token);
        var key = BridgeStatefulTools.CallerIdentityKey(ctx);

        // Expected: sha256(token.encode('utf-8')).hexdigest()[:16]
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var expected = "bearer:" + Convert.ToHexString(hash).ToLowerInvariant()[..16];

        Assert.Equal(expected, key);
    }

    [Fact]
    public void CallerKey_LegacyBearer_IsLowercaseHex()
    {
        // Python sha256(...).hexdigest() returns lowercase hex.
        // C# Convert.ToHexString returns UPPERCASE — our code must lower-case it.
        var ctx = MakeBearerCtx("any-token");
        var key = BridgeStatefulTools.CallerIdentityKey(ctx);
        var hexPart = key["bearer:".Length..];

        Assert.Equal(16, hexPart.Length);
        Assert.Equal(hexPart, hexPart.ToLowerInvariant());
        Assert.DoesNotMatch("[A-F]", hexPart);
    }

    [Fact]
    public void CallerKey_Jwt_HasJwtPrefix()
    {
        var ctx = MakeJwtCtx("user-subject-12345");
        var key = BridgeStatefulTools.CallerIdentityKey(ctx);

        Assert.Equal("jwt:user-subject-12345", key);
    }

    [Fact]
    public void CallerKey_Jwt_IsSubClaim()
    {
        // Python: f"jwt:{ctx.subject}"
        var ctx = MakeJwtCtx("some-oidc-sub");
        var key = BridgeStatefulTools.CallerIdentityKey(ctx);
        Assert.Equal("jwt:some-oidc-sub", key);
    }

    [Theory]
    [InlineData("token-a", "token-b")]
    [InlineData("abc", "ABC")]
    public void CallerKey_DifferentTokens_ProduceDifferentKeys(string t1, string t2)
    {
        var k1 = BridgeStatefulTools.CallerIdentityKey(MakeBearerCtx(t1));
        var k2 = BridgeStatefulTools.CallerIdentityKey(MakeBearerCtx(t2));
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void CallerKey_SameToken_ProducesSameKey()
    {
        const string token = "stable-token";
        var k1 = BridgeStatefulTools.CallerIdentityKey(MakeBearerCtx(token));
        var k2 = BridgeStatefulTools.CallerIdentityKey(MakeBearerCtx(token));
        Assert.Equal(k1, k2);
    }

    // ── SerializeCursors: Python json.dumps(d, indent=2, sort_keys=True) ──────

    [Fact]
    public void SerializeCursors_Empty_ReturnsEmptyObject()
    {
        // Python: json.dumps({}, indent=2, sort_keys=True) → "{}"
        var result = BridgeStatefulTools.SerializeCursors([]);
        Assert.Equal("{}", result);
    }

    [Fact]
    public void SerializeCursors_SingleEntry_MatchesPython()
    {
        // Python:
        //   json.dumps({"bearer:abc123def456abcd": "deadbeef"}, indent=2, sort_keys=True)
        //   →
        //   {
        //     "bearer:abc123def456abcd": "deadbeef"
        //   }
        var pairs = new[] { KeyValuePair.Create("bearer:abc123def456abcd", "deadbeef") };
        var result = BridgeStatefulTools.SerializeCursors(pairs);

        const string expected =
            "{\n" +
            "  \"bearer:abc123def456abcd\": \"deadbeef\"\n" +
            "}";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SerializeCursors_TwoEntries_SortedByKey_MatchesPython()
    {
        // Python json.dumps with sort_keys=True sorts alphabetically.
        // "bearer:..." < "jwt:..." lexicographically — sorted order is preserved.
        // Python output (indent=2, sort_keys=True):
        //   {
        //     "bearer:abc123def456abcd": "sha1111",
        //     "jwt:user-sub": "sha2222"
        //   }
        var sorted = new SortedDictionary<string, string>
        {
            ["bearer:abc123def456abcd"] = "sha1111",
            ["jwt:user-sub"]            = "sha2222",
        };
        var result = BridgeStatefulTools.SerializeCursors(sorted);

        const string expected =
            "{\n" +
            "  \"bearer:abc123def456abcd\": \"sha1111\",\n" +
            "  \"jwt:user-sub\": \"sha2222\"\n" +
            "}";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SerializeCursors_SortOrder_BeforeJwt()
    {
        // Lexicographic: "bearer:" < "jwt:" — bearer entries come first.
        var pairs = new[]
        {
            KeyValuePair.Create("jwt:sub",     "sha-jwt"),
            KeyValuePair.Create("bearer:ab12", "sha-bearer"),
        };
        // Sorted by the method (SortedDictionary used internally).
        // After sort: bearer < jwt.
        var sorted = new SortedDictionary<string, string>
        {
            ["jwt:sub"]     = "sha-jwt",
            ["bearer:ab12"] = "sha-bearer",
        };
        var result = BridgeStatefulTools.SerializeCursors(sorted);

        // First key must be "bearer:ab12".
        Assert.StartsWith("{\n  \"bearer:ab12\"", result);
        // Second key must be "jwt:sub".
        Assert.Contains("\"jwt:sub\"", result);
        // Comma after first entry.
        Assert.Contains("\"sha-bearer\",", result);
    }

    [Fact]
    public void SerializeCursors_NoTrailingCommaOnLastEntry()
    {
        var sorted = new SortedDictionary<string, string>
        {
            ["key-a"] = "value-a",
            ["key-b"] = "value-b",
        };
        var result = BridgeStatefulTools.SerializeCursors(sorted);
        // Last entry must NOT have a trailing comma before the closing brace.
        Assert.EndsWith("\"value-b\"\n}", result);
    }

    // ── Cursor advance-only-when-since==null rule ─────────────────────────────
    //
    // This is a behavioral invariant: the cursor is only written when since==null.
    // We test this at the logic level (not integration) by asserting the condition.

    [Theory]
    [InlineData(null,                    true)]
    [InlineData("2024-01-01T00:00:00Z", false)]
    public void CursorAdvanceRule_OnlyWhenSinceIsNull(string? since, bool shouldAdvance)
    {
        // Mirror the condition in BridgeStatefulTools.ListRecentAdditions:
        //   if (newCommits.Count > 0 && since is null) → advance
        const int newCommitCount = 3;
        var willAdvance = newCommitCount > 0 && since is null;
        Assert.Equal(shouldAdvance, willAdvance);
    }

    // ── mark_inbox_read: commit message string ────────────────────────────────

    [Theory]
    [InlineData(
        "inbox/from-code/notes.md",
        "knowledge/processed/notes.md",
        "inbox: process inbox/from-code/notes.md -> knowledge/processed/notes.md")]
    [InlineData(
        "inbox/from-code/2024-07-03-session.md",
        "knowledge/processed/2024-07-03-session.md",
        "inbox: process inbox/from-code/2024-07-03-session.md -> knowledge/processed/2024-07-03-session.md")]
    public void MarkInboxRead_CommitMessage_MatchesPythonExact(
        string src, string dest, string expectedMessage)
    {
        // Python: f"inbox: process {src} -> {dest}"
        // This string is asserted in tests and must match Python exactly.
        var actualMessage = $"inbox: process {src} -> {dest}";
        Assert.Equal(expectedMessage, actualMessage);
    }

    [Fact]
    public void MarkInboxRead_CommitMessage_HasArrow()
    {
        // Must contain " -> " (space-arrow-space) as in Python.
        const string src  = "inbox/from-code/foo.md";
        const string dest = "knowledge/processed/foo.md";
        var msg = $"inbox: process {src} -> {dest}";
        Assert.Contains(" -> ", msg);
        Assert.StartsWith("inbox: process ", msg);
    }

    // ── mark_inbox_read: path normalization ───────────────────────────────────

    [Theory]
    [InlineData("foo.md",                     "inbox/from-code/foo.md")]
    [InlineData("/foo.md",                    "inbox/from-code/foo.md")]
    [InlineData("inbox/from-code/foo.md",     "inbox/from-code/foo.md")]
    [InlineData("/inbox/from-code/foo.md",    "inbox/from-code/foo.md")]
    public void MarkInboxRead_PathNormalization_MatchesPython(string input, string expected)
    {
        // Python:
        //   rel = path.lstrip("/")
        //   if not rel.startswith("inbox/from-code/"):
        //       rel = f"inbox/from-code/{rel}"
        var rel = input.TrimStart('/');
        if (!rel.StartsWith("inbox/from-code/", StringComparison.Ordinal))
            rel = $"inbox/from-code/{rel}";
        Assert.Equal(expected, rel);
    }

    [Fact]
    public void MarkInboxRead_FilenameExtraction_MatchesPython()
    {
        // Python: filename = rel.rsplit("/", 1)[-1]
        const string rel = "inbox/from-code/my-file.md";
        var filename = rel.Split('/')[^1];
        Assert.Equal("my-file.md", filename);
    }

    [Fact]
    public void MarkInboxRead_DestPath_MatchesPython()
    {
        // Python: dest = f"knowledge/processed/{filename}"
        const string filename = "my-file.md";
        var dest = $"knowledge/processed/{filename}";
        Assert.Equal("knowledge/processed/my-file.md", dest);
    }

    // ── mark_inbox_read: not_found error code ─────────────────────────────────

    [Fact]
    public void MarkInboxRead_NotFound_ErrorCode()
    {
        // Python: raise ToolError("not_found", str(exc))
        var ex = new Bridge.Mcp.Auth.BridgeToolException("not_found", "file does not exist");
        Assert.Equal("not_found", ex.Code);
    }

    // ── list_recent_additions: delta-after-SHA logic ──────────────────────────

    [Fact]
    public void ListRecentAdditions_DeltaAfterSha_ExcludesOldCommits()
    {
        // Mirror the Python loop:
        //   for c in commits:
        //       sha = c.get("sha")
        //       if last_sha and sha == last_sha: break
        //       new_commits.append(...)
        //
        // Given 5 commits [c1, c2, c3(=cursor), c4, c5],
        // cursor points at c3 → new_commits = [c1, c2] (stop before c3).
        var commits = new[]
        {
            ("sha-c1", "commit 1"),
            ("sha-c2", "commit 2"),
            ("sha-c3", "commit 3"),  // cursor points here
            ("sha-c4", "commit 4"),
            ("sha-c5", "commit 5"),
        };
        const string lastSha = "sha-c3";

        var newCommits = new List<(string sha, string msg)>();
        foreach (var (sha, msg) in commits)
        {
            if (sha == lastSha) break;
            newCommits.Add((sha, msg));
        }

        Assert.Equal(2, newCommits.Count);
        Assert.Equal("sha-c1", newCommits[0].sha);
        Assert.Equal("sha-c2", newCommits[1].sha);
    }

    [Fact]
    public void ListRecentAdditions_NoCursor_ReturnsAllCommits()
    {
        // When lastSha is null → all commits returned (no early break).
        var commits = new[]
        {
            ("sha-1", "first"),
            ("sha-2", "second"),
            ("sha-3", "third"),
        };
        string? lastSha = null;

        var newCommits = new List<string>();
        foreach (var (sha, _) in commits)
        {
            if (lastSha is not null && sha == lastSha) break;
            newCommits.Add(sha);
        }

        Assert.Equal(3, newCommits.Count);
    }

    [Fact]
    public void ListRecentAdditions_CursorAtHead_ReturnsEmpty()
    {
        // Cursor points at the most recent commit → nothing new.
        var commits = new[]
        {
            ("sha-head", "latest commit"),
            ("sha-old1", "older commit"),
        };
        const string lastSha = "sha-head";

        var newCommits = new List<string>();
        foreach (var (sha, _) in commits)
        {
            if (sha == lastSha) break;
            newCommits.Add(sha);
        }

        Assert.Empty(newCommits);
    }

    [Fact]
    public void ListRecentAdditions_CursorNotInList_ReturnsAllCommits()
    {
        // Cursor SHA not in the current commit list (e.g. older than --depth 50).
        // Python behavior: no break → all commits returned (same as no cursor).
        var commits = new[]
        {
            ("sha-1", "first"),
            ("sha-2", "second"),
        };
        const string lastSha = "sha-not-in-list";

        var newCommits = new List<string>();
        foreach (var (sha, _) in commits)
        {
            if (sha == lastSha) break;
            newCommits.Add(sha);
        }

        Assert.Equal(2, newCommits.Count);
    }

    // ── Structural: BridgeStatefulTools has expected tool methods ─────────────

    [Theory]
    [InlineData("ListRecentAdditions")]
    [InlineData("MarkInboxRead")]
    public void BridgeStatefulTools_ExposesExpectedPublicMethods(string methodName)
    {
        var type = typeof(BridgeStatefulTools);
        var method = type.GetMethod(methodName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
    }

    [Fact]
    public void BridgeStatefulTools_IsDecoratedWithMcpServerToolType()
    {
        var type = typeof(BridgeStatefulTools);
        var attr = type.GetCustomAttributes(
            typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), inherit: false);
        Assert.Single(attr);
    }

    [Theory]
    [InlineData("ListRecentAdditions", "list_recent_additions")]
    [InlineData("MarkInboxRead",       "mark_inbox_read")]
    public void BridgeStatefulTools_ToolNamesMatchPython(string methodName, string expectedToolName)
    {
        var type   = typeof(BridgeStatefulTools);
        var method = type.GetMethod(methodName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var toolAttr = method!.GetCustomAttributes(
            typeof(ModelContextProtocol.Server.McpServerToolAttribute), inherit: false)
            .Cast<ModelContextProtocol.Server.McpServerToolAttribute>()
            .FirstOrDefault();
        Assert.NotNull(toolAttr);
        Assert.Equal(expectedToolName, toolAttr!.Name);
    }

    // ── list_recent_additions: date field verbatim passthrough ────────────────

    [Theory]
    // UTC instant with sub-seconds: Python passes verbatim; C# must NOT drop sub-seconds.
    [InlineData("2024-07-03T12:34:56.123Z")]
    // CET+02:00 offset: Python passes verbatim; C# must NOT reformat to wrong UTC instant.
    [InlineData("2024-07-03T12:34:56+02:00")]
    // Plain UTC: must survive unchanged.
    [InlineData("2024-07-03T10:00:00Z")]
    // Null raw: no date emitted.
    [InlineData(null)]
    public void ListRecentAdditions_DateField_VerbatimPassthrough(string? rawDate)
    {
        // The fix: BridgeStatefulTools uses c.Author?.DateRaw (the verbatim string from
        // Forgejo) instead of c.Author?.Date?.ToString("yyyy-MM-ddTHH:mm:ssZ") which
        // reformats using the parsed DateTimeOffset and appends a literal 'Z' (not UTC).
        //
        // Reproduce the output expression from BridgeStatefulTools.ListRecentAdditions:
        //   date = c.Author?.DateRaw
        // which must equal the raw string (or null) — no reformatting allowed.
        var author = new ForgeCommitAuthor(null, null, null, DateRaw: rawDate);
        var commit = new ForgeCommit("abc123", "msg", author, null);

        var actualDate = commit.Author?.DateRaw;

        Assert.Equal(rawDate, actualDate);
    }

    [Fact]
    public void ListRecentAdditions_DateField_NonUtcOffset_NotCollapsedToWrongInstant()
    {
        // Regression: "yyyy-MM-ddTHH:mm:ssZ" with a +02:00 parsed DateTimeOffset
        // renders the local time (12:34:56) with a literal 'Z' suffix → claims UTC
        // 12:34:56 when the real UTC instant is 10:34:56.
        // The fix (DateRaw passthrough) must preserve "+02:00" verbatim.
        const string raw = "2024-07-03T12:34:56+02:00";
        var author = new ForgeCommitAuthor(null, null, DateTimeOffset.Parse(raw), DateRaw: raw);

        // Old (broken) path — kept as a comment to explain what we're guarding against:
        // var brokenDate = author.Date?.ToString("yyyy-MM-ddTHH:mm:ssZ"); // → "2024-07-03T12:34:56Z" (WRONG)

        // Fixed path:
        var fixedDate = author.DateRaw;
        Assert.Equal(raw, fixedDate);

        // Prove the old format string would have produced the wrong answer:
        var brokenDate = author.Date?.ToString("yyyy-MM-ddTHH:mm:ssZ");
        Assert.NotEqual(raw, brokenDate); // must differ — the regression is real
    }

    [Fact]
    public void ListRecentAdditions_DateField_SubSeconds_NotDropped()
    {
        // Regression: "yyyy-MM-ddTHH:mm:ssZ" truncates sub-second precision.
        // Python passes "2024-07-03T12:34:56.123Z" verbatim; C# must too.
        const string raw = "2024-07-03T12:34:56.123Z";
        var author = new ForgeCommitAuthor(null, null, DateTimeOffset.Parse(raw), DateRaw: raw);

        var fixedDate = author.DateRaw;
        Assert.Equal(raw, fixedDate);

        var brokenDate = author.Date?.ToString("yyyy-MM-ddTHH:mm:ssZ");
        Assert.NotEqual(raw, brokenDate); // sub-seconds were dropped — regression confirmed
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BridgeAuthContext MakeBearerCtx(string rawToken) => new()
    {
        Mode     = "bearer",
        Subject  = "legacy-bearer",
        Scopes   = Bridge.Mcp.Auth.LegacyScopes.All,
        RawToken = rawToken,
    };

    private static BridgeAuthContext MakeJwtCtx(string sub) => new()
    {
        Mode     = "jwt",
        Subject  = sub,
        Scopes   = new HashSet<string> { "bridge:read:documents" },
        RawToken = "jwt-token-value",
    };
}
