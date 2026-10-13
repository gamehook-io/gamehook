using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Gamehook.Infrastructure;
using Gamehook.RestApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gamehook.Tests.Tests;

public sealed class OverlayKeyValueTests
{
    private const string Id = "215a7b0c-593a-4a62-9f76-e776e7992d68";

    private DirectoryInfo profile = null!;
    private WebApplication app = null!;
    private HttpClient http = null!;

    [SetUp]
    public async Task SetUp()
    {
        profile = Directory.CreateTempSubdirectory("gamehook-kv-");
        var filesystem = new FilesystemProvider(new ConfigurationBuilder().Build(), profile.FullName, officialMappersEnabled: false);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton(new OverlayKeyValueStore(filesystem));
        builder.Services.AddOverlaySettingsOrigins(new OverlayOriginPolicy(["https://gamehook.io", "https://*.gamehook.io"]));
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        app.UseStatusCodePages();
        app.UseCors();
        app.MapKeyValueEndpoints();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        http = new HttpClient { BaseAddress = new Uri(address) };
    }

    [TearDown]
    public async Task TearDown()
    {
        http.Dispose();
        await app.DisposeAsync();
        profile.Delete(recursive: true);
    }

    private string FileFor(string id) => Path.Combine(profile.FullName, "kv", "overlays", id + ".json");

