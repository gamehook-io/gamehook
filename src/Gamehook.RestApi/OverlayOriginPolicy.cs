using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gamehook.RestApi;

/// Which web origins may use the overlay settings endpoints (/kv/overlays). Browsers always send an Origin
/// header on cross-origin requests and a web page cannot forge or remove it, so checking it here
/// stops other websites from reading or writing overlay settings through the user's browser.
/// Requests without an Origin (curl, native tools, same-origin navigation) are not browser
/// cross-site requests and are allowed. Loopback origins (localhost, 127.0.0.1, [::1], any port)
/// are always allowed so locally hosted overlays and dev servers work. An allowed origin may start
/// its host with "*." (https://*.example.org) to allow every subdomain, but not the bare domain,
/// which needs its own entry.
public sealed class OverlayOriginPolicy
{
    public const string CorsPolicyName = "overlay-settings";
    public const string ConfigurationKey = "OverlaySettingsOrigins";
    public static readonly string[] DefaultOrigins = ["https://gamehook.io", "https://*.gamehook.io"];

    private readonly HashSet<string> allowed = new(StringComparer.Ordinal);
    // "scheme://:port" plus the ".domain" every subdomain host must end with.
    private readonly List<(string Scheme, int Port, string Suffix)> wildcards = [];

    public OverlayOriginPolicy(IEnumerable<string> origins)
    {
        foreach (var origin in origins)
        {
            var wildcard = origin.Contains("//*.", StringComparison.Ordinal);
            if (!TryParse(wildcard ? origin.Replace("//*.", "//", StringComparison.Ordinal) : origin, out var uri)) continue;
            if (wildcard) wildcards.Add((uri.Scheme, uri.Port, "." + uri.Host));
            else allowed.Add(Normalize(uri));
        }
    }

    public static OverlayOriginPolicy FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration.GetSection(ConfigurationKey).GetChildren().Select(child => child.Value).OfType<string>().ToArray();
        return new OverlayOriginPolicy(configured.Length > 0 ? configured : DefaultOrigins);
    }

    public bool IsAllowed(string? origin)
    {
        if (!TryParse(origin, out var uri)) return false;
        return IsLoopback(uri.Host) || allowed.Contains(Normalize(uri)) ||
            wildcards.Any(rule => rule.Scheme == uri.Scheme && rule.Port == uri.Port && uri.Host.EndsWith(rule.Suffix, StringComparison.Ordinal));
    }

    // Only a bare scheme://host[:port] origin counts. "null" (file://, sandboxed frames), paths,
    // credentials, and other schemes are refused.
    private static bool TryParse(string? origin, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https") || parsed.UserInfo.Length > 0 || parsed.PathAndQuery != "/" || parsed.Fragment.Length > 0) return false;
        if (origin.EndsWith('/')) return false;
        uri = parsed;
        return true;
    }

    private static string Normalize(Uri uri) => $"{uri.Scheme}://{uri.Host}:{uri.Port}".ToLowerInvariant();

    private static bool IsLoopback(string host) => host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
}

public static class OverlayOriginPolicyExtensions
{
    /// Registers the origin policy and the named CORS policy that /kv/overlays endpoints require, replacing
    /// the API's allow-any-origin default for those routes only.
    public static IServiceCollection AddOverlaySettingsOrigins(this IServiceCollection services, OverlayOriginPolicy policy)
    {
        services.AddSingleton(policy);
        services.AddCors(options => options.AddPolicy(OverlayOriginPolicy.CorsPolicyName, builder => builder
            .SetIsOriginAllowed(policy.IsAllowed)
            .AllowAnyMethod()
            .AllowAnyHeader()));
        return services;
    }
}
