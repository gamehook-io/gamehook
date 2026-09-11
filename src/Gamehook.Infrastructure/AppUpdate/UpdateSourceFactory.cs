using Velopack.Sources;

namespace Gamehook.Infrastructure.AppUpdate;

public static class UpdateSourceFactory
{
    public const string RepositoryUrl = "https://github.com/gamehook-io/gamehook";

    public static IUpdateSource Create(string? feedUrl)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
            return new GithubSource(RepositoryUrl, null, prerelease: false);

        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("The update feed must be an HTTPS URL without embedded credentials.", nameof(feedUrl));

        return new SimpleWebSource(uri);
    }
}
