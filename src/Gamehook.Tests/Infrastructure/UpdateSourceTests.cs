using Gamehook.Infrastructure.AppUpdate;
using Velopack.Sources;

namespace Gamehook.Tests.Infrastructure;

public class UpdateSourceTests
{
    [Test]
    public void Default_source_matches_release_publishing() =>
        Assert.That(UpdateSourceFactory.Create(null), Is.TypeOf<GithubSource>());

    [Test]
    public void Custom_https_feed_is_supported() =>
        Assert.That(UpdateSourceFactory.Create("https://updates.example.com/releases"), Is.TypeOf<SimpleWebSource>());

    [TestCase("http://updates.example.com/releases")]
    [TestCase("file:///tmp/releases")]
    [TestCase("https://user:password@updates.example.com/releases")]
    [TestCase("not a URL")]
    public void Unsafe_feed_is_rejected(string url) =>
        Assert.Throws<ArgumentException>(() => UpdateSourceFactory.Create(url));
}
