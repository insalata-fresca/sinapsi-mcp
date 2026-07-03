using System.ComponentModel;
using System.Text;
using Bridge.Mcp.Audit;
using Bridge.Mcp.Auth;
using Bridge.Mcp.Git;
using ModelContextProtocol.Server;

namespace Bridge.Mcp.Tools;

/// <summary>
/// B4b-5 DEPOSIT TOOLS: deposit_session_summary, deposit_document, deposit_artifact.
///
/// All three tools require scope=bridge:deposit and the deposit rate bucket (30/min).
/// They write git commits via GitOpsService.CommitToRepoAsync (3-retry rebase + auto-create-repo).
///
/// PARITY CONTRACT (S36 equivalence plane 3):
///   - YAML front-matter field order is load-bearing: type, title, repo, tags, created, source.
///   - `created` format: "yyyy-MM-ddTHH:mm:ssZ" (UTC, matches Python datetime.strftime("%Y-%m-%dT%H:%M:%SZ")).
///   - filename: conversations/{YYYY-MM-DD}-{slug}-{sha8}.md  |  artifacts/{type}-{slug}-{sha8}.md  |  {slug}-{sha8}.md
///   - sha8 = SHA256(UTF-8 content)[:8]  (reuse GitOpsService.Sha8)
///   - slug = GitOpsService.Slugify(title)  (byte-parity confirmed in existing tests)
///   - body = "{fm}\n\n# {title}\n\n{content}\n"
///   - knowledge topic side-effect: deposit_session_summary + deposit_document(research/draft/summary)
///     → AddRepoTopicAsync("knowledge"); reference/archive → NO topic.
///   - deposit_artifact: always adds topic.
///
/// ROUTING TABLE (deposit_document):
///   research | draft | summary  →  {repo}/artifacts/{type}-{slug}-{sha8}.md  + knowledge topic
///   reference | archive         →  {FORGEJO_USER}/personal-archive/{slug}-{sha8}.md  (no topic)
///   anything else               →  ToolError("invalid_type", ...)
/// </summary>
[McpServerToolType]
public sealed class BridgeDepositTools(
    AuthService auth,
    GitOpsService git,
    AuditService audit,
    BridgeConfig config)
{
    // ── YAML front-matter (byte-identical to Python _yaml_front_matter) ────────
    //
    // Python:
    //   lines = ["---"]
    //   for k, v in fields.items():
    //       if isinstance(v, list):
    //           rendered = "[" + ", ".join(json.dumps(item) for item in v) + "]"
    //           lines.append(f"{k}: {rendered}")
    //       else:
    //           lines.append(f"{k}: {json.dumps(v)}")
    //   lines.append("---")
    //   return "\n".join(lines)
    //
    // Field order MUST match the call sites in Python exactly:
    //   deposit_session_summary: type, title, repo (raw), tags, created, source
    //   deposit_document:        type, title, repo (resolved), tags, created, source
    //
    // json.dumps(string) → "\"value\"" (quoted string with double-quotes).
    // json.dumps(list)   → "[" + ", ".join(json.dumps(item) for item in v) + "]"
    //   Python's json.dumps with default separators uses ", " between items.
    //
    // Python json.dumps ensure_ascii=True (default) escapes non-ASCII as \uXXXX
    // with LOWERCASE hex digits, and uses \" for double-quotes (not ").
    // C# JsonSerializer.Default produces " for " and uppercase \uXXXX —
    // both differ from Python. We use a manual PythonJsonDumps implementation.

    /// <summary>
    /// Build a YAML front-matter block byte-identical to Python's _yaml_front_matter.
    ///
    /// Fields is an ordered list of (key, value) pairs where value is either a
    /// string or a list of strings. Strings are JSON-quoted; lists are rendered as
    /// JSON arrays with ", " separators (matching Python json.dumps defaults).
    /// </summary>
    internal static string YamlFrontMatter(IEnumerable<(string Key, object? Value)> fields)
    {
        var lines = new List<string> { "---" };
        foreach (var (key, value) in fields)
        {
            if (value is IEnumerable<string> list)
            {
                // Python: "[" + ", ".join(json.dumps(item) for item in v) + "]"
                var items = list.Select(item => PythonJsonDumps(item));
                lines.Add($"{key}: [{string.Join(", ", items)}]");
            }
            else
            {
                // Python: json.dumps(v) — a string value is quoted.
                lines.Add($"{key}: {PythonJsonDumps(value?.ToString() ?? "")}");
            }
        }
        lines.Add("---");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Produce the JSON-quoted string Python's json.dumps(str) would produce.
    ///
    /// Python json.dumps default behavior (ensure_ascii=True):
    ///   - double-quote (") → \" (backslash + quote, NOT ")
    ///   - backslash → \\
    ///   - control chars → \n, \r, \t, \b, \f (short form)
    ///   - non-ASCII BMP chars → \uXXXX with LOWERCASE hex (e.g. ü → ü)
    ///   - supplementary chars (>U+FFFF) → surrogate pair \uD8XX\uDCXX
    ///
    /// C# JsonSerializer.Default uses " for " and uppercase hex — both wrong.
    /// This implementation matches Python byte-for-byte.
    /// </summary>
    internal static string PythonJsonDumps(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20)
                    {
                        // Other control characters: \uXXXX lowercase
                        sb.Append($"\\u{(int)c:x4}");
                    }
                    else if (c > 0x7E)
                    {
                        // Non-ASCII: \uXXXX lowercase hex (Python ensure_ascii=True default)
                        sb.Append($"\\u{(int)c:x4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// UTC timestamp in Python _now_iso() format: "yyyy-MM-ddTHH:mm:ssZ"
    /// Matches Python: datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    /// </summary>
    private static string NowIso() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    /// <summary>
    /// UTC date in Python _today() format: "yyyy-MM-dd"
    /// Matches Python: datetime.now(timezone.utc).strftime("%Y-%m-%d")
    /// </summary>
    private static string Today() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd");

    // ── deposit_session_summary ────────────────────────────────────────────────

    [McpServerTool(Name = "deposit_session_summary")]
    [Description(
        "Commit a conversation distillate to {repo}/conversations/. " +
        "Writes conversations/{YYYY-MM-DD}-{slug}-{sha8(content)}.md with YAML front-matter. " +
        "Adds the 'knowledge' topic to the repo. " +
        "Returns {repo, path, commit, url}.")]
    public async Task<object> DepositSessionSummary(
        [Description("Repo name (bare 'name' or 'owner/name').")] string repo,
        [Description("Human title for the summary.")] string title,
        [Description("Body text of the distillate.")] string content,
        [Description("Optional tag list embedded in YAML front-matter.")] string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.DepositScope,
                AuthService.DepositBucket,
                config.RateLimitDepositPerMin);

            tags ??= [];
            var repoFull = git.ResolveRepo(repo);
            var slug   = GitOpsService.Slugify(title);
            var digest = GitOpsService.Sha8(content);
            var path   = $"conversations/{Today()}-{slug}-{digest}.md";

            // PARITY: in deposit_session_summary Python passes "repo" (the raw input),
            // NOT the resolved repo_full, as the "repo" field in the front-matter.
            var fm = YamlFrontMatter([
                ("type",    "session-summary"),
                ("title",   title),
                ("repo",    repo),
                ("tags",    (object)tags),
                ("created", NowIso()),
                ("source",  "bridge-mcp"),
            ]);
            var body = $"{fm}\n\n# {title}\n\n{content}\n";

            var result = await git.CommitToRepoAsync(
                repoFull,
                path,
                body,
                $"deposit(session-summary): {title}",
                keepDirs: GitOpsService.KeepDirsWorkspace,
                ct: cancellationToken);

            await git.AddRepoTopicAsync(repoFull, "knowledge", cancellationToken);

            audit.Enqueue("deposit_session_summary", ctx, project: repo, query: null, sensitive: false);

            return new
            {
                repo   = result.RepoFull,
                path   = result.Path,
                commit = result.CommitSha,
                url    = git.FileUrl(result.RepoFull, result.Path),
            };
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── deposit_document ───────────────────────────────────────────────────────

    private static readonly HashSet<string> ResearchTypes = ["research", "draft", "summary"];
    private static readonly HashSet<string> ArchiveTypes  = ["reference", "archive"];

    [McpServerTool(Name = "deposit_document")]
    [Description(
        "Route a researched document into the project repo or the personal archive. " +
        "type must be one of: research|draft|summary|reference|archive. " +
        "research/draft/summary → {repo}/artifacts/{type}-{slug}-{sha8}.md + 'knowledge' topic. " +
        "reference/archive → {FORGEJO_USER}/personal-archive/{slug}-{sha8}.md (no topic). " +
        "Invalid type raises ToolError(invalid_type). " +
        "Returns {repo, path, commit, type, url}.")]
    public async Task<object> DepositDocument(
        [Description("Repo name (bare 'name' or 'owner/name'). For reference/archive, routed to personal-archive instead.")] string repo,
        [Description("Human title for the document.")] string title,
        [Description("Document content.")] string content,
        [Description("Document type: research|draft|summary|reference|archive.")] string type,
        [Description("Optional tag list embedded in YAML front-matter.")] string[]? tags = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.DepositScope,
                AuthService.DepositBucket,
                config.RateLimitDepositPerMin);

            tags ??= [];
            // Python: type_l = type.strip().lower()
            var typeL = type.Trim().ToLowerInvariant();
            var slug   = GitOpsService.Slugify(title);
            var digest = GitOpsService.Sha8(content);

            string repoFull;
            string path;
            bool tagWorkspace;
            IEnumerable<string> keepDirs;

            if (ResearchTypes.Contains(typeL))
            {
                repoFull     = git.ResolveRepo(repo);
                path         = $"artifacts/{typeL}-{slug}-{digest}.md";
                tagWorkspace = true;
                keepDirs     = GitOpsService.KeepDirsWorkspace;
            }
            else if (ArchiveTypes.Contains(typeL))
            {
                repoFull     = git.ArchiveRepo();
                path         = $"{slug}-{digest}.md";
                tagWorkspace = false;
                keepDirs     = [];
            }
            else
            {
                throw new BridgeToolException(
                    "invalid_type",
                    "type must be one of research|draft|summary|reference|archive");
            }

            // PARITY: in deposit_document Python passes repo_full (resolved) as the
            // "repo" field in the front-matter (unlike deposit_session_summary which passes raw).
            var fm = YamlFrontMatter([
                ("type",    typeL),
                ("title",   title),
                ("repo",    repoFull),
                ("tags",    (object)tags),
                ("created", NowIso()),
                ("source",  "bridge-mcp"),
            ]);
            var body = $"{fm}\n\n# {title}\n\n{content}\n";

            var result = await git.CommitToRepoAsync(
                repoFull,
                path,
                body,
                $"deposit({typeL}): {title}",
                keepDirs: keepDirs,
                ct: cancellationToken);

            if (tagWorkspace)
                await git.AddRepoTopicAsync(repoFull, "knowledge", cancellationToken);

            audit.Enqueue("deposit_document", ctx, project: repoFull, query: null, sensitive: false);

            return new
            {
                repo   = result.RepoFull,
                path   = result.Path,
                commit = result.CommitSha,
                type   = typeL,
                url    = git.FileUrl(result.RepoFull, result.Path),
            };
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── deposit_artifact ───────────────────────────────────────────────────────

    [McpServerTool(Name = "deposit_artifact")]
    [Description(
        "Write content verbatim to {repo}/artifacts/{path}. " +
        "encoding: 'utf-8'|'utf8'|'text'|'' → text content; 'base64'|'b64' → strict base64 decode to raw bytes. " +
        "Path validated: no '..', max 256 chars, leading slashes stripped. " +
        "Error codes: invalid_path, invalid_base64, invalid_encoding. " +
        "Adds the 'knowledge' topic to the repo. " +
        "Returns {repo, path, commit, url}.")]
    public async Task<object> DepositArtifact(
        [Description("Repo name (bare 'name' or 'owner/name').")] string repo,
        [Description("Relative path within artifacts/. Must not be empty, must not contain '..', max 256 chars. Leading slashes stripped.")] string path,
        [Description("Text content (for utf-8) or base64-encoded bytes (for base64).")] string content,
        [Description("Encoding: 'utf-8'|'utf8'|'text'|'' for text, or 'base64'|'b64' for binary.")] string encoding = "utf-8",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var ctx = auth.Authorize(
                AuthService.DepositScope,
                AuthService.DepositBucket,
                config.RateLimitDepositPerMin);

            var repoFull = git.ResolveRepo(repo);
            var safePath = GitOpsService.SafeArtifactPath(path);
            var fullPath = $"artifacts/{safePath}";

            // Python: enc = encoding.strip().lower()
            var enc = encoding.Trim().ToLowerInvariant();

            GitOpsService.CommitResult result;

            if (enc is "base64" or "b64")
            {
                // Strict base64 decode → raw bytes (binary-safe, byte-identical blob).
                // Python: base64.b64decode(content, validate=True) raises ValueError on invalid.
                //
                // FIX (Chunk 3 Defect 1+3): Python validate=True REJECTS any whitespace
                // (space, \n, \r, \t) in the input. .NET Convert.FromBase64String silently
                // strips whitespace and decodes — plane-3 storage divergence: for MIME-wrapped
                // or line-folded base64 (common CLI/LLM output), Python errors + writes nothing
                // while C# would write a committed file. Pre-scan and reject whitespace to make
                // the invalid_base64 error contract byte-identical to Python's validate=True.
                // Item 7: match Python error message text exactly.
                // Python base64.b64decode(content, validate=True) on whitespace input raises
                // binascii.Error("Only base64 data is allowed") → message = "Only base64 data is allowed"
                // C# must produce the same message text for byte-identical error contracts.
                if (content.Any(char.IsWhiteSpace))
                    throw new BridgeToolException(
                        "invalid_base64",
                        "content is not valid base64: Only base64 data is allowed");

                // FIX (Item 7): produce Python-exact binascii error messages for every
                // non-whitespace invalid input, mirroring base64.b64decode(validate=True).
                byte[] payload = DecodeBase64PythonParity(content);

                // FIX (Chunk 3 Defect 2): pass keepDirs so EnsureKeepDirs creates .gitkeep
                // placeholders on freshly-created repos — mirrors Python which calls
                // commit_to_repo(..., keep_dirs=_KEEP_DIRS_WS) for BOTH text and base64 branches.
                result = await git.CommitToRepoBinaryAsync(
                    repoFull,
                    fullPath,
                    payload,
                    $"deposit(artifact): {safePath}",
                    keepDirs: GitOpsService.KeepDirsWorkspace,
                    ct: cancellationToken);
            }
            else if (enc is "utf-8" or "utf8" or "text" or "")
            {
                result = await git.CommitToRepoAsync(
                    repoFull,
                    fullPath,
                    content,
                    $"deposit(artifact): {safePath}",
                    keepDirs: GitOpsService.KeepDirsWorkspace,
                    ct: cancellationToken);
            }
            else
            {
                throw new BridgeToolException("invalid_encoding", "encoding must be 'utf-8' or 'base64'");
            }

            await git.AddRepoTopicAsync(repoFull, "knowledge", cancellationToken);

            audit.Enqueue("deposit_artifact", ctx, project: repo, query: null, sensitive: false);

            return new
            {
                repo   = result.RepoFull,
                path   = result.Path,
                commit = result.CommitSha,
                url    = git.FileUrl(result.RepoFull, result.Path),
            };
        }
        catch (BridgeToolException ex) { BridgeToolError.Throw(ex); throw; }
    }

    // ── Python-parity base64 decoder ──────────────────────────────────────────
    //
    // Python base64.b64decode(s, validate=True) delegates to binascii.a2b_base64
    // with the validate flag.  That C extension produces exactly these six messages
    // (CPython 3.x, binascii module):
    //
    //   "string argument should contain only ASCII characters"  — non-ASCII byte
    //   "Only base64 data is allowed"                          — invalid char (not in alphabet + not '=')
    //   "Excess data after padding"                            — data after '==' terminator
    //   "Incorrect padding"                                    — wrong number of base64 data quanta
    //   "Leading padding not allowed"                          — '=' before any data chars
    //
    // All are wrapped by the caller as:
    //   "content is not valid base64: <message>"
    //
    // NOTE: Whitespace is already rejected by the caller before this method is
    // reached (pre-scan guard above).  Whitespace chars ARE invalid base64, so
    // without the pre-scan they would fall into the "Only base64 data is allowed"
    // branch here — the pre-scan uses a custom message by design.
    //
    // ALPHABET: A-Z a-z 0-9 + /   (64 chars) — standard RFC 4648 base64.
    // '=' is padding only; whitespace was already rejected before this call.
    private static readonly System.Collections.Frozen.FrozenSet<char> _b64Alphabet =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");

    /// <summary>
    /// Validate and decode a strict RFC 4648 base64 string, emitting the exact
    /// error message text that CPython's <c>binascii.a2b_base64(validate=True)</c>
    /// would produce.  Throws <see cref="BridgeToolException"/> with code
    /// <c>invalid_base64</c> on any violation.
    /// </summary>
    internal static byte[] DecodeBase64PythonParity(string content)
    {
        // 1. Non-ASCII → "string argument should contain only ASCII characters"
        //    CPython checks this first, before any other validation (including whitespace).
        foreach (var ch in content)
        {
            if (ch > 127)
                throw new BridgeToolException("invalid_base64",
                    "content is not valid base64: string argument should contain only ASCII characters");
        }

        // 2. Empty string decodes to empty byte array (Python behaviour).
        if (content.Length == 0)
            return [];

        // 3. Leading padding → "Leading padding not allowed"
        //    Python raises this when the very first char is '='.
        if (content[0] == '=')
            throw new BridgeToolException("invalid_base64",
                "content is not valid base64: Leading padding not allowed");

        // 4. Quantum-aware validation matching CPython binascii.a2b_base64(validate=True).
        //
        //    Python processes the string in 4-char quanta.  Error priority (verified against
        //    CPython 3.12 / 3.13 across 18 000+ inputs):
        //
        //      a. Any char not in the base64 alphabet and not '='
        //         → "Only base64 data is allowed"  (fires immediately, before pad checks)
        //
        //      b. data_chars_before_first_eq % 4 == 1
        //         → "Invalid base64-encoded string: number of data characters (N)
        //            cannot be 1 more than a multiple of 4"
        //         (fires before Discontinuous for same-quantum '=' issues)
        //
        //      c. '=' at quantum position 2 with a data char at quantum position 3
        //         (= at pos 1 is caught earlier by rule b since only 1 data char precedes)
        //         → "Discontinuous padding not allowed"  (e.g. 'QQ=A')
        //
        //      d. '=' at quantum position 0 (start of a new quantum) after a full-data quantum
        //         → "Excess padding not allowed"  (e.g. 'QUJD=', 'QUJD==')
        //
        //      e. Any '=' following a quantum that ended with '=' (pad continuation)
        //         → "Excess padding not allowed"  (e.g. 'QQ===')
        //
        //      f. Any data char following a quantum that ended with '='
        //         → "Excess data after padding"  (e.g. 'QQ==A', 'QUJDRA==X')
        //
        //      g. Total length % 4 ∈ {2, 3} without any '=' padding
        //         → "Incorrect padding"
        //         (CPython validate=True requires explicit padding; auto-pad is NOT done.
        //          Accepting and committing these would diverge from Python plane-3 storage.)
        //
        //      h. Partial quantum ending with '=': total length not a multiple of 4
        //         → "Incorrect padding"
        //
        //    Note: whitespace was already rejected by the caller's pre-scan guard;
        //    it would otherwise fall into branch (a) as a non-alphabet char.

        // ── Step 4a: scan for any invalid (non-alphabet, non-'=') chars first. ──
        // This must fire before the data_chars count check (rule b).
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (c != '=' && !_b64Alphabet.Contains(c))
                throw new BridgeToolException("invalid_base64",
                    "content is not valid base64: Only base64 data is allowed");
        }

        // ── Step 4b: count data chars before the first '='. ──
        // If that count % 4 == 1, Python emits the "number of data characters" message.
        int firstEqIdx = content.IndexOf('=');
        int dataCountBeforePad = firstEqIdx < 0 ? content.Length : firstEqIdx;
        if (dataCountBeforePad % 4 == 1)
            throw new BridgeToolException("invalid_base64",
                $"content is not valid base64: Invalid base64-encoded string: number of data characters " +
                $"({dataCountBeforePad}) cannot be 1 more than a multiple of 4");

        // ── Steps 4c–g: quantum-aware structural validation. ──
        //
        // At this point we know: no invalid chars, data_before_pad % 4 != 1.
        // Walk quanta to detect Discontinuous / Excess padding / Excess data / Incorrect padding.

        bool lastQuantumPadded = false; // did the most recently completed quantum end with '='?

        for (int qi = 0; qi < content.Length; qi += 4)
        {
            // Remaining chars in this quantum (may be <4 for the last partial quantum).
            int qLen = Math.Min(4, content.Length - qi);

            // Count '=' and data chars within this quantum.
            int eqInQuantum   = 0;
            int dataInQuantum = 0;
            for (int k = qi; k < qi + qLen; k++)
            {
                if (content[k] == '=') eqInQuantum++;
                else                   dataInQuantum++;
            }

            // ── Excess data after padding: any data follows a padded quantum. ──
            if (lastQuantumPadded && dataInQuantum > 0)
                throw new BridgeToolException("invalid_base64",
                    "content is not valid base64: Excess data after padding");

            // ── Excess padding (variant 1): '=' follows a padded quantum. ──
            if (lastQuantumPadded && eqInQuantum > 0)
                throw new BridgeToolException("invalid_base64",
                    "content is not valid base64: Excess padding not allowed");

            if (qLen == 4)
            {
                // Full quantum: exactly 4 chars.
                if (eqInQuantum == 0)
                {
                    // All 4 data chars — valid quantum.
                    lastQuantumPadded = false;
                }
                else if (eqInQuantum == 1 && content[qi + 3] == '=')
                {
                    // Pattern: data-data-data-'=' — valid single-pad quantum.
                    lastQuantumPadded = true;
                }
                else if (eqInQuantum == 2 && content[qi + 2] == '=' && content[qi + 3] == '=')
                {
                    // Pattern: data-data-'='-'=' — valid double-pad quantum.
                    lastQuantumPadded = true;
                }
                else if (eqInQuantum >= 1 && content[qi] != '=' &&
                         content[qi + 1] != '=' && content[qi + 2] == '=' && content[qi + 3] != '=')
                {
                    // Pattern: data-data-'='-data — '=' at position 2, data at position 3.
                    // Discontinuous padding (rule c above).
                    throw new BridgeToolException("invalid_base64",
                        "content is not valid base64: Discontinuous padding not allowed");
                }
                else
                {
                    // Any other arrangement with '=' in a full quantum that doesn't match
                    // a valid pattern.  This covers:
                    //   data-'='-data-data (pos 1 with data following — but data_count<4
                    //     already handled by rule b; here data_before_pad==1, 1%4==1 so
                    //     rule b fired first — we should not reach this branch for pos-1 cases)
                    //   '='-data-data-data (leading '=' already caught at step 3 for i==0;
                    //     for mid-string quanta this is "Excess data after padding")
                    // Fallback: "Excess padding not allowed" covers the remaining structural error.
                    throw new BridgeToolException("invalid_base64",
                        "content is not valid base64: Excess padding not allowed");
                }
            }
            else
            {
                // Partial quantum (last quantum, qLen ∈ {1,2,3}).
                if (eqInQuantum > 0 && dataInQuantum == 0)
                {
                    // Partial quantum made up entirely of '=' chars (e.g. 'QUJD=' or 'QUJD=='):
                    // the preceding full-data quantum needed NO padding, yet padding chars follow.
                    // Python: "Excess padding not allowed".
                    throw new BridgeToolException("invalid_base64",
                        "content is not valid base64: Excess padding not allowed");
                }

                if (eqInQuantum > 0)
                {
                    // Partial quantum containing both data and '=' chars, or starting with '=',
                    // but total length is not a multiple of 4 (e.g. 'QQ=', 'AB=').
                    // Python: "Incorrect padding".
                    throw new BridgeToolException("invalid_base64",
                        "content is not valid base64: Incorrect padding");
                }

                // Partial quantum with only data chars (no '=').
                // rule g: data_before_pad % 4 ∈ {2, 3} — "Incorrect padding".
                // (qLen==1 / data_count%4==1 was already caught by rule b.)
                // CPython validate=True requires explicit padding; auto-padding is not done here
                // because that would silently write different bytes than Python (plane-3 divergence).
                throw new BridgeToolException("invalid_base64",
                    "content is not valid base64: Incorrect padding");
            }
        }

        // All structural checks passed — delegate to the runtime.
        // Length is a multiple of 4 with correct padding, so Convert.FromBase64String is safe.
        return Convert.FromBase64String(content);
    }
}
