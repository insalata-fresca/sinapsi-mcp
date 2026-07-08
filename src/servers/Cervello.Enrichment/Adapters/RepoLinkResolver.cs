using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="ILinkResolver"/> that resolves whether a <c>[[slug]]</c> targets an existing
/// dossier by probing the CT-local <c>ste/cervello</c> working tree for
/// <c>map/{people,threads,projects}/&lt;slug&gt;.md</c> (SCHEMAS §1 path resolver; lint R4). A
/// proposed link that does not resolve is declared as a <c>stub: true</c> file in the same PR by
/// the graph-writer. Mirrors the <c>FakeLinkResolver</c> contract (existence check only).
/// </summary>
public sealed class RepoLinkResolver : ILinkResolver
{
    private static readonly string[] MapDirs = ["people", "threads", "projects"];

    private readonly string _repoRoot;

    public RepoLinkResolver(string repoWorkingTree)
    {
        if (string.IsNullOrWhiteSpace(repoWorkingTree))
            throw new ArgumentException("repoWorkingTree must be non-empty", nameof(repoWorkingTree));
        _repoRoot = repoWorkingTree;
    }

    public Task<bool> DossierExistsAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug)) return Task.FromResult(false);
        foreach (var dir in MapDirs)
            if (File.Exists(Path.Combine(_repoRoot, "map", dir, $"{slug}.md")))
                return Task.FromResult(true);
        return Task.FromResult(false);
    }
}
