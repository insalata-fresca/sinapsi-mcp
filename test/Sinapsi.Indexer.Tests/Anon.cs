// ---------------------------------------------------------------------------
// Anon - reflection helpers to read fields off the anonymous result objects the
// indexer tools return ({ error = ... } / { results = ... } / { published = ... }).
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Reflection;

namespace Sinapsi.Indexer.Tests;

internal static class Anon
{
    /// <summary>Read a named property off an anonymous result object, or null.</summary>
    internal static object? Prop(object result, string name) =>
        result.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(result);

    /// <summary>The tool's <c>error</c> string, or null when the result has no error.</summary>
    internal static string? Error(object result) => Prop(result, "error") as string;

    /// <summary>True when the result carries a non-empty <c>error</c> field.</summary>
    internal static bool HasError(object result) => !string.IsNullOrEmpty(Error(result));
}
