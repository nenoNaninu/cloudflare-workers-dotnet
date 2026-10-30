using System.Text.Json.Serialization;

namespace Cloudflare.Workers.Hosting;

/// <summary>Options for acquiring or launching a Browser Run session.</summary>
public sealed record BrowserAcquireOptions
{
    /// <summary>Inactivity timeout in milliseconds (10,000 to 1,200,000).</summary>
    public double? KeepAlive { get; init; }
    public bool? Recording { get; init; }
    public string? Location { get; init; }
    public bool? Targets { get; init; }
    public double? LiveViewUrlExpiresInMs { get; init; }
    public BrowserGuardrails? Guardrails { get; init; }

    /// <summary>Worker bindings that handle outbound requests for each hostname.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ServiceBinding>? OutboundByHost { get; init; }
}

public sealed record BrowserGuardrails
{
    /// <summary>Allowed hostname patterns. An empty list denies all hostnames.</summary>
    public string[]? AllowedDomains { get; init; }
    public string[]? AllowedDomainSets { get; init; }
}

public sealed record BrowserConnectOptions
{
    public string? TargetId { get; init; }
}

public sealed record BrowserLiveViewOptions
{
    /// <summary>One of "devtools", "tab", or "full".</summary>
    public string? Mode { get; init; }
    public string? TargetId { get; init; }
    public double? ExpiresInMs { get; init; }
    public BrowserLiveViewGuardrails? Guardrails { get; init; }
}

public sealed record BrowserLiveViewGuardrails
{
    public string Mode { get; init; } = "readonly";
}

/// <summary>Pagination options for active sessions and session history.</summary>
public sealed record BrowserListOptions
{
    public int? Limit { get; init; }
    public int? Offset { get; init; }
}

public sealed record BrowserTargetOptions
{
    public double? LiveViewUrlExpiresInMs { get; init; }
}

public sealed record BrowserAcquireResult(string SessionId, BrowserTarget[]? Targets = null);

public sealed record BrowserSession(
    string SessionId,
    double? StartTime = null,
    double? EndTime = null,
    int? CloseReason = null,
    string? CloseReasonText = null,
    string? ConnectionId = null,
    double? ConnectionStartTime = null,
    double? ConnectionEndTime = null,
    double? LastUpdated = null,
    string? WebSocketDebuggerUrl = null,
    string? DevtoolsFrontendUrl = null);

public sealed record BrowserActiveSession(string Id);

public sealed record BrowserLimits(
    BrowserActiveSession[] ActiveSessions,
    double MaxConcurrentSessions,
    double AllowedBrowserAcquisitions,
    double TimeUntilNextAllowedBrowserAcquisition,
    double? UsedBrowserTimeSeconds = null);

public sealed record BrowserTarget(
    string Id,
    string Type,
    string Url,
    string? Title = null,
    string? Description = null,
    string? WebSocketDebuggerUrl = null,
    string? DevtoolsFrontendUrl = null);

public sealed record BrowserVersion(
    [property: JsonPropertyName("Browser")] string Browser,
    [property: JsonPropertyName("Protocol-Version")] string ProtocolVersion,
    [property: JsonPropertyName("User-Agent")] string UserAgent,
    [property: JsonPropertyName("V8-Version")] string V8Version,
    [property: JsonPropertyName("WebKit-Version")] string WebKitVersion,
    string WebSocketDebuggerUrl);

/// <summary>Authenticated Live View URLs. Treat these URLs as credentials.</summary>
public sealed record BrowserLiveView(
    string WebSocketDebuggerUrl,
    string DevtoolsFrontendUrl,
    string Id,
    BrowserLiveViewOptions Options);

/// <param name="Status">Either "closing" or "closed".</param>
public sealed record BrowserCloseSessionResult(string Status);

public sealed record BrowserTargetActionResult(string Message);
