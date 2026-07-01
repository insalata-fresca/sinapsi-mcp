namespace Sinapsi.Mcp;

// Validation for the library's public-API inputs and for the env-driven
// configuration the server-hosting helpers bind. Every public entry point
// (CallToolAsync, AddSinapsiMcpServer, MapSinapsiMcp) funnels its caller-
// supplied arguments through here BEFORE they are used, so a malformed value is
// rejected with a clear, named reason instead of producing an opaque downstream
// failure (a bad Uri handed to HttpClient, a stray control char smuggled into a
// header, a non-numeric port silently building an unbindable listen URL).
//
// The checks are additive fail-fast guards: they throw ArgumentException /
// InvalidOperationException naming the offending parameter or env var. They do
// not change any existing success-path behaviour.
internal static class McpValidation
{
    // A bearer token / tool name is bounded so a pathological caller value can
    // neither blow up a header nor be smuggled past size checks. 8 KiB is far
    // past any real JWT; a tool name is a short identifier.
    internal const int MaxBearerLength = 8_192;
    internal const int MaxToolNameLength = 512;

    // An env-var prefix is a short uppercase identifier.
    internal const int MaxEnvPrefixLength = 128;

    // TCP port range (1..65535); 0 is "pick any" and is not a valid listen port.
    internal const int MinPort = 1;
    internal const int MaxPort = 65535;

    // Validate the upstream gateway Uri: must be a non-null, absolute http/https
    // URI. A relative or non-HTTP scheme could never be a valid MCP endpoint and
    // would fail opaquely inside HttpClient.
    internal static Uri RequireGateway(Uri? gateway)
    {
        if (gateway is null)
            throw new ArgumentNullException(nameof(gateway), "gateway MCP endpoint is required");
        if (!gateway.IsAbsoluteUri)
            throw new ArgumentException("gateway must be an absolute URI", nameof(gateway));
        if (gateway.Scheme != Uri.UriSchemeHttp && gateway.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException(
                $"gateway scheme '{gateway.Scheme}' is not supported (expected http or https)",
                nameof(gateway));
        return gateway;
    }

    // Validate the bearer identity: required, length-capped, no control chars
    // (a newline or NUL would corrupt the Authorization header).
    internal static string RequireBearer(string? bearerJwt)
    {
        if (string.IsNullOrWhiteSpace(bearerJwt))
            throw new ArgumentException("bearerJwt is required", nameof(bearerJwt));
        if (bearerJwt.Length > MaxBearerLength)
            throw new ArgumentException(
                $"bearerJwt too long ({bearerJwt.Length} chars; max {MaxBearerLength})",
                nameof(bearerJwt));
        if (ContainsControl(bearerJwt))
            throw new ArgumentException("bearerJwt contains control characters", nameof(bearerJwt));
        return bearerJwt;
    }

    // Validate the tool name: required, length-capped, no control chars.
    internal static string RequireToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("toolName is required", nameof(toolName));
        if (toolName.Length > MaxToolNameLength)
            throw new ArgumentException(
                $"toolName too long ({toolName.Length} chars; max {MaxToolNameLength})",
                nameof(toolName));
        if (ContainsControl(toolName))
            throw new ArgumentException("toolName contains control characters", nameof(toolName));
        return toolName;
    }

    // Validate a non-empty, control-char-free string argument (server name /
    // version). The parameter name is supplied so the thrown message names the
    // offending public argument.
    internal static string RequireText(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} is required", paramName);
        if (ContainsControl(value))
            throw new ArgumentException($"{paramName} contains control characters", paramName);
        return value;
    }

    // Validate the env-var prefix used to bind the listen address: required,
    // length-capped, and restricted to the [A-Z0-9_] shape a real env-var prefix
    // takes, so it can be safely composed into "<prefix>_HOST" / "<prefix>_PORT".
    internal static string RequireEnvPrefix(string? envPrefix)
    {
        if (string.IsNullOrWhiteSpace(envPrefix))
            throw new ArgumentException("envPrefix is required", nameof(envPrefix));
        if (envPrefix.Length > MaxEnvPrefixLength)
            throw new ArgumentException(
                $"envPrefix too long ({envPrefix.Length} chars; max {MaxEnvPrefixLength})",
                nameof(envPrefix));
        foreach (var c in envPrefix)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ArgumentException(
                    "envPrefix must contain only A-Z, 0-9 and underscore", nameof(envPrefix));
        return envPrefix;
    }

    // Validate a default port supplied in code (a programming input, hence
    // ArgumentOutOfRangeException).
    internal static int RequireDefaultPort(int defaultPort)
    {
        if (defaultPort is < MinPort or > MaxPort)
            throw new ArgumentOutOfRangeException(
                nameof(defaultPort), defaultPort,
                $"defaultPort must be in {MinPort}..{MaxPort}");
        return defaultPort;
    }

    // Fail-closed read of a listen port from an env var. When the var is unset,
    // the validated default is used. When it is set but not a valid TCP port
    // (non-numeric, <= 0, or out of range) we THROW naming the env var rather
    // than silently building an unbindable listen URL from garbage.
    internal static int ReadPort(string envVar, int defaultPort)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
            return defaultPort;
        if (!int.TryParse(raw, out var port) || port is < MinPort or > MaxPort)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected a TCP port in {MinPort}..{MaxPort}.");
        return port;
    }

    // Read a listen host from an env var, falling back to the given default when
    // unset. A configured host is rejected if it carries control characters or
    // whitespace that could corrupt the composed listen URL.
    internal static string ReadHost(string envVar, string defaultHost)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(raw))
            return defaultHost;
        if (ContainsControl(raw) || raw.Any(char.IsWhiteSpace))
            throw new InvalidOperationException(
                $"{envVar} is invalid: host must not contain whitespace or control characters.");
        return raw;
    }

    private static bool ContainsControl(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }
}
