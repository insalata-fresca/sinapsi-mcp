using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Sinapsi.Forge.Tools;

/// <summary>Common release + tag tools, including binary asset upload.</summary>
[McpServerToolType]
public sealed class ReleaseTools
{
    [McpServerTool(Name = "list_releases", ReadOnly = true)]
    [Description("List a repository's releases.")]
    public static Task<object> ListReleases(IForgeClient forge, string owner, string repo, int limit = 30, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidateLimit(limit),
            async () => await forge.ListReleasesAsync(owner, repo, limit, ct));

    [McpServerTool(Name = "get_latest_release", ReadOnly = true)]
    [Description("Get the latest (non-draft) release.")]
    public static Task<object> GetLatestRelease(IForgeClient forge, string owner, string repo, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo),
            async () => await forge.GetLatestReleaseAsync(owner, repo, ct));

    [McpServerTool(Name = "create_release", Destructive = false)]
    [Description("Create a release for a tag (created if missing, from target_commitish).")]
    public static Task<object> CreateRelease(IForgeClient forge, string owner, string repo,
        [Description("Tag name.")] string tag_name,
        [Description("Release title.")] string? name = null,
        [Description("Release notes (markdown).")] string? body = null,
        [Description("Target branch/commit if the tag must be created.")] string? target_commitish = null,
        bool draft = false, bool prerelease = false, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidateRef(tag_name, "tag_name"),
            async () => await forge.CreateReleaseAsync(owner, repo, new(tag_name, name, body, target_commitish, draft, prerelease), ct));

    [McpServerTool(Name = "upload_release_asset", Destructive = false)]
    [Description("Upload a binary asset to a release. Provide EXACTLY ONE source: " +
        "source_path (a file path on the MCP host's filesystem — best for large/20MB+ binaries), " +
        "source_url (an http(s) URL the MCP fetches), or content_base64 (inline base64 — small files only).")]
    public static Task<object> UploadReleaseAsset(IForgeClient forge, string owner, string repo,
        [Description("Release id (from create_release/list_releases).")] long release_id,
        [Description("Asset file name.")] string name,
        [Description("Asset bytes, base64-encoded. Small files only; omit when using source_path/source_url.")] string? content_base64 = null,
        [Description("Path to a file on the MCP host's filesystem to stream as the asset (20MB+ binaries).")] string? source_path = null,
        [Description("http(s) URL the MCP fetches and streams as the asset.")] string? source_url = null,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(release_id, "release_id") ?? SinapsiForgeValidation.ValidateText(name, "name", 255),
            async () => await forge.UploadReleaseAssetAsync(owner, repo, release_id, name, content_base64, source_path, source_url, ct));

    [McpServerTool(Name = "get_release", ReadOnly = true)]
    [Description("Get a release by its numeric id.")]
    public static Task<object> GetRelease(IForgeClient forge, string owner, string repo,
        [Description("Release id.")] long release_id, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(release_id, "release_id"),
            async () => await forge.GetReleaseAsync(owner, repo, release_id, ct));

    [McpServerTool(Name = "get_release_by_tag", ReadOnly = true)]
    [Description("Get a release by its tag name. Unlike get_latest_release, this finds drafts and prereleases too.")]
    public static Task<object> GetReleaseByTag(IForgeClient forge, string owner, string repo,
        [Description("Tag name.")] string tag, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidateRef(tag, "tag"),
            async () => await forge.GetReleaseByTagAsync(owner, repo, tag, ct));

    [McpServerTool(Name = "edit_release", Destructive = true)]
    [Description("Edit a release: rename, change notes, retarget, or flip draft/prerelease. " +
        "Set draft=false to PUBLISH a draft; draft=true to unpublish back to a draft.")]
    public static Task<object> EditRelease(IForgeClient forge, string owner, string repo,
        [Description("Release id (from create_release/list_releases).")] long release_id,
        [Description("New tag name.")] string? tag_name = null,
        [Description("New target branch/commit (only honoured while the release is a draft).")] string? target_commitish = null,
        [Description("New release title.")] string? name = null,
        [Description("New release notes (markdown).")] string? body = null,
        [Description("Set false to publish a draft; true to convert to a draft.")] bool? draft = null,
        [Description("Mark as prerelease (true) or full release (false).")] bool? prerelease = null,
        CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(release_id, "release_id"),
            async () => await forge.EditReleaseAsync(owner, repo, release_id, new(tag_name, target_commitish, name, body, draft, prerelease), ct));

    [McpServerTool(Name = "delete_release", Destructive = true)]
    [Description("Delete a release by id. Does NOT delete the underlying git tag. Irreversible.")]
    public static Task<object> DeleteRelease(IForgeClient forge, string owner, string repo,
        [Description("Release id.")] long release_id, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(release_id, "release_id"),
            async () =>
            {
                await forge.DeleteReleaseAsync(owner, repo, release_id, ct);
                return new { ok = true, deleted_release = release_id };
            });

    [McpServerTool(Name = "list_release_assets", ReadOnly = true)]
    [Description("List the assets (uploaded files) attached to a release.")]
    public static Task<object> ListReleaseAssets(IForgeClient forge, string owner, string repo,
        [Description("Release id.")] long release_id, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(release_id, "release_id"),
            async () => await forge.ListReleaseAssetsAsync(owner, repo, release_id, ct));

    [McpServerTool(Name = "edit_release_asset", Destructive = true)]
    [Description("Rename a release asset.")]
    public static Task<object> EditReleaseAsset(IForgeClient forge, string owner, string repo,
        [Description("Release id.")] long release_id,
        [Description("Asset id (from list_release_assets/upload_release_asset).")] long asset_id,
        [Description("New asset file name.")] string name, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(release_id, "release_id") ?? SinapsiForgeValidation.ValidatePositiveId(asset_id, "asset_id") ?? SinapsiForgeValidation.ValidateText(name, "name", 255),
            async () => await forge.EditReleaseAssetAsync(owner, repo, release_id, asset_id, name, ct));

    [McpServerTool(Name = "delete_release_asset", Destructive = true)]
    [Description("Delete a single asset from a release. Irreversible.")]
    public static Task<object> DeleteReleaseAsset(IForgeClient forge, string owner, string repo,
        [Description("Release id.")] long release_id,
        [Description("Asset id.")] long asset_id, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidatePositiveId(release_id, "release_id") ?? SinapsiForgeValidation.ValidatePositiveId(asset_id, "asset_id"),
            async () =>
            {
                await forge.DeleteReleaseAssetAsync(owner, repo, release_id, asset_id, ct);
                return new { ok = true, deleted_asset = asset_id };
            });

    [McpServerTool(Name = "list_tags", ReadOnly = true)]
    [Description("List a repository's tags.")]
    public static Task<object> ListTags(IForgeClient forge, string owner, string repo, int limit = 30, CancellationToken ct = default)
        => ForgeToolGuard.RunAsync(
            () => SinapsiForgeValidation.ValidateOwnerRepo(owner, repo) ?? SinapsiForgeValidation.ValidateLimit(limit),
            async () => await forge.ListTagsAsync(owner, repo, limit, ct));
}
