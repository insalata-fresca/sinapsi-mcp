using Cervello.Watcher;
using Cervello.Watcher.Drive;
using Cervello.Watcher.Ingest;
using Cervello.Watcher.Normalize;
using Cervello.Watcher.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Watcher.Tests;

/// <summary>Disposable temp workspace: a staging dir + a manifest path, cleaned up on Dispose.</summary>
public sealed class TempWorkspace : IDisposable
{
    public string Root { get; }
    public string StagingDir { get; }
    public string InboxDir { get; }
    public string ManifestPath { get; }

    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "cervello-wtest-" + Guid.NewGuid().ToString("N"));
        StagingDir = Path.Combine(Root, "staging");
        InboxDir = Path.Combine(Root, "inbox");
        ManifestPath = Path.Combine(Root, "repo", "recordings", "manifest.yaml");
        Directory.CreateDirectory(StagingDir);
        Directory.CreateDirectory(InboxDir);
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}

/// <summary>Builds a fully-wired WatchWorker over fakes + a temp workspace for cycle tests.</summary>
public sealed class WorkerHarness
{
    public FakeDriveClient Drive { get; } = new();
    public InMemoryStateStore State { get; } = new();
    public BlobStore Blobs { get; }
    public IdempotencyLedger Ledger { get; }
    public Downloader Downloader { get; }
    public Normalizer Normalizer { get; } = new();
    public YamlManifestStore Manifest { get; }
    public ReadyMarker Ready { get; }
    public WatchWorker Worker { get; }
    public TempWorkspace Ws { get; }
    public WatcherConfig Config { get; }

    public WorkerHarness(TempWorkspace ws, string folderId = "folder-recordings")
    {
        Ws = ws;
        // Build config from an EMPTY local env map (all defaults) — never touches the
        // process environment, so nothing another (parallel) test set can leak in here.
        Config = WatcherConfig.From(new Dictionary<string, string?>())
            with { StagingDir = ws.StagingDir, RepoWorkingTree = ws.Root };
        Blobs = new BlobStore(ws.StagingDir);
        Ledger = new IdempotencyLedger(State, NullLogger<IdempotencyLedger>.Instance);
        Downloader = new Downloader(Drive, Blobs, Ledger, NullLogger<Downloader>.Instance);
        Manifest = new YamlManifestStore(ws.ManifestPath);
        Ready = new ReadyMarker(ws.InboxDir);
        Worker = new WatchWorker(
            Config, Drive, State, Downloader, Normalizer, Manifest, Ready,
            NullLogger<WatchWorker>.Instance);
        Worker.SetFolderId(folderId);
    }
}
