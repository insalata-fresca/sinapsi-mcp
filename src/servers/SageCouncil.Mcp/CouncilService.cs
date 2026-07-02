using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Sinapsi.AgentJwt;
using Sinapsi.Mcp;

namespace SageCouncil.Mcp;

public sealed record CouncilOptions
{
    // Where the "claude" member runs: an HTTP agent backend exposing a simple
    // session API (POST /v1/sessions, POST .../messages, DELETE ...). Neutral
    // local default; point it at your own backend with AGENT_BACKEND_URL.
    public Uri BackendUrl { get; init; } = new("http://127.0.0.1:8088");

    // The MCP gateway the gemini/chatgpt members call through. Neutral local
    // default; override with GATEWAY_URL.
    public Uri GatewayUrl { get; init; } = new("http://127.0.0.1:8443/mcp");

    // A consult is intentionally long-running — members do genuine multi-step
    // research and accuracy beats speed, so these ceilings are generous. They
    // are safety nets for a hung backend, not a normal-case budget.
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan MemberDeadline { get; init; } = TimeSpan.FromMinutes(25);

    // The model the "claude" member's session is created with. Env-overridable.
    public string Model { get; init; } = "claude-sonnet-4-6";

    // Swappable + expandable personas: name → system prompt, loaded at startup
    // from PERSONA_DIR (drop a `<name>.md` file to add one — no recompile). These
    // overlay the built-in personas (CouncilService._focusPrompts), so a file can
    // also override a built-in. Empty when the dir is absent (zero-risk default).
    public IReadOnlyDictionary<string, string> ExtraPersonas { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // Per-member machine identities (one JWK file each in the AgentJwt KeyDir).
    // Env-overridable so the roster can be renamed without a recompile.
    public string ClaudeAgent { get; init; } = "agent-council-claude";
    public string GeminiAgent { get; init; } = "agent-council-gemini";
    public string ChatgptAgent { get; init; } = "agent-council-chatgpt";

    /// <summary>Default per-outbound-call ceiling (ms) when none is configured.</summary>
    internal const int DefaultTimeoutMs = 30 * 60 * 1000;   // 30 min

    /// <summary>Default per-member wall-clock deadline (ms) when none is configured.</summary>
    internal const int DefaultMemberTimeoutMs = 25 * 60 * 1000; // 25 min

    /// <summary>Hard ceiling on either configurable timeout (ms). A consult is
    /// intentionally long-running, so this is generous — 2 hours is far past any
    /// legitimate research call; a larger value (e.g. a mistyped extra zero) is
    /// treated as a config error, not honoured.</summary>
    internal const int MaxTimeoutMs = 2 * 60 * 60 * 1000;   // 7_200_000

    public static CouncilOptions FromEnvironment()
    {
        static string Env(string k, string dflt) =>
            Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : dflt;
        return new CouncilOptions
        {
            BackendUrl = new Uri(Env("AGENT_BACKEND_URL", "http://127.0.0.1:8088")),
            GatewayUrl = new Uri(Env("GATEWAY_URL", "http://127.0.0.1:8443/mcp")),
            Timeout = TimeSpan.FromMilliseconds(ReadTimeoutMs("SAGE_TIMEOUT_MS", DefaultTimeoutMs)),
            MemberDeadline = TimeSpan.FromMilliseconds(ReadTimeoutMs("SAGE_MEMBER_TIMEOUT_MS", DefaultMemberTimeoutMs)),
            Model = Env("AGENT_MODEL", "claude-sonnet-4-6"),
            ClaudeAgent = Env("COUNCIL_CLAUDE_AGENT", "agent-council-claude"),
            GeminiAgent = Env("COUNCIL_GEMINI_AGENT", "agent-council-gemini"),
            ChatgptAgent = Env("COUNCIL_CHATGPT_AGENT", "agent-council-chatgpt"),
            ExtraPersonas = LoadPersonas(Env("PERSONA_DIR", "/etc/sage-council-mcp/personas")),
        };
    }

    /// <summary>
    /// Read a timeout (ms) env var fail-closed. Unset → the supplied default. A
    /// non-numeric, <c>&lt;= 0</c>, or above-<see cref="MaxTimeoutMs"/> value is
    /// rejected as invalid config — we throw a clear error naming the offending
    /// env var rather than silently honouring a footgun (a 0 would make every
    /// call time out instantly; a negative throws deep in the cancellation path;
    /// a wildly-large value defeats the safety net).
    /// </summary>
    private static int ReadTimeoutMs(string envVar, int dflt)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
            return dflt;
        if (!int.TryParse(raw, out var ms) || ms <= 0 || ms > MaxTimeoutMs)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an integer in 1..{MaxTimeoutMs} ms " +
                $"(default {dflt}).");
        return ms;
    }

    /// <summary>Load `<name>.md` persona files from a directory (optional; empty if absent).</summary>
    private static IReadOnlyDictionary<string, string> LoadPersonas(string dir)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.md"))
                {
                    var body = File.ReadAllText(f).Trim();
                    if (body.Length > 0) map[Path.GetFileNameWithoutExtension(f)] = body;
                }
        }
        catch { /* personas are an optional overlay — never fail startup over them */ }
        return map;
    }
}

