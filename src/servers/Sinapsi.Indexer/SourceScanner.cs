using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Sinapsi.Indexer;

/// <summary>One source repo to index: a clone URL + branch + a local cache dir.</summary>
public sealed record RepoSpec(string Source, string Url, string Branch, string CacheDir);

/// <summary>
/// Re-scans the SOURCE repos (the truth) into <see cref="Document"/>s. Events are
/// only change-notifications; a (re)scan = git pull + walk the markdown +
/// classify + hash (rebuild = re-scan the sources, never replay the event log).
/// Reads markdown only; never secret-shaped paths.
/// </summary>
public sealed class SourceScanner
{
    private readonly IReadOnlyList<RepoSpec> _repos;
    private readonly string? _token;
    private readonly string _gitUser;
    private readonly string _learningsSource;
    private readonly ILogger _log;

    // Defence-in-depth path denylist — never index anything secret-shaped, even
    // though live secrets are expected to live in a secret manager, not a repo.
    private static readonly string[] DenyFragments =
        { "/secrets/", "/secret/", "vault.yml", "vault.yaml", "/.git/", "/private/" };

    public SourceScanner(IReadOnlyList<RepoSpec> repos, string? token, ILogger log)
    {
        _repos = repos;
        _token = token;
        _gitUser = Env("INDEXER_GIT_USER", "git");
        _learningsSource = Env("INDEXER_LEARNINGS_SOURCE", "learnings");
        _log = log;
    }

    public IReadOnlyList<RepoSpec> Repos => _repos;

    /// <summary>Build the default repo set from env (forge base + branch + cache dir).
    /// All values are env-driven with neutral local defaults — nothing is baked in.</summary>
    public static IReadOnlyList<RepoSpec> ReposFromEnv()
    {
        var baseUrl = Env("FORGE_BASE_URL", "https://forge.example.com").TrimEnd('/');
        var cache = Env("INDEXER_CACHE_DIR", "/var/lib/sinapsi-indexer/repos");
        var branch = Env("INDEXER_REPO_BRANCH", "main");
        // Comma list of "source=owner/repo". Empty by default — configure the
        // repos to index (e.g. "docs=acme/docs,learnings=acme/learnings").
        var spec = Env("INDEXER_REPOS", "");
        var list = new List<RepoSpec>();
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var source = kv[0].Trim();
            var url = $"{baseUrl}/{kv[1].Trim()}.git";
            list.Add(new RepoSpec(source, url, branch, System.IO.Path.Combine(cache, source)));
        }
        return list;
    }

    private static string Env(string k, string dflt) =>
        Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : dflt;

    /// <summary>git clone (if absent) or fetch+hard-reset (if present). Returns false on failure.</summary>
    public async Task<bool> SyncAsync(RepoSpec repo, CancellationToken ct)
    {
        try
        {
            // Inject the read token into the URL only at runtime; never logged.
            var url = repo.Url;
            if (!string.IsNullOrEmpty(_token) && url.StartsWith("https://"))
                url = "https://" + _gitUser + ":" + _token + "@" + url["https://".Length..];

            if (!Directory.Exists(System.IO.Path.Combine(repo.CacheDir, ".git")))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(repo.CacheDir)!);
                if (Directory.Exists(repo.CacheDir)) Directory.Delete(repo.CacheDir, true);
                await GitAsync(null, ct, "clone", "--depth", "1", "--branch", repo.Branch, url, repo.CacheDir);
            }
            else
            {
                await GitAsync(repo.CacheDir, ct, "remote", "set-url", "origin", url);
                await GitAsync(repo.CacheDir, ct, "fetch", "--depth", "1", "origin", repo.Branch);
                await GitAsync(repo.CacheDir, ct, "reset", "--hard", $"origin/{repo.Branch}");
            }
            return true;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "git sync failed for {source}", repo.Source);
            return false;
        }
    }

    /// <summary>Walk the synced checkout → one Document per indexable .md file.</summary>
    public IReadOnlyList<Document> Scan(RepoSpec repo)
    {
        var docs = new List<Document>();
        if (!Directory.Exists(repo.CacheDir)) return docs;
        foreach (var file in Directory.EnumerateFiles(repo.CacheDir, "*.md", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(repo.CacheDir, file).Replace('\\', '/');
            var probe = "/" + rel.ToLowerInvariant();
            if (DenyFragments.Any(probe.Contains)) continue;

            string body;
            try { body = File.ReadAllText(file); }
            catch { continue; }
            if (body.Length == 0) continue;

            docs.Add(new Document
            {
                DocId = Document.MakeDocId(repo.Source, rel),
                Source = repo.Source,
                Path = rel,
                Kind = ClassifyKind(repo.Source, rel),
                Title = ExtractTitle(body, rel),
                Body = body,
                ContentSha = Sha256(body),
                Scope = repo.Source == _learningsSource ? ScopeOf(rel) : "",
            });
        }
        return docs;
    }

    internal string ClassifyKind(string source, string rel) => ClassifyKind(source, rel, _learningsSource);

    /// <summary>Classify a markdown file into a coarse kind. Files in the learnings
    /// source are "learning"; otherwise a small, generic path-prefix heuristic
    /// covers a docs/decisions/conventions layout, defaulting to "doc".</summary>
    internal static string ClassifyKind(string source, string rel, string learningsSource)
    {
        if (source == learningsSource) return "learning";
        if (rel.StartsWith("patterns/")) return "pattern";
        if (rel is "decisions.md" || rel.StartsWith("decisions/")) return "decision";
        if (rel is "conventions.md" || rel.StartsWith("conventions/")) return "convention";
        if (rel is "caveats.md") return "caveat";
        if (rel is "backlog.md") return "backlog";
        if (rel.StartsWith("scopes/")) return "scope";
        if (rel.StartsWith("state/")) return "state";
        if (rel.StartsWith("docs/")) return "doc";
        return "doc";
    }

    internal static string ScopeOf(string rel)
    {
        // learnings layout: first path segment is the scope bucket ("global", or a
        // per-project slug). File at the root → "global".
        var i = rel.IndexOf('/');
        return i > 0 ? rel[..i] : "global";
    }

    internal static string ExtractTitle(string body, string rel)
    {
        foreach (var line in body.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("# ")) return t[2..].Trim();
        }
        return System.IO.Path.GetFileNameWithoutExtension(rel);
    }

    internal static string Sha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task GitAsync(string? cwd, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (cwd is not null) psi.WorkingDirectory = cwd;
        // Never prompt for credentials interactively (fail fast instead of hang).
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git failed to start");
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync(ct);
            // Scrub a leaked token from any error text before it can reach a log.
            if (!string.IsNullOrEmpty(_token)) err = err.Replace(_token, "***");
            throw new InvalidOperationException($"git {args[0]} exit {p.ExitCode}: {err.Trim()}");
        }
    }
}
