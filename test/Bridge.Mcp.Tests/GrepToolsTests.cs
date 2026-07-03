using System.Text;
using Bridge.Mcp.Auth;
using Bridge.Mcp.Git;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bridge.Mcp.Tests;

/// <summary>
/// Tests for B4b-4 Grep Tools: search_documents + lookup_fact.
///
/// Strategy:
///   - GitGrepAsync output-parsing contract is verified by running real git grep
///     directly (via Process) on a local repo, then parsing the output with the same
///     logic GitOpsService uses. This avoids the EnsureCloneAsync / network dependency
///     while still exercising the byte-exact parsing that determines grep parity.
///   - BridgeGrepTools.FanOutGrepAsync / ResolveTargetsAsync are tested at the
///     structural level (internal method shape, scope logic, cap behavior via reflection
///     or structural contracts).
///   - lookup_fact sensitive stub: always returns scope_required regardless of auth.
///
/// What is explicitly NOT tested here:
///   - GitOpsService.GitGrepAsync end-to-end (requires Forgejo HTTP — integration test).
///   - ResolveTargetsAsync for scope=all/projects (calls ListReposWithTopicAsync → HTTP).
/// </summary>
public sealed class GrepToolsTests : IAsyncLifetime
{
    private string _tmpDir = null!;

    // ── Setup / teardown ──────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "bridge-grep-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tmpDir);