public sealed record MemberResult(string Member, string Report, long LatencyMs, string? Error = null);

/// <summary>
/// Orchestrates a multi-perspective consult: parallel fan-out to council
/// members (the "claude" member via the agent backend; "gemini"/"chatgpt" via
/// the MCP gateway), each behind its own restricted identity, capped by a
/// per-member wall-clock deadline, then a synthesis pass.
/// </summary>
public sealed class CouncilService(HttpClient http, GatewayMcpClient gateway, AgentJwtMinter jwtMinter, CouncilOptions opt, ILogger<CouncilService> log)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // These are research mandates, not personas. The council is convened on hard
    // problems where accuracy and depth matter far more than speed: every focus
    // tells the member to actually investigate (web search + read primary
    // sources), triangulate, and cite — never to answer from memory alone.
    private static readonly IReadOnlyDictionary<string, string> _focusPrompts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["general"] = "You are a senior research analyst consulted on a hard, non-trivial question. Do genuine multi-step research before answering: use the search/grounding tools available to you (e.g. a search + extract tool, or your own web grounding), read the primary sources, and cross-check claims against each other. Accuracy and depth matter far more than speed — take the time you need. Return a structured report that shows your reasoning and cites the sources you relied on.",
        ["code-review"] = "You are a principal engineer doing a rigorous code/design review of a hard problem. Research current best practices, real-world failure modes, and known issues using the tools available to you (web search, primary docs); do not rely on memory. Be concrete and specific, and cite your sources. Return a thorough review: strengths, risks, edge cases, and a clear recommendation.",
        ["architecture"] = "You are a systems architect consulted on a hard design question. Research industry patterns, comparable systems, and documented trade-offs using the tools available to you (web search, primary sources). Depth over speed. Return: problem framing, the viable options with honest pros/cons, prior art with citations, and a justified recommendation.",
        ["second-opinion"] = "You are a contrarian principal engineer giving a second opinion on a hard decision. Actively hunt for counter-arguments, failure modes, and risks the asker may have missed — research them with the tools available to you, do not just assert. Cite your sources. Be specific and provocative; do not soften.",
        ["deep-research"] = "You are a deep-research analyst. Run an iterative investigation (>=5 distinct searches), extract and read the key primary sources, and triangulate findings across them. This is the highest-depth mode: take the time needed for accuracy. Produce a comprehensive multi-section report with inline citations and a confidence note on each major claim.",
        ["design"] = "You are a senior product/UX designer on a multi-perspective design council. Your job is the FRONT HALF of UI design — model the user and their journey (persona incl. the anti-persona, jobs-to-be-done, and the stages with each decision and the information needed to make it), derive information architecture, and judge usability — NOT visual styling/colour (that is a separate craft layer). Follow the UX guidance and the STEP instructions included in the question, and return exactly what the step asks for (usually strict JSON, with no prose outside it). Ground your judgement in real product flow patterns rather than padding into generic research. Correct for over-optimism: walk the flow as a NOVICE under load, not an expert who already knows the answer, and surface where they cannot complete the job.",
    };

    // Personas are swappable + expandable: a PERSONA_DIR overlay file wins over a
    // built-in, then the built-ins, then "general". (See CouncilOptions.ExtraPersonas.)
    private string Sys(string focus) =>
        opt.ExtraPersonas.TryGetValue(focus, out var ext) ? ext
        : _focusPrompts.TryGetValue(focus, out var s) ? s
        : _focusPrompts["general"];

    // Focuses whose members return strict JSON (not prose) — the synthesis pass must
    // stay JSON too, so a downstream consumer gets clean structured output.
    private static bool IsStructuredFocus(string focus) => focus == "design";

    public async Task<string> ConsultAsync(string prompt, string focus, IReadOnlyList<string> members, CancellationToken ct)
    {
        var tasks = members.Select(m => WithDeadlineAsync(m, Dispatch(m, prompt, focus, ct)));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var synthesis = await SynthesizeAsync(prompt, focus, results, ct).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            prompt,
            focus,
            members = results.Select(r => new
            {
                member = r.Member,
                report = r.Report,
                latency_ms = r.LatencyMs,
                error = r.Error,
            }),
            synthesis,
        }, _json);
    }

    private Task<MemberResult> Dispatch(string member, string prompt, string focus, CancellationToken ct) => member switch
    {
        "claude-research" => ClaudeMemberAsync(prompt, focus, ct),
        "gemini-research" => GeminiMemberAsync(prompt, focus, ct),
        "chatgpt-research" => ChatgptMemberAsync(prompt, focus, ct),
        _ => Task.FromResult(new MemberResult(member, "", 0, CouncilErrors.Sanitize($"unknown member type: {member}"))),
    };

    /// <summary>Bound a member to a hard wall-clock deadline.</summary>
    private async Task<MemberResult> WithDeadlineAsync(string member, Task<MemberResult> work)
    {
        var sw = Stopwatch.StartNew();
        var delay = Task.Delay(opt.MemberDeadline);
        var done = await Task.WhenAny(work, delay).ConfigureAwait(false);
        if (done == work)
            return await work.ConfigureAwait(false);
        return new MemberResult(member, "", sw.ElapsedMilliseconds, CouncilErrors.Sanitize($"member deadline exceeded ({opt.MemberDeadline.TotalMilliseconds}ms)"));
    }

    private static AuthenticationHeaderValue Bearer(string jwt) => new("Bearer", jwt);

    // ---- Member: claude-research (agent-backend session spawn) ----
    private async Task<MemberResult> ClaudeMemberAsync(string prompt, string focus, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string jwt;
        try { jwt = await jwtMinter.MintAsync(opt.ClaudeAgent, ct).ConfigureAwait(false); }
        catch (Exception e) { return new("claude-research", "", sw.ElapsedMilliseconds, CouncilErrors.Sanitize($"mint: {e.Message}")); }

        string sessionId;
        try { sessionId = await CreateSessionAsync(jwt, focus, ct).ConfigureAwait(false); }
        catch (Exception e) { return new("claude-research", "", sw.ElapsedMilliseconds, CouncilErrors.Sanitize($"session: {e.Message}")); }

        var composed = $"{Sys(focus)}\n\nQuestion: {prompt}";
        try
        {
            var report = await SendMessageAsync(jwt, sessionId, composed, ct).ConfigureAwait(false);
            return new("claude-research", report, sw.ElapsedMilliseconds);
        }
        catch (Exception e) { return new("claude-research", "", sw.ElapsedMilliseconds, CouncilErrors.Sanitize($"message: {e.Message}")); }
        finally { await DeleteSessionAsync(jwt, sessionId).ConfigureAwait(false); }
    }

    // ---- Member: gemini-research (gateway → gemini_research, a deep-research
    // mode). It is an async tool: gemini_research spawns a task and returns a
    // task_id; we poll gemini_get_status until done/failed. Depth is mapped from
    // the consult focus. Contract: task_id; status ∈ running|done|failed; result
    // holds the final text. ----
    private async Task<MemberResult> GeminiMemberAsync(string prompt, string focus, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(opt.Timeout);

            var query = $"{Sys(focus)}\n\nQuestion: {prompt}";
            var jwt = await jwtMinter.MintAsync(opt.GeminiAgent, cts.Token).ConfigureAwait(false);
            var spawn = await gateway.CallToolAsync(opt.GatewayUrl, jwt, "gemini_research",
                new { query, depth = GeminiDepth(focus) }, cts.Token).ConfigureAwait(false);

            var taskId = ExtractJsonString(spawn, "task_id")
                ?? throw new InvalidOperationException($"gemini_research returned no task_id: {Truncate(spawn)}");

            var report = await PollGeminiResearchAsync(taskId, cts.Token).ConfigureAwait(false);
            return new("gemini-research", report, sw.ElapsedMilliseconds);
        }
        catch (Exception e) { return new("gemini-research", "", sw.ElapsedMilliseconds, CouncilErrors.Sanitize(e.Message)); }
    }

    private static string GeminiDepth(string focus) => focus switch
    {
        "deep-research" or "architecture" or "second-opinion" => "deep",
        _ => "standard",
    };

    /// <summary>
    /// Poll gemini_get_status until the research task is done or failed. The JWT
    /// is re-minted each poll (the minter caches it) so a research run longer than
    /// the token TTL doesn't 401 mid-flight. Bounded by the linked token (opt.Timeout).
    /// </summary>
    private async Task<string> PollGeminiResearchAsync(string taskId, CancellationToken ct)
    {
        var interval = PollInterval;
        while (true)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            var jwt = await jwtMinter.MintAsync(opt.GeminiAgent, ct).ConfigureAwait(false);
            var statusJson = await gateway.CallToolAsync(opt.GatewayUrl, jwt, "gemini_get_status",
                new { task_id = taskId }, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(statusJson);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            switch (status)
            {
                case "done":
                    if (!root.TryGetProperty("result", out var r)) return "";
                    return r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : r.GetRawText();
                case "failed":
                    var err = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown error";
                    throw new InvalidOperationException($"gemini research failed: {err}");
                default:
                    continue; // running — keep polling
            }
        }
    }

    // The gemini status poll interval. Settable so a test can poll fast; the
    // production default (10s) is unchanged.
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);

    private static string? ExtractJsonString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var v) ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];

    // ---- Member: chatgpt-research (gateway → codex_codex) ----
    private async Task<MemberResult> ChatgptMemberAsync(string prompt, string focus, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var jwt = await jwtMinter.MintAsync(opt.ChatgptAgent, ct).ConfigureAwait(false);
            // Let codex actually research (web search, reading sources). The
            // read-only sandbox keeps it safe (no writes) without crippling its
            // investigation.
            var composed = $"{Sys(focus)}\n\nQuestion: {prompt}\n\nResearch this thoroughly using the tools available to you (web search, reading sources). Prioritise accuracy and depth over speed, and cite the sources you relied on.";
            var args = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["prompt"] = composed,
                ["sandbox"] = "read-only",
                ["approval-policy"] = "never",
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(opt.Timeout);
            var report = await gateway.CallToolAsync(opt.GatewayUrl, jwt, "codex_codex", args, cts.Token).ConfigureAwait(false);
            return new("chatgpt-research", report, sw.ElapsedMilliseconds);
        }
        catch (Exception e) { return new("chatgpt-research", "", sw.ElapsedMilliseconds, CouncilErrors.Sanitize(e.Message)); }
    }

    // ---- Synthesis (a 4th "claude" session) ----
    private async Task<string> SynthesizeAsync(string prompt, string focus, IReadOnlyList<MemberResult> results, CancellationToken ct)
    {
        var usable = results.Where(r => !string.IsNullOrEmpty(r.Report) && r.Error is null).ToList();
        if (usable.Count == 0) return "(no member returned a usable answer; synthesis skipped)";
        if (usable.Count == 1) return usable[0].Report;

        // A structured focus (e.g. design) requires the synthesis to be strict JSON of the
        // same shape the members returned — merge, don't prose — so a downstream consumer
        // gets clean JSON. Every other focus keeps the grounded 2-3 paragraph prose synthesis.
        var instruction = IsStructuredFocus(focus)
            ? $"You are reconciling {usable.Count} expert analyses of the SAME design step. Each perspective below is a strict JSON object of the same shape. Merge them into ONE reconciled JSON object of that SAME shape: take the union of the journey stages / decisions / information-needs / states / anti-patterns, and where they disagree choose the better-justified position. Output ONLY the merged JSON — no markdown fence, no prose before or after. Do not use tool calls."
            : $"You are synthesising {usable.Count} expert perspectives on a question. Identify points where they agree, where they disagree, and write a single grounded 2-3 paragraph answer. Do not use tool calls.";
        var sb = new StringBuilder()
            .Append(instruction).Append("\n\n")
            .Append("Question: ").Append(prompt).Append("\n\n");
        for (var i = 0; i < usable.Count; i++)
            sb.Append("-- Perspective ").Append(i + 1).Append(" (").Append(usable[i].Member).Append(") --\n").Append(usable[i].Report).Append("\n\n");
        sb.Append("-- Your synthesis --\n");

        string jwt;
        try { jwt = await jwtMinter.MintAsync(opt.ClaudeAgent, ct).ConfigureAwait(false); }
        catch (Exception e) { return CouncilErrors.Sanitize($"(synthesis failed: mint: {e.Message})"); }

        string sid;
        try { sid = await CreateSessionAsync(jwt, "general", ct).ConfigureAwait(false); }
        catch (Exception e) { return CouncilErrors.Sanitize($"(synthesis session failed: {e.Message})"); }

        try { return await SendMessageAsync(jwt, sid, sb.ToString(), ct).ConfigureAwait(false); }
        catch (Exception e) { return CouncilErrors.Sanitize($"(synthesis message failed: {e.Message})"); }
        finally { await DeleteSessionAsync(jwt, sid).ConfigureAwait(false); }
    }

    // ---- agent-backend helpers ----
    private async Task<string> CreateSessionAsync(string jwt, string focus, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(opt.BackendUrl, "/v1/sessions"))
        {
            Content = JsonContent(new { focus, model = opt.Model }),
        };
        req.Headers.Authorization = Bearer(jwt);
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("session_id").GetString()
               ?? throw new InvalidOperationException("no session_id");
    }

    private async Task<string> SendMessageAsync(string jwt, string sessionId, string message, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(opt.Timeout);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(opt.BackendUrl, $"/v1/sessions/{sessionId}/messages"))
        {
            Content = JsonContent(new { message }),
        };
        req.Headers.Authorization = Bearer(jwt);
        using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("result", out var r)) return "";
        return r.ValueKind switch
        {
            JsonValueKind.String => r.GetString() ?? "",
            JsonValueKind.Object => r.TryGetProperty("result", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString()!
                                  : r.TryGetProperty("text", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString()!
                                  : r.GetRawText(),
            _ => r.GetRawText(),
        };
    }

    private async Task DeleteSessionAsync(string jwt, string sessionId)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, new Uri(opt.BackendUrl, $"/v1/sessions/{sessionId}"));
            req.Headers.Authorization = Bearer(jwt);
            using var _ = await http.SendAsync(req).ConfigureAwait(false);
        }
        catch (Exception e) { log.LogDebug(e, "session delete failed (ignored)"); }
    }

    private static StringContent JsonContent(object o) =>
        new(JsonSerializer.Serialize(o, _json), Encoding.UTF8, "application/json");
}
