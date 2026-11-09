using RhinoMagpie;
using RhinoMagpie.Agent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    passed++;
}

var configuration = BridgeConfig.Parse("""{"model":"owner/custom-model","baseUrl":"https://provider.example/custom/v1/","apiKey":"fixture-private-key"}""");
Check(configuration.BaseUrl == "https://provider.example/custom/v1" && configuration.Model == "owner/custom-model",
    "an explicit custom HTTPS endpoint and model work without Magpie");
Check(!configuration.ToString().Contains("fixture-private-key"), "connection diagnostics redact credentials");
Check(BridgeConfig.Parse("""{"model":"local","baseUrl":"http://127.0.0.1:9001","apiKey":""}""").BaseUrl ==
    "http://127.0.0.1:9001/v1", "an explicitly selected loopback service works without a key");
Check(BridgeConfig.Parse("""{"model":"local","baseUrl":"http://[::1]:9001/v1","apiKey":""}""").BaseUrl ==
    "http://[::1]:9001/v1", "IPv6 loopback keeps its selected API root");
foreach (string invalid in new[]
{
    """{"model":"m","baseUrl":"http://remote.example/v1","apiKey":"key"}""",
    """{"model":"m","baseUrl":"file:///tmp/api","apiKey":"key"}""",
    """{"model":"m","baseUrl":"https://user:secret@provider.example/v1","apiKey":"key"}""",
    """{"model":"m","baseUrl":"https://provider.example/v1?key=secret","apiKey":"key"}""",
    """{"model":"m","baseUrl":"https://provider.example/v1#fragment","apiKey":"key"}""",
    """{"model":"","baseUrl":"https://provider.example/v1","apiKey":"key"}""",
    """{"model":"m","baseUrl":"https://provider.example/v1","apiKey":"key\nbad"}""",
})
{
    try { BridgeConfig.Parse(invalid); throw new Exception("Invalid connection accepted"); }
    catch (ArgumentException error) { Check(!error.Message.Contains("secret"), "invalid connection fails without reflecting credentials"); }
}

string directory = Path.Combine(AppContext.BaseDirectory, "connection-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string settings = Path.Combine(directory, "settings.json");
try
{
    if (OperatingSystem.IsWindows())
    {
        ConnectionSettings.Save(configuration, settings);
        string saved = File.ReadAllText(settings);
        Check(!saved.Contains("fixture-private-key") && saved.Contains("dpapi-current-user") &&
            !JsonNode.Parse(saved)!.AsObject().ContainsKey("apiKey"),
            "the settings file contains Windows user-protected credentials and no plaintext key");
        Check(ConnectionSettings.Read(settings) == configuration, "Windows protected credentials round-trip with the exact endpoint");
        ConnectionSettings.Save(configuration with { ApiKey = "replacement-fixture-key" }, settings);
        Check(ConnectionSettings.Read(settings).ApiKey == "replacement-fixture-key" &&
            !File.ReadAllText(settings).Contains("replacement-fixture-key"), "key replacement stays protected");
        string before = File.ReadAllText(settings);
        try { ConnectionSettings.Save(configuration with { BaseUrl = "http://remote.example" }, settings); }
        catch (ArgumentException) { }
        Check(File.ReadAllText(settings) == before, "invalid updates preserve the previous complete connection");
        var damaged = JsonNode.Parse(before)!;
        damaged["protectedApiKey"] = Convert.ToBase64String(new byte[64]);
        File.WriteAllText(settings, damaged.ToJsonString());
        try { ConnectionSettings.Read(settings); throw new Exception("Damaged credential accepted"); }
        catch (InvalidOperationException error)
        {
            Check(!error.Message.Contains("replacement-fixture-key"), "damaged protected credentials fail closed without leaking the key");
        }
    }
    ConnectionSettings.Save(configuration with { ApiKey = "" }, settings);
    Check(ConnectionSettings.Read(settings).ApiKey == "", "explicit keyless local connections are supported");
    File.WriteAllText(settings, """{"model":"legacy","baseUrl":"http://localhost:3425","apiKey":"legacy-plaintext"}""");
    try { ConnectionSettings.Read(settings); throw new Exception("Legacy plaintext connection accepted"); }
    catch (InvalidOperationException error) { Check(!error.Message.Contains("legacy-plaintext"), "legacy provider settings are not silently imported"); }
    Check(Directory.GetFiles(directory).Length == 1, "atomic settings writes leave no temporary files");
    Check(ConnectionSettings.ConfigPath.Contains(Path.Combine(".config", "miki-rhino")), "Miki owns its settings directory");
}
finally
{
    foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}

foreach (string baseUrl in new[] { "https://provider.example/custom/v1", "http://127.0.0.1:9001/v1" })
{
    var requests = new List<(string Url, string? Authorization, string Body)>();
    using var handler = new ProviderFixture(async request =>
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
        requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(request.Method == HttpMethod.Get
                ? JsonNode.Parse("""{"data":[{"id":"owner/custom-model"}]}""")!
                : JsonNode.Parse("""{"choices":[{"message":{"role":"assistant","content":"fixture reply"}}]}""")!),
        };
    });
    using var client = new HttpClient(handler);
    var gateway = new MagpieGateway(client);
    var selected = configuration with { BaseUrl = baseUrl };
    var models = await gateway.GetModelsAsync(selected, CancellationToken.None);
    await gateway.CompleteAsync(selected, [], [], CancellationToken.None);
    Check(requests.Select(request => request.Url).SequenceEqual(new[] { baseUrl + "/models", baseUrl + "/chat/completions" }),
        "catalog and completion target only the explicit provider API root");
    Check(requests.All(request => request.Authorization == "Bearer fixture-private-key") && models.Single().Id == selected.Model,
        "only the chosen provider receives its credential and selected model");
    Check(JsonNode.Parse(requests.Last().Body)!["model"]!.GetValue<string>() == selected.Model,
        "the selected custom model is sent without alias replacement");
}

foreach (HttpStatusCode failure in new[] { HttpStatusCode.TemporaryRedirect, HttpStatusCode.Unauthorized })
{
    int requests = 0;
    using var handler = new ProviderFixture(_ =>
    {
        requests++;
        var response = new HttpResponseMessage(failure) { Content = new StringContent("fixture-private-key") };
        response.Headers.Location = new Uri("https://unselected.example/v1");
        return Task.FromResult(response);
    });
    using var client = new HttpClient(handler);
    try { await new MagpieGateway(client).CompleteAsync(configuration, [], [], CancellationToken.None); throw new Exception("Provider failure accepted"); }
    catch (InvalidOperationException error)
    {
        Check(requests == 1 && !error.Message.Contains("fixture-private-key"), "provider failure neither falls back nor echoes a credential");
    }
}
Console.WriteLine($"{passed} connection checks passed");

sealed class ProviderFixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request);
}
