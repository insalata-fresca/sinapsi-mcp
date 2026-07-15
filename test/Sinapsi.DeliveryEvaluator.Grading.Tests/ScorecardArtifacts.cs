namespace Sinapsi.DeliveryEvaluator.Grading.Tests;

/// <summary>Locates the committed scorecard artifact directory by walking up to the repo root
/// (the folder holding <c>Sinapsi.Mcp.sln</c>). Used only by the regen path.</summary>
internal static class ScorecardArtifacts
{
    public static string Dir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Sinapsi.Mcp.sln")))
            d = d.Parent;
        if (d is null)
            throw new DirectoryNotFoundException("could not locate repo root (Sinapsi.Mcp.sln) from " + AppContext.BaseDirectory);
        return Path.Combine(d.FullName, "scorecards", "b2-delivery-evaluator");
    }
}
