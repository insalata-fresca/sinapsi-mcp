// Fail-closed configuration matrix for the server-hosting helpers. Proves that a
// bad listen-address env var (non-numeric / out-of-range port, whitespace/control
// in host) THROWS naming the offending env var rather than silently composing an
// unbindable listen URL, and that the public MapSinapsiMcp / AddSinapsiMcpServer
// seams reject malformed code inputs. This is the fail-closed leg -- it asserts
// the guard actually throws, not that it silently defaults a footgun. Banners are
// plain ASCII so the file diffs as text.
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using Sinapsi.Mcp;
using Xunit;

namespace Sinapsi.Mcp.Tests;

public class McpConfigFailClosedTests
{
    // ---- McpValidation.ReadPort (fail-closed) ---------------------------------

    [Fact]
    public void ReadPort_returns_default_when_unset()
    {
        Assert.Equal(9214, McpValidation.ReadPort("SINAPSI_MCP_UNSET_PORT_XYZ", 9214));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("99999999")]
    public void ReadPort_throws_naming_var_on_invalid(string raw)
    {
        const string var = "SINAPSI_MCP_TEST_PORT";
        Environment.SetEnvironmentVariable(var, raw);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => McpValidation.ReadPort(var, 9200));
            Assert.Contains(var, ex.Message);
        }
        finally { Environment.SetEnvironmentVariable(var, null); }
    }

    [Fact]
    public void ReadPort_accepts_valid_configured_value()
    {
        const string var = "SINAPSI_MCP_TEST_PORT_OK";
        Environment.SetEnvironmentVariable(var, "12345");
        try { Assert.Equal(12345, McpValidation.ReadPort(var, 9200)); }
        finally { Environment.SetEnvironmentVariable(var, null); }
    }

    // ---- McpValidation.ReadHost (fail-closed) ---------------------------------

    [Fact]
    public void ReadHost_returns_default_when_unset()
    {
        Assert.Equal("0.0.0.0", McpValidation.ReadHost("SINAPSI_MCP_UNSET_HOST_XYZ", "0.0.0.0"));
    }

    [Theory]
    [InlineData("bad host")]     // whitespace
    [InlineData("host\twith")]   // tab
    [InlineData("host\nline")]   // newline
    public void ReadHost_throws_naming_var_on_malformed(string raw)
    {
        const string var = "SINAPSI_MCP_TEST_HOST";
        Environment.SetEnvironmentVariable(var, raw);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => McpValidation.ReadHost(var, "0.0.0.0"));
            Assert.Contains(var, ex.Message);
        }
        finally { Environment.SetEnvironmentVariable(var, null); }
    }

    // ---- envPrefix / defaultPort (public seam inputs) -------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("bad-prefix")] // dash not allowed
    public void RequireEnvPrefix_rejects_malformed(string? prefix)
    {
        var ex = Assert.Throws<ArgumentException>(() => McpValidation.RequireEnvPrefix(prefix));
        Assert.Equal("envPrefix", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(70000)]
    public void RequireDefaultPort_rejects_out_of_range(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => McpValidation.RequireDefaultPort(port));
    }

    // ---- end-to-end through MapSinapsiMcp -------------------------------------

    [Fact]
    public void MapSinapsiMcp_throws_when_configured_port_is_invalid()
    {
        Environment.SetEnvironmentVariable("FAILCLOSED_PROBE_PORT", "not-a-port");
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.AddSinapsiMcpServer("example-mcp", "0.1.0").WithHttpTransport();
            using var app = builder.Build();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                app.MapSinapsiMcp(envPrefix: "FAILCLOSED_PROBE", defaultPort: 9200));
            Assert.Contains("FAILCLOSED_PROBE_PORT", ex.Message);
        }
        finally { Environment.SetEnvironmentVariable("FAILCLOSED_PROBE_PORT", null); }
    }

    [Fact]
    public void MapSinapsiMcp_rejects_malformed_env_prefix()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddSinapsiMcpServer("example-mcp", "0.1.0").WithHttpTransport();
        using var app = builder.Build();

        Assert.Throws<ArgumentException>(() =>
            app.MapSinapsiMcp(envPrefix: "bad prefix", defaultPort: 9200));
    }

    // ---- AddSinapsiMcpServer input validation ---------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AddSinapsiMcpServer_rejects_blank_name(string? name)
    {
        var builder = WebApplication.CreateBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddSinapsiMcpServer(name!, "0.1.0"));
    }

    [Fact]
    public void AddSinapsiMcpServer_rejects_control_char_version()
    {
        var builder = WebApplication.CreateBuilder();
        var ex = Assert.Throws<ArgumentException>(() => builder.AddSinapsiMcpServer("example-mcp", "0.1\n0"));
        Assert.Equal("serverVersion", ex.ParamName);
    }
}
