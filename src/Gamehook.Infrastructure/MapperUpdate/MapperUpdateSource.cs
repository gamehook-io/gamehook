namespace Gamehook.Infrastructure.MapperUpdate;

public enum MapperUpdateSource
{
    // Fetches a small JSON manifest from gamehook.io (a site the user controls) to resolve the
    // latest commit on the tracked branch, instead of calling api.github.com directly - avoids
    // its 60/hr unauthenticated rate limit on that lookup.
    Proxy,

    // Talks to api.github.com directly to resolve the latest commit on the tracked branch.
    Direct,
}
