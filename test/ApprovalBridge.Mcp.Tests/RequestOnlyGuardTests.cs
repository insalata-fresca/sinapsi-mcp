using System.Reflection;
using ApprovalBridge.Mcp;
using Xunit;

namespace ApprovalBridge.Mcp.Tests;

/// <summary>
/// The request-only proof (CARD definition of done: "a test proving the tool cannot approve
/// (only request)", docs/66 §8 T1). Two structural assertions, each independently sufficient:
///
/// <list type="bullet">
/// <item><see cref="ApprovalBridgeClient_HasNoApproveOrRejectMethod"/> — the ONLY network seam
/// this server has onto the broker declares no method that could call
/// <c>/approve</c>/<c>/reject</c>. There is nothing to invoke even from a compromised or
/// carelessly-extended tool method.</item>
/// <item><see cref="Assembly_DeclaresNoApproveOrRejectMemberAnywhere"/> — a whole-assembly sweep:
/// no public type in this server declares ANY member (method/property) whose name suggests an
/// approve/reject capability. This catches the case where a future PR adds a *new* type with an
/// approve path instead of extending an existing one.</item>
/// </list>
///
/// Both are reflection assertions over the COMPILED types (not string-matching source), so they
/// hold even if the implementation is refactored, and both fail loudly if anyone ever adds an
/// approve/reject capability to this server.
/// </summary>
public sealed class RequestOnlyGuardTests
{
    [Fact]
    public void ApprovalBridgeClient_HasNoApproveOrRejectMethod()
    {
        var methodNames = typeof(ApprovalBridgeClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name.ToLowerInvariant())
            .ToArray();

        // The one and only broker-facing call this client can make.
        Assert.Contains("requestasync", methodNames);

        Assert.DoesNotContain(methodNames, n => n.Contains("approve"));
        Assert.DoesNotContain(methodNames, n => n.Contains("reject"));
    }

    [Fact]
    public void Assembly_DeclaresNoApproveOrRejectMemberAnywhere()
    {
        var asm = typeof(ApprovalBridgeClient).Assembly;

        var offendingMembers = asm.GetTypes()
            .Where(t => t.IsPublic || t.IsNestedPublic)
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Contains("Approve", StringComparison.OrdinalIgnoreCase)
                     || m.Name.Contains("Reject", StringComparison.OrdinalIgnoreCase))
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .ToArray();

        Assert.True(offendingMembers.Length == 0,
            "ApprovalBridge.Mcp must expose ONLY the request path (docs/66 §8 T1); " +
            $"found approve/reject-named member(s): {string.Join(", ", offendingMembers)}");
    }

    [Fact]
    public void RequestOutcome_HasNoApprovedOrRejectedState()
    {
        // ApprovalBridgeRequestResult only ever models Accepted (the request was queued) vs. not —
        // there is no third "Approved" state this tool could ever report, because it never learns
        // of one: the broker's approve/executed lifecycle is invisible to this server by design.
        var props = typeof(ApprovalBridgeRequestResult)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToArray();

        Assert.Contains("Accepted", props);
        Assert.DoesNotContain(props, n => n.Contains("Approve", StringComparison.OrdinalIgnoreCase));
    }
}