    private static StringContent Json(string json, string mediaType = "application/json") => new(json, Encoding.UTF8, mediaType);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? origin, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (body is not null) request.Content = Json(body);
        return await http.SendAsync(request);
    }

    [TestCase("https://gamehook.io")]
    [TestCase("https://GAMEHOOK.io")]
    [TestCase("https://overlays.gamehook.io")]
    [TestCase("https://a.b.gamehook.io")]
    [TestCase("http://localhost:5173")]
    [TestCase("http://127.0.0.1:5173")]
    [TestCase("http://[::1]:5173")]
    public async Task Allowed_origins_can_read_and_write_and_get_cors_headers(string origin)
    {
        var write = await SendAsync(HttpMethod.Post, $"/kv/overlays/{Id}", origin, """{"a":1}""");
        var read = await SendAsync(HttpMethod.Get, $"/kv/overlays/{Id}", origin);

        Assert.Multiple(() =>
        {
            Assert.That(write.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(read.Headers.GetValues("Access-Control-Allow-Origin").Single(), Is.EqualTo(origin));
        });
    }

    [TestCase("https://evil.example")]
    [TestCase("https://gamehook.io.evil.example")]
    [TestCase("https://evilgamehook.io")]
    [TestCase("https://sub.gamehook.io.evil.example")]
    [TestCase("https://xgamehook.io")]
    [TestCase("https://-.gamehook.io@evil.example")]
    [TestCase("http://sub.gamehook.io")]
    [TestCase("https://sub.gamehook.io:8443")]
    [TestCase("https://.gamehook.io")]
    [TestCase("http://gamehook.io")]
    [TestCase("https://gamehook.io:8443")]
    [TestCase("https://gamehook.io/")]
    [TestCase("https://user@gamehook.io")]
    [TestCase("https://localhost.evil.example")]
    [TestCase("null")]
    [TestCase("file://")]
    [TestCase("gamehook.io")]
    public async Task Other_origins_are_refused_for_every_method_and_nothing_is_written(string origin)
    {
        await http.PostAsync($"/kv/overlays/{Id}", Json("""{"keep":true}"""));
        var before = File.ReadAllText(FileFor(Id));

        var write = await SendAsync(HttpMethod.Post, $"/kv/overlays/{Id}", origin, """{"keep":false}""");
        var read = await SendAsync(HttpMethod.Get, $"/kv/overlays/{Id}", origin);
        var list = await SendAsync(HttpMethod.Get, "/kv/overlays", origin);

        Assert.Multiple(() =>
        {
            Assert.That(write.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(read.Headers.Contains("Access-Control-Allow-Origin"), Is.False);
            Assert.That(File.ReadAllText(FileFor(Id)), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task Preflight_from_a_disallowed_origin_gets_no_cors_approval()
    {
        var evil = new HttpRequestMessage(HttpMethod.Options, $"/kv/overlays/{Id}");
        evil.Headers.Add("Origin", "https://evil.example");
        evil.Headers.Add("Access-Control-Request-Method", "POST");
        evil.Headers.Add("Access-Control-Request-Headers", "content-type");
        var good = new HttpRequestMessage(HttpMethod.Options, $"/kv/overlays/{Id}");
        good.Headers.Add("Origin", "https://gamehook.io");
        good.Headers.Add("Access-Control-Request-Method", "POST");
        good.Headers.Add("Access-Control-Request-Headers", "content-type");

        var evilResponse = await http.SendAsync(evil);
        var goodResponse = await http.SendAsync(good);

        Assert.Multiple(() =>
        {
            Assert.That(evilResponse.Headers.Contains("Access-Control-Allow-Origin"), Is.False);
            Assert.That(goodResponse.Headers.GetValues("Access-Control-Allow-Origin").Single(), Is.EqualTo("https://gamehook.io"));
        });
    }

    [Test]
    public async Task Requests_without_an_origin_header_are_allowed()
    {
        Assert.That((await SendAsync(HttpMethod.Post, $"/kv/overlays/{Id}", origin: null, """{"a":1}""")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public void Origins_come_from_configuration_with_gamehook_io_as_the_default()
    {
        var configured = OverlayOriginPolicy.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OverlaySettingsOrigins:0"] = "https://example.org" }).Build());
        var defaults = OverlayOriginPolicy.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Multiple(() =>
        {
            Assert.That(configured.IsAllowed("https://example.org"), Is.True);
            Assert.That(configured.IsAllowed("https://gamehook.io"), Is.False);
            Assert.That(configured.IsAllowed("https://sub.example.org"), Is.False);
            Assert.That(defaults.IsAllowed("https://gamehook.io"), Is.True);
            Assert.That(defaults.IsAllowed("https://www.gamehook.io"), Is.True);
        });
    }

    [Test]
    public async Task List_is_empty_until_an_overlay_saves_and_ignores_unrelated_files()
    {
        Assert.That(await http.GetFromJsonAsync<string[]>("/kv/overlays"), Is.Empty);
        Assert.That(await http.GetFromJsonAsync<string[]>("/kv/overlays/"), Is.Empty);

        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json("""{"a":1}"""))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        File.WriteAllText(Path.Combine(profile.FullName, "kv", "overlays", "notes.json"), "{}");
        File.WriteAllText(Path.Combine(profile.FullName, "kv", "overlays", Id.ToUpperInvariant().Replace("-", "") + ".json"), "{}");

        Assert.That(await http.GetFromJsonAsync<string[]>("/kv/overlays"), Is.EqualTo(new[] { Id }));
    }

    [Test]
    public async Task Get_returns_404_before_any_save_then_the_saved_object()
    {
        Assert.That((await http.GetAsync($"/kv/overlays/{Id}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        await http.PostAsync($"/kv/overlays/{Id}", Json("""{"theme":"dark","count":3,"ratio":0.5,"on":true}"""));
        var saved = JsonNode.Parse(await http.GetStringAsync($"/kv/overlays/{Id}"))!;

        Assert.Multiple(() =>
        {
            Assert.That((string?)saved["theme"], Is.EqualTo("dark"));
            Assert.That((int?)saved["count"], Is.EqualTo(3));
            Assert.That((double?)saved["ratio"], Is.EqualTo(0.5));
            Assert.That((bool?)saved["on"], Is.True);
            Assert.That(File.Exists(FileFor(Id)), Is.True);
        });
    }

    [Test]
    public async Task Post_merges_keys_and_null_removes_a_key()
    {
        await http.PostAsync($"/kv/overlays/{Id}", Json("""{"a":1,"b":2}"""));
        var response = await http.PostAsync($"/kv/overlays/{Id}", Json("""{"b":null,"c":3}"""));
        var merged = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Multiple(() =>
        {
            Assert.That(merged.Select(pair => pair.Key), Is.EquivalentTo(new[] { "a", "c" }));
            Assert.That(JsonNode.Parse(File.ReadAllText(FileFor(Id)))!.AsObject().Count, Is.EqualTo(2));
        });
    }

    [TestCase("215A7B0C-593A-4A62-9F76-E776E7992D68-")]
    [TestCase("215a7b0c593a4a629f76e776e7992d68")]
    [TestCase("{215a7b0c-593a-4a62-9f76-e776e7992d68}")]
    [TestCase("..%2F..%2Fescape")]
    [TestCase("not-a-guid")]
    public async Task Ids_other_than_a_hyphenated_guid_are_rejected_and_nothing_is_written(string id)
    {
        Assert.That((await http.GetAsync($"/kv/overlays/{id}")).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await http.PostAsync($"/kv/overlays/{id}", Json("{}"))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(Directory.Exists(Path.Combine(profile.FullName, "kv", "overlays")), Is.False);
    }

    [TestCase("[1,2]")]
    [TestCase("\"text\"")]
    [TestCase("null")]
    [TestCase("{\"a\":1,\"a\":2}")]
    [TestCase("{\"a\":1} trailing")]
    [TestCase("{\"a\":1,}")]
    [TestCase("{\"\":1}")]
    [TestCase("")]
    public async Task Bodies_that_are_not_a_clean_json_object_are_rejected(string body)
    {
        var response = await http.PostAsync($"/kv/overlays/{Id}", Json(body));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(File.Exists(FileFor(Id)), Is.False);
    }

    [Test]
    public async Task Lists_and_nested_objects_round_trip()
    {
        await http.PostAsync($"/kv/overlays/{Id}", Json("""{"hidden":["hp","mp"],"layout":{"left":{"width":400,"items":[1,null,{"x":true}]}},"empty":[],"none":{}}"""));
        var saved = JsonNode.Parse(await http.GetStringAsync($"/kv/overlays/{Id}"))!;

        Assert.Multiple(() =>
        {
            Assert.That(saved["hidden"]!.AsArray().Select(item => (string?)item), Is.EqualTo(new[] { "hp", "mp" }));
            Assert.That((int?)saved["layout"]!["left"]!["width"], Is.EqualTo(400));
            Assert.That(saved["layout"]!["left"]!["items"]![1], Is.Null);
            Assert.That((bool?)saved["layout"]!["left"]!["items"]![2]!["x"], Is.True);
            Assert.That(saved["empty"]!.AsArray(), Is.Empty);
            Assert.That(saved["none"]!.AsObject(), Is.Empty);
        });
    }

    [Test]
    public async Task A_posted_key_replaces_its_whole_value_instead_of_deep_merging()
    {
        await http.PostAsync($"/kv/overlays/{Id}", Json("""{"layout":{"a":1,"b":2}}"""));
        await http.PostAsync($"/kv/overlays/{Id}", Json("""{"layout":{"c":3}}"""));

        var saved = JsonNode.Parse(await http.GetStringAsync($"/kv/overlays/{Id}"))!;
        Assert.That(saved["layout"]!.AsObject().Select(pair => pair.Key), Is.EqualTo(new[] { "c" }));
    }

    [TestCase("""{"a":{"__proto__":1}}""")]
    [TestCase("""{"a":[{"b":{"__proto__":{}}}]}""")]
    [TestCase("""{"a":{"":1}}""")]
    [TestCase("""{"a":[1e999]}""")]
    [TestCase("""{"a":{"b":{"c":-1e999}}}""")]
    [TestCase("""{"__proto__":1}""")]
    [TestCase("""{"a":{"b":1,"b":2}}""")]
    public async Task Bad_keys_numbers_and_duplicates_are_rejected_at_any_depth(string body)
    {
        var response = await http.PostAsync($"/kv/overlays/{Id}", Json(body));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(File.Exists(FileFor(Id)), Is.False);
    }

    [Test]
    public async Task Nested_strings_and_keys_over_the_limits_are_rejected()
    {
        var longString = new string('x', OverlayKeyValueStore.MaxStringLength + 1);
        var longKey = new string('k', OverlayKeyValueStore.MaxKeyLength + 1);

        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json($$"""{"a":["{{longString}}"]}"""))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json("{\"a\":{\"" + longKey + "\":1}}"))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Depth_beyond_the_limit_is_rejected_while_parsing_and_the_limit_itself_is_accepted()
    {
        string Nest(int levels) => string.Concat(Enumerable.Repeat("""{"a":""", levels)) + "1" + new string('}', levels);

        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json(Nest(OverlayKeyValueStore.MaxDepth)))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json(Nest(OverlayKeyValueStore.MaxDepth + 1)))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var deep = new string('[', 5000) + new string(']', 5000);
        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json($$"""{"a":{{deep}}}"""))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task The_total_number_of_values_is_capped_even_within_the_byte_limit()
    {
        var items = string.Join(',', Enumerable.Repeat("1", OverlayKeyValueStore.MaxNodes));

        var response = await http.PostAsync($"/kv/overlays/{Id}", Json($$"""{"a":[{{items}}]}"""));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(File.Exists(FileFor(Id)), Is.False);
    }

    [Test]
    public async Task Long_strings_are_rejected_and_the_limit_itself_is_accepted()
    {
        var ok = new string('x', OverlayKeyValueStore.MaxStringLength);
        var tooLong = new string('x', OverlayKeyValueStore.MaxStringLength + 1);

        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json($$"""{"a":"{{ok}}"}"""))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json($$"""{"a":"{{tooLong}}"}"""))).StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Numbers_in_any_json_form_up_to_double_range_are_accepted()
    {
        var response = await http.PostAsync($"/kv/overlays/{Id}", Json("""{"a":-12,"b":3.5e2,"c":0,"d":1.7e308}"""));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task A_hand_edited_file_with_invalid_content_reads_as_a_server_error_but_can_be_overwritten()
    {
        Directory.CreateDirectory(Path.Combine(profile.FullName, "kv", "overlays"));
        File.WriteAllText(FileFor(Id), """{"a":{"__proto__":true}}""");

        Assert.That((await http.GetAsync($"/kv/overlays/{Id}")).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json("""{"b":1}"""))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(JsonNode.Parse(await http.GetStringAsync($"/kv/overlays/{Id}"))!.AsObject().Single().Key, Is.EqualTo("b"));
    }

    [Test]
    public async Task Other_content_types_are_rejected()
    {
        var response = await http.PostAsync($"/kv/overlays/{Id}", Json("{}", "text/plain"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
    }

    [Test]
    public async Task Oversized_bodies_and_oversized_merged_documents_are_rejected_without_changing_the_file()
    {
        var big = new string('x', OverlayKeyValueStore.MaxDocumentBytes);
        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json($$"""{"a":"{{big}}"}"""))).StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));

        // Each batch is under the body limit; together the merged document is not.
        var text = new string('x', OverlayKeyValueStore.MaxStringLength);
        string Batch(string prefix) => "{" + string.Join(',', Enumerable.Range(0, 12).Select(i => $"\"{prefix}{i}\":\"{text}\"")) + "}";
        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json(Batch("a")))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var before = File.ReadAllText(FileFor(Id));
        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json(Batch("b")))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var full = File.ReadAllText(FileFor(Id));
        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json(Batch("c")))).StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));

        Assert.That(File.ReadAllText(FileFor(Id)), Is.EqualTo(full));
        Assert.That(full.Length, Is.GreaterThan(before.Length));
    }

    [Test]
    public async Task Too_many_keys_are_rejected()
    {
        var keys = string.Join(',', Enumerable.Range(0, OverlayKeyValueStore.MaxKeys + 1).Select(i => $"\"k{i}\":1"));

        var response = await http.PostAsync($"/kv/overlays/{Id}", Json($"{{{keys}}}"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task The_number_of_overlays_is_capped()
    {
        for (var i = 0; i < OverlayKeyValueStore.MaxOverlays; i++)
        {
            Assert.That((await http.PostAsync($"/kv/overlays/{Guid.NewGuid():D}", Json("{}"))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json("{}"))).StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
        var existing = (await http.GetFromJsonAsync<string[]>("/kv/overlays"))![0];
        Assert.That((await http.PostAsync($"/kv/overlays/{existing}", Json("""{"a":1}"""))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task A_corrupt_file_reads_as_a_server_error_but_can_be_overwritten()
    {
        Directory.CreateDirectory(Path.Combine(profile.FullName, "kv", "overlays"));
        File.WriteAllText(FileFor(Id), "{ not json");

        Assert.That((await http.GetAsync($"/kv/overlays/{Id}")).StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        Assert.That((await http.PostAsync($"/kv/overlays/{Id}", Json("""{"a":1}"""))).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((int?)JsonNode.Parse(await http.GetStringAsync($"/kv/overlays/{Id}"))!["a"], Is.EqualTo(1));
    }

    [Test]
    public async Task Concurrent_updates_to_different_keys_are_all_kept()
    {
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => http.PostAsync($"/kv/overlays/{Id}", Json($$"""{"k{{i}}":{{i}}}"""))));

        var saved = JsonNode.Parse(await http.GetStringAsync($"/kv/overlays/{Id}"))!.AsObject();
        Assert.That(saved.Count, Is.EqualTo(20));
        Assert.That(Directory.GetFiles(Path.Combine(profile.FullName, "kv", "overlays"), "*.tmp"), Is.Empty);
    }
}