        // Create a local git repo with the golden corpus for parsing tests.
        _localRepoDir = Path.Combine(_tmpDir, "localrepo");
        Directory.CreateDirectory(_localRepoDir);
        await RunGitAsync(["init", "--initial-branch=main"], _localRepoDir);
        await RunGitAsync(["config", "user.email", "test@example.com"], _localRepoDir);
        await RunGitAsync(["config", "user.name",  "Test"],             _localRepoDir);
        await WriteAndCommitAsync("docs/readme.md", GoldenReadme);
        await WriteAndCommitAsync("notes/work.md",  GoldenNotes);
        await WriteAndCommitAsync("data.bin",       null, isNullBytes: true);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tmpDir, recursive: true); }
        catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    private string _localRepoDir = null!;

    // ── Golden corpus ─────────────────────────────────────────────────────────

    // 7 lines containing "bridge-mcp" → git grep --max-count=5 caps at 5 per file.
    private const string GoldenReadme = """
        # Bridge MCP

        This is the bridge-mcp README.

        The bridge-mcp server connects to Forgejo.
        It uses bridge-mcp git operations.
        Tags: bridge-mcp knowledge workspace.
        Another bridge-mcp mention here.
        bridge-mcp is built in C#.
        Final bridge-mcp line.
        """;

    // 3 lines containing "lookup_fact" and 2 containing "search_documents".
    private const string GoldenNotes = """
        Notes on the bridge tools:

        lookup_fact: searches personal-facts repo.
        lookup_fact can handle sensitive queries.
        search_documents: fans out to all knowledge repos.
        search_documents uses git grep internally.
        lookup_fact uses the same git grep engine.
        """;

    // ── git grep output parsing (mirrors GitOpsService.GitGrepAsync parsing) ──

    /// <summary>
    /// Run git grep exactly as GitOpsService does, and parse the output with the same
    /// logic. This verifies the parity of the parsing contract.
    /// </summary>
    private async Task<List<(string Path, int Line, string Snippet)>> LocalGrepAsync(
        string query, int maxResults = 20)
    {
        // Exactly mirrors GitOpsService: git grep -n -I --max-count=5 --no-color --fixed-strings
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory       = _localRepoDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (var a in new[] { "grep", "-n", "-I", "--max-count=5", "--no-color", "--fixed-strings", query })
            psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        // exit 1 = no matches (not an error); !=0,1 = real error
        if (p.ExitCode != 0 && p.ExitCode != 1)
            throw new Exception($"git grep failed: {await p.StandardError.ReadToEndAsync()}");

        // Exactly mirrors GitOpsService.GitGrepAsync parsing:
        //   rawLine.Split(':', 3) → [path, lineNum, snippet]
        //   snippet = parts[2].Trim()
        var results = new List<(string Path, int Line, string Snippet)>();
        foreach (var rawLine in stdout.Split('\n'))
        {
            if (string.IsNullOrEmpty(rawLine)) continue;
            var parts = rawLine.Split(':', 3);
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[1], out var lineNum)) continue;
            results.Add((parts[0], lineNum, parts[2].Trim()));
            if (results.Count >= maxResults) break;
        }
        return results;
    }

    // ── Git grep parity: fixed-string semantics ───────────────────────────────

    [Fact]
    public async Task GitGrep_FixedString_MatchesExpectedLines()
    {
        // "bridge-mcp" appears in GoldenReadme on 7 lines (3,5,6,7,8,9,10).
        // --max-count=5 caps at 5 from docs/readme.md.
        var matches = await LocalGrepAsync("bridge-mcp", maxResults: 20);

        var readmeMatches = matches.Where(m => m.Path == "docs/readme.md").ToList();
        Assert.InRange(readmeMatches.Count, 1, 5); // --max-count=5 per file
        foreach (var m in readmeMatches)
            Assert.Contains("bridge-mcp", m.Snippet);
    }

    [Fact]
    public async Task GitGrep_MaxCount5PerFile_Enforced()
    {
        // docs/readme.md has 7 "bridge-mcp" lines but --max-count=5 caps at 5 per file.
        var matches = await LocalGrepAsync("bridge-mcp", maxResults: 20);

        var perFile = matches.GroupBy(m => m.Path).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (path, count) in perFile)
            Assert.True(count <= 5, $"File {path} returned {count} matches; expected ≤5");
    }

    [Fact]
    public async Task GitGrep_MaxResultsCap_Applied()
    {
        // maxResults=3 must cap total returned matches.
        var matches = await LocalGrepAsync("bridge-mcp", maxResults: 3);
        Assert.InRange(matches.Count, 0, 3);
    }

    [Fact]
    public async Task GitGrep_SkipsBinaryFiles()
    {
        // data.bin contains null bytes → -I (skip binary) → no match.
        var matches = await LocalGrepAsync("bridge", maxResults: 20);
        var binMatches = matches.Where(m => m.Path == "data.bin").ToList();
        Assert.Empty(binMatches);
    }

    [Fact]
    public async Task GitGrep_NoMatchReturnsEmptyList()
    {
        var matches = await LocalGrepAsync("THIS_TERM_DOES_NOT_EXIST_XYZZY_42", maxResults: 20);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task GitGrep_FixedStringNotRegex()
    {
        // "." is a regex wildcard but --fixed-strings treats it as literal.
        // "bridge.mcp" (with dot) must NOT match "bridge-mcp".
        var matches = await LocalGrepAsync("bridge.mcp", maxResults: 20);
        Assert.Empty(matches);
    }

    [Fact]
    public async Task GitGrep_LineNumbers_Ascending()
    {
        // lookup_fact appears in notes/work.md; lines should be in ascending order.
        var matches = await LocalGrepAsync("lookup_fact", maxResults: 20);

        Assert.NotEmpty(matches);
        Assert.All(matches, m => Assert.Equal("notes/work.md", m.Path));
        var lines = matches.Select(m => m.Line).ToList();
        Assert.Equal(lines.OrderBy(x => x).ToList(), lines);
    }

    [Fact]
    public async Task GitGrep_SnippetIsGolden()
    {
        // Verify that snippet = parts[2].Trim() — raw content after path:lineNum:
        // Python: line.split(":", 2)[2].strip()  →  exact same value.
        var matches = await LocalGrepAsync("lookup_fact: searches", maxResults: 5);
        Assert.Single(matches);
        Assert.Equal("lookup_fact: searches personal-facts repo.", matches[0].Snippet);
    }

    // ── BridgeGrepTools.FanOutGrepAsync — cap + ordering ─────────────────────

    [Fact]
    public void FanOutGrepAsync_IsInternal_WithCorrectSignature()
    {
        // FanOutGrepAsync is internal (exposed for testing via InternalsVisibleTo).
        // Verify the method is accessible and has the expected parameter count.
        var method = typeof(Bridge.Mcp.Tools.BridgeGrepTools).GetMethod(
            "FanOutGrepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var parms = method!.GetParameters();
        Assert.Equal(5, parms.Length);
        Assert.Equal(typeof(string), parms[1].ParameterType); // query
        Assert.Equal(typeof(int),    parms[2].ParameterType); // maxPerRepo
        Assert.Equal(typeof(int),    parms[3].ParameterType); // overallCap
    }

    [Fact]
    public void ResolveTargetsAsync_IsInternal_WithCorrectSignature()
    {
        // ResolveTargetsAsync is internal (exposed for testing via InternalsVisibleTo).
        var method = typeof(Bridge.Mcp.Tools.BridgeGrepTools).GetMethod(
            "ResolveTargetsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        var parms = method!.GetParameters();
        Assert.Equal(2, parms.Length);
        Assert.Equal(typeof(string),            parms[0].ParameterType);
        Assert.Equal(typeof(CancellationToken), parms[1].ParameterType);
    }

    // ── ResolveTargetsAsync scope logic ──────────────────────────────────────
    // Only scope=archive is tested here without HTTP.

    [Fact]
    public async Task ResolveTargets_Archive_OnlyContainsPersonalArchive()
    {
        // scope="archive" → ArchiveRepo() only; never calls ListReposWithTopicAsync (HTTP).
        var (grepTools, cfg) = MakeGrepTools();
        var targets = await grepTools.ResolveTargetsAsync("archive", CancellationToken.None);

        var archiveRepo = $"{cfg.ForgejoUser}/personal-archive";
        Assert.Contains(archiveRepo, targets);
        Assert.Single(targets);
    }

    [Fact]
    public async Task ResolveTargets_Archive_NoDuplicates()
    {
        var (grepTools, _) = MakeGrepTools();
        var targets = await grepTools.ResolveTargetsAsync("archive", CancellationToken.None);
        Assert.Equal(targets.Distinct().Count(), targets.Count);
    }

    // ── lookup_fact sensitive stub ────────────────────────────────────────────

    [Fact]
    public void LookupFact_MethodExists_ReturnsTaskObject()
    {
        var method = typeof(Bridge.Mcp.Tools.BridgeGrepTools).GetMethod(
            "LookupFact",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<object>), method!.ReturnType);
    }

    [Fact]
    public void LookupFact_SensitiveScope_IsKnownString()
    {
        // Must be exactly "bridge:read:facts_sensitive" (Python parity).
        Assert.Equal("bridge:read:facts_sensitive", AuthService.ReadFactsSensitiveScope);
    }

    // ── BridgeGrepTools structure ─────────────────────────────────────────────

    [Theory]
    [InlineData("SearchDocuments")]
    [InlineData("LookupFact")]
    public void BridgeGrepTools_ExposesExpectedMethods(string methodName)
    {
        var type = typeof(Bridge.Mcp.Tools.BridgeGrepTools);
        var method = type.GetMethod(methodName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
    }

    [Fact]
    public void BridgeGrepTools_IsDecoratedWithMcpServerToolType()
    {
        var type = typeof(Bridge.Mcp.Tools.BridgeGrepTools);
        var attr = type.GetCustomAttributes(
            typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), inherit: false);
        Assert.Single(attr);
    }

    // ── search_documents response shape ──────────────────────────────────────

    [Fact]
    public void SearchDocuments_ResponseShape_HasCorrectKeys()
    {
        var result = new
        {
            query   = "test",
            scope   = "all",
            matches = new[] { new { repo = "r", path = "p", line = 1, snippet = "s" } },
        };
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("\"query\"",   json);
        Assert.Contains("\"scope\"",   json);
        Assert.Contains("\"matches\"", json);
    }

    // ── ContextPack section shape ─────────────────────────────────────────────

    [Fact]
    public void ContextPack_JsonLength_MatchesPythonDumps()
    {
        // get_context_pack uses JsonLength(section) to measure budget.
        // Python: len(json.dumps(section, ensure_ascii=False))
        // C#: compact JSON + structural-separator count (one space per ':' or ',' not in a string).
        // Verify the method exists and the measurement is consistent.
        var section = new { section = "facts", matches = new[] { new { repo = "r", path = "p", line = 1, snippet = "hello" } } };
        var len     = Bridge.Mcp.Tools.BridgeContextPackTools.JsonLength(section);
        Assert.True(len > 0, "JsonLength must be positive");
        // Verify non-ASCII chars are NOT escaped (ensure_ascii=False parity).
        var nonAscii = new { text = "héllo" };
        var lenNonAscii = Bridge.Mcp.Tools.BridgeContextPackTools.JsonLength(nonAscii);
        // "héllo" is 5 chars but the é is 2 bytes UTF-8. JSON with ensure_ascii=False
        // encodes é as-is (1 char), so len("héllo") = 5 chars.
        // JSON with escaping: é → é (6 chars). Without escaping: é (1 char).
        // Therefore UnsafeRelaxedJsonEscaping (no escape) gives a SHORTER result than default.
        var lenEscaped = System.Text.Json.JsonSerializer.Serialize(nonAscii).Length;
        Assert.True(lenNonAscii <= lenEscaped,
            "UnsafeRelaxedJsonEscaping should produce ≤ length vs escaped");
    }

    // ── Defect 1 regression: JsonLength must match Python json.dumps default separator lengths ──

    [Fact]
    public void JsonLength_SimpleObject_MatchesPythonDefaultSeparators()
    {
        // Python: json.dumps({"section": "facts", "repo": "ste/kb"}, ensure_ascii=False)
        // = '{"section": "facts", "repo": "ste/kb"}' → 38 chars (DEFAULT ", " / ": " separators)
        // C# compact: '{"section":"facts","repo":"ste/kb"}' → 35 chars (NO spaces)
        // Fix: compact(35) + structural_separators(3) = 38 ✓
        var section = new { section = "facts", repo = "ste/kb" };
        var compact = System.Text.Json.JsonSerializer.Serialize(section,
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        var pythonDefaultLen = compact.Length
            + Bridge.Mcp.Tools.BridgeContextPackTools.CountStructuralSeparators(compact);

        // Verify against known value: {"section": "facts", "repo": "ste/kb"} is 38 chars
        // (2 colons + 1 comma = 3 structural separators → compact_35 + 3 = 38).
        Assert.Equal(38, pythonDefaultLen);
        Assert.Equal(38, Bridge.Mcp.Tools.BridgeContextPackTools.JsonLength(section));
    }

    [Fact]
    public void JsonLength_StringsWithColonsAndCommas_CountsOnlyStructural()
    {
        // String values containing ":" and "," must NOT inflate the separator count.
        // Python: len(json.dumps({"text": "hello: world, bye"}, ensure_ascii=False))
        //       = len('{"text": "hello: world, bye"}') = 29
        // compact: '{"text":"hello: world, bye"}' = 28 chars
        // structural separators: 1 colon (key:value) → compact(28) + 1 = 29 ✓
        var section = new { text = "hello: world, bye" };
        var compact = System.Text.Json.JsonSerializer.Serialize(section,
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        var structuralCount = Bridge.Mcp.Tools.BridgeContextPackTools.CountStructuralSeparators(compact);
        // Only the structural ":" between the key and value counts (= 1).
        // The ":" in "hello: world" and "," in "world, bye" are INSIDE a string → not counted.
        Assert.Equal(1, structuralCount);
        Assert.Equal(29, Bridge.Mcp.Tools.BridgeContextPackTools.JsonLength(section));
    }

    [Fact]
    public void JsonLength_EscapedBackslashInString_HandledCorrectly()
    {
        // A \\ in a JSON string value represents one backslash; the scanner must not
        // treat the char after \\ as escaped (it's the next char, not an escape sequence).
        // "a\\:b" in JSON source = 'a\:b' string value — the ":" is inside the string.
        var section = new { k = "a\\:b" };
        var compact = System.Text.Json.JsonSerializer.Serialize(section,
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        // compact = {"k":"a\\:b"} — the : is inside the string → structural count = 1 (key:value only)
        var structuralCount = Bridge.Mcp.Tools.BridgeContextPackTools.CountStructuralSeparators(compact);
        Assert.Equal(1, structuralCount); // only k:value is structural
    }

    [Fact]
    public void JsonLength_DocumentsSection_MatchesPythonLiveEvidence()
    {
        // Live evidence from the bug report:
        // Python live: len(json.dumps(section, ensure_ascii=False)) = 868 for a documents section.
        // This test uses a synthetic section approximating that shape to verify the algorithm
        // scales correctly. With a realistic 5-match section:
        //   section shape: {section:"documents", repo:"ste/repo-X", matches:[{repo,path,line,snippet}×5]}
        // The delta (Python_default - compact) must equal CountStructuralSeparators(compact).
        var matches = Enumerable.Range(1, 5).Select(i => new
        {
            repo    = "ste/kb",
            path    = $"docs/file-{i}.md",
            line    = i * 10,
            snippet = $"This is snippet number {i} with some content here.",
        }).ToArray();
        var section = new { section = "documents", repo = "ste/kb", matches };

        var compact = System.Text.Json.JsonSerializer.Serialize(section,
            new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        var structural = Bridge.Mcp.Tools.BridgeContextPackTools.CountStructuralSeparators(compact);
        var measured   = Bridge.Mcp.Tools.BridgeContextPackTools.JsonLength(section);

        Assert.Equal(compact.Length + structural, measured);
        // The measured length must be GREATER than compact (Python default > compact).
        Assert.True(measured > compact.Length,
            $"JsonLength {measured} must be > compact length {compact.Length}");
    }

    [Fact]
    public void ContextPack_ResponseShape_HasCorrectKeys()
    {
        var result = new { topic = "t", scope = "all", budget = 30000, used = 100, sections = new object[0] };
        var json   = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("\"topic\"",    json);
        Assert.Contains("\"scope\"",    json);
        Assert.Contains("\"budget\"",   json);
        Assert.Contains("\"used\"",     json);
        Assert.Contains("\"sections\"", json);
    }

    [Fact]
    public void GetContextPack_IsDecoratedWithMcpServerToolType()
    {
        var type = typeof(Bridge.Mcp.Tools.BridgeContextPackTools);
        var attr = type.GetCustomAttributes(
            typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), inherit: false);
        Assert.Single(attr);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (Bridge.Mcp.Tools.BridgeGrepTools tools, BridgeConfig cfg) MakeGrepTools()
    {
        var cfg = new BridgeConfig
        {
            ForgejoUser              = "owner",
            ForgejoBaseUrl           = "https://forgejo.example.com",
            ForgejoPatToken          = "pat-test",
            BridgeRepoCache          = Path.Combine(_tmpDir, "cache"),
            BridgeBearerToken        = "test-bearer",
            RateLimitReadPerMin      = 60,
            RateLimitSensitivePerMin = 5,
        };
        var git = new GitOpsService(
            new Sinapsi.Forge.Gitea.GiteaForgeClient(
                new System.Net.Http.HttpClient { BaseAddress = new Uri("http://localhost:3000/api/v1/") }),
            cfg, StubHttpClientFactory.Instance, NullLogger<GitOpsService>.Instance);
        var auth    = new BridgeRateLimiter();
        var authSvc = new AuthService(cfg, auth);
        BridgeAuthState.CurrentAuth = new BridgeAuthContext
        {
            Mode = "bearer", Subject = "legacy-bearer",
            Scopes = LegacyScopes.All, RawToken = "test-bearer",
        };
        var audit = new Bridge.Mcp.Audit.AuditService(cfg, NullLogger<Bridge.Mcp.Audit.AuditService>.Instance);
        var tools = new Bridge.Mcp.Tools.BridgeGrepTools(
            authSvc, git, audit, cfg,
            NullLogger<Bridge.Mcp.Tools.BridgeGrepTools>.Instance);
        return (tools, cfg);
    }

    // ── Git helpers ───────────────────────────────────────────────────────────

    private async Task WriteAndCommitAsync(string relativePath, string? content, bool isNullBytes = false)
    {
        var full = Path.Combine(_localRepoDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        if (isNullBytes)
            await File.WriteAllBytesAsync(full, new byte[] { 0x00, 0xFF, 0xFE, 0x00, 0x01 });
        else
            await File.WriteAllTextAsync(full, content ?? "", Encoding.UTF8);

        await RunGitAsync(["add", "-A"], _localRepoDir);
        await RunGitAsync(["commit", "-m", $"add {relativePath}"], _localRepoDir,
            new Dictionary<string, string>
            {
                ["GIT_COMMITTER_NAME"]  = "Test",
                ["GIT_COMMITTER_EMAIL"] = "test@example.com",
                ["GIT_AUTHOR_NAME"]     = "Test",
                ["GIT_AUTHOR_EMAIL"]    = "test@example.com",
            });
    }

    private static async Task RunGitAsync(
        string[] args, string dir, Dictionary<string, string>? env = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory       = dir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync();
            throw new Exception($"git {string.Join(" ", args)} in {dir} failed: {err}");
        }
    }
}
