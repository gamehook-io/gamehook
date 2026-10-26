using System.Net;
using System.Net.Sockets;
using Gamehook.Domain;
using Gamehook.Infrastructure;
using Gamehook.RestApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gamehook.Tests.Tests;

// The hosted overlays (https://overlays.gamehook.io) call this loopback API from a public page,
// including from the Android app's WebView. Chromium-based browsers then send a Private Network
// Access preflight and refuse the request unless the API answers it.
public sealed class PrivateNetworkAccessTests
{
    [Test]
    public async Task Preflight_asking_for_private_network_access_is_granted_only_when_asked()
    {
        var directory = Directory.CreateTempSubdirectory("gamehook-pna-");
        try
        {
            int port;
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Port"] = port.ToString() })
                .Build();
            using var instances = new GamehookInstances(() => new GamehookSession(
                new ContinuousReadModeTests.StubMapperFactory(new ContinuousReadModeTests.CountingMapper())));
            var api = new GamehookApiHostedService(
                instances, [], new FilesystemProvider(configuration, directory.FullName, officialMappersEnabled: true), configuration,
                NullLoggerFactory.Instance, new ApiBindStatus(), new SettingsService(instances, initialContinuousRead: true));
            await api.StartAsync(CancellationToken.None);
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

                HttpRequestMessage Preflight(bool privateNetwork)
                {
                    var request = new HttpRequestMessage(HttpMethod.Options, "/instances");
                    request.Headers.Add("Origin", "https://overlays.gamehook.io");
                    request.Headers.Add("Access-Control-Request-Method", "GET");
                    if (privateNetwork) request.Headers.Add("Access-Control-Request-Private-Network", "true");
                    return request;
                }

                var granted = await http.SendAsync(Preflight(privateNetwork: true));
                var plain = await http.SendAsync(Preflight(privateNetwork: false));

                Assert.Multiple(() =>
                {
                    Assert.That(granted.Headers.GetValues("Access-Control-Allow-Private-Network").Single(), Is.EqualTo("true"));
                    Assert.That(granted.Headers.GetValues("Access-Control-Allow-Origin").Single(), Is.EqualTo("*"));
                    Assert.That(plain.Headers.Contains("Access-Control-Allow-Private-Network"), Is.False);
                });
            }
            finally
            {
                await api.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
