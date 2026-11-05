using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RecallOS.Core.Abstractions;
using RecallOS.Core.Api;
using RecallOS.Core.Assistant.Llm;
using RecallOS.Core.Audio;
using RecallOS.Core.Integrations;
using RecallOS.Core.Intelligence;
using RecallOS.Core.Models;
using RecallOS.Core.Ocr;
using RecallOS.Core.Pipeline;
using RecallOS.Core.Privacy;
using RecallOS.Core.Search;
using RecallOS.Core.Security;
using RecallOS.Core.Sharing;
using RecallOS.Core.Tests.Fakes;
using RecallOS.Core.Timesheets;
using Xunit;

namespace RecallOS.Core.Tests;

// ---- what is never recorded --------------------------------------------------------------

public sealed class SensitiveWindowTests
{
    private static readonly RecallSettings Defaults = new();

    [Theory]
    [InlineData("msedge", "New InPrivate tab - Work - Microsoft Edge", "private browsing")]
    [InlineData("chrome", "New Incognito Tab - Google Chrome (Incognito)", "private browsing")]
    [InlineData("firefox", "Mozilla Firefox Private Browsing", "private browsing")]
    [InlineData("brave", "Search - Brave (Private)", "private browsing")]
    [InlineData("1Password.exe", "Vault", "password manager")]
    [InlineData("bitwarden", "Bitwarden", "password manager")]
    [InlineData("KeePassXC", "Passwords.kdbx", "password manager")]
    [InlineData("chrome", "Bitwarden Web Vault - Google Chrome", "password manager")]
    [InlineData("chrome", "Accounts Overview | Wells Fargo - Google Chrome", "banking")]
    [InlineData("msedge", "Online Banking - Credit Union - Microsoft Edge", "banking")]
    public void TheseAreOffLimitsByDefault(string process, string title, string reason)
    {
        Assert.Equal(reason, SensitiveWindows.Reason(process, title, Defaults));
    }

    [Theory]
    [InlineData("code", "AuthService.cs - RecallOS - Visual Studio Code")]
    [InlineData("chrome", "Private equity explained - Wikipedia - Google Chrome")]
    [InlineData("chrome", "Pull request #212 - GitHub - Google Chrome")]
    public void OrdinaryWindowsAreLeftAlone(string process, string title)
    {
        Assert.Null(SensitiveWindows.Reason(process, title, Defaults));
    }

    [Fact]
    public void EachGroupCanBeTurnedOffOnItsOwn()
    {
        var settings = new RecallSettings { ExcludePrivateWindows = false };
        Assert.Null(SensitiveWindows.Reason("chrome", "x - Google Chrome (Incognito)", settings));
        Assert.NotNull(SensitiveWindows.Reason("bitwarden", "Bitwarden", settings));
    }

    private sealed class ScreenWithOtherWindows : IScreenCaptureService
    {
        private readonly FakeScreen _inner = new() { VaryEachFrame = true };

        public List<ForegroundWindowInfo> Visible { get; } = [];

        public Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default) =>
            _inner.CaptureAsync(request, cancellationToken);

        public ForegroundWindowInfo GetForegroundWindow() => _inner.GetForegroundWindow();

        public IReadOnlyList<ForegroundWindowInfo> GetVisibleWindows() => Visible;

        public TimeSpan GetUserIdleTime() => TimeSpan.Zero;

        public bool IsSessionLocked() => false;
    }

    [Fact]
    public async Task APrivateWindowOnAnotherMonitorStopsAFullScreenCapture()
    {
        await using var store = await ScratchStore.CreateAsync();
        var screen = new ScreenWithOtherWindows();
        var pipeline = new CapturePipeline(screen, store.Frames, store.Files, store.Settings);

        Assert.True((await pipeline.CaptureAsync(CaptureRequest.Manual(CaptureTarget.AllScreens))).WasStored);

        screen.Visible.Add(new ForegroundWindowInfo(7, "Search - Google Chrome (Incognito)", "chrome"));

        var outcome = await pipeline.CaptureAsync(CaptureRequest.Manual(CaptureTarget.AllScreens));
        Assert.False(outcome.WasStored);
        Assert.Equal(CaptureSkipReason.Excluded, outcome.SkipReason);
        Assert.Single(await store.Frames.GetRecentAsync(10));
    }
}

// ---- what an organisation can enforce -----------------------------------------------------

public sealed class EnterprisePolicyTests
{
    private sealed class Source(Dictionary<string, object> values) : IPolicySource
    {
        public object? Read(string name) => values.GetValueOrDefault(name);
    }

    [Fact]
    public void WithNoPolicyNothingChanges()
    {
        var policy = new EnterprisePolicy(new Source([]));
        var settings = policy.Apply(new RecallSettings { ExcludePrivateWindows = false, RetentionDays = 0 });

        Assert.False(policy.IsManaged);
        Assert.False(settings.ExcludePrivateWindows);
        Assert.Equal(0, settings.RetentionDays);
    }

    [Fact]
    public void PoliciesOnlyEverTightenAndAreNoted()
    {
        var policy = new EnterprisePolicy(new Source(new Dictionary<string, object>
        {
            ["DisableRecording"] = 1,
            ["DisableRemoteModels"] = 1,
            ["DisableLocalApi"] = 1,
            ["RequireWindowsHello"] = 1,
            ["ExcludePrivateWindows"] = 1,
            ["ExcludedProcesses"] = "acme-vault",
            ["MaxRetentionDays"] = 14,
            ["MaxStorageGigabytes"] = 5
        }));

        var settings = policy.Apply(new RecallSettings
        {
            AutoCaptureEnabled = true,
            AssistantProvider = AssistantProvider.Server,
            LocalApiEnabled = true,
            ExcludePrivateWindows = false,
            ExcludedProcesses = "mine",
            RetentionDays = 0,
            MaxStorageGigabytes = 2
        });

        Assert.False(settings.AutoCaptureEnabled);
        Assert.Equal(AssistantProvider.Local, settings.AssistantProvider);
        Assert.False(settings.LocalApiEnabled);
        Assert.True(settings.RequireWindowsHello);
        Assert.True(settings.ExcludePrivateWindows);
        Assert.Contains("mine", settings.ParseExcludedProcesses());
        Assert.Contains("acme-vault", settings.ParseExcludedProcesses());

        // Unlimited isn't stricter than a limit, and the user's tighter limit stands.
        Assert.Equal(14, settings.RetentionDays);
        Assert.Equal(2, settings.MaxStorageGigabytes);
        Assert.True(policy.IsManaged);
        Assert.Contains(nameof(RecallSettings.RetentionDays), policy.Enforced);
    }

    [Fact]
    public async Task RemovingAPolicyBringsTheFeatureBack()
    {
        await using var store = await ScratchStore.CreateAsync();

        var managed = new Storage.SqliteSettingsStore(store.Factory, null, new EnterprisePolicy(new Source(new Dictionary<string, object>
        {
            ["DisableTeamVaults"] = 1,
            ["DisableConnections"] = 1
        })));
        await managed.LoadAsync();
        Assert.True(managed.Current.TeamVaultsDisabled);
        Assert.True(managed.Current.ConnectionsDisabled);

        // Saved while managed, the way any change in the app would save it.
        await managed.SaveAsync(managed.Current.Clone());

        var unmanaged = new Storage.SqliteSettingsStore(store.Factory, null, new EnterprisePolicy(new Source(new Dictionary<string, object>())));
        await unmanaged.LoadAsync();
        Assert.False(unmanaged.Current.TeamVaultsDisabled);
        Assert.False(unmanaged.Current.ConnectionsDisabled);
    }

    [Fact]
    public async Task APolicyCannotBeSavedAway()
    {
        await using var store = await ScratchStore.CreateAsync();
        var settings = new Storage.SqliteSettingsStore(store.Factory, null, new EnterprisePolicy(new Source(new Dictionary<string, object> { ["DisableRecording"] = 1 })));
        await settings.LoadAsync();
        Assert.False(settings.Current.AutoCaptureEnabled);

        var attempt = settings.Current.Clone();
        attempt.AutoCaptureEnabled = true;
        await settings.SaveAsync(attempt);

        Assert.False(settings.Current.AutoCaptureEnabled);
    }
}

// ---- choosing a model ----------------------------------------------------------------------

public sealed class ServerModelTests
{
    [Theory]
    [InlineData("Plain answer.", "Plain answer.")]
    [InlineData("<think>Let me consider.</think>The answer.", "The answer.")]
    [InlineData("<think>Still going", "")]
    [InlineData("Before <think>hidden</think>after", "Before after")]
    public void ReasoningIsNotPartOfTheAnswer(string reply, string expected)
    {
        Assert.Equal(expected, ServerLanguageModel.VisibleText(reply));
    }

    private sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Stream(params string[] pieces)
    {
        var text = new StringBuilder();
        foreach (var piece in pieces)
        {
            text.Append("data: ").Append(new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = piece } }) }.ToJsonString()).Append("\n\n");
        }

        text.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    private static async Task SetAsync(ScratchStore store, Action<RecallSettings> change)
    {
        var settings = store.Settings.Current.Clone();
        change(settings);
        await store.Settings.SaveAsync(settings);
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://[::1]:1234/v1", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("http://192.168.1.20:11434/v1", false)]
    [InlineData("http://localhost.evil.example/v1", false)]
    public void OnlyLoopbackCountsAsThisPc(string url, bool local)
    {
        Assert.Equal(local, ServerLanguageModel.IsOnThisPc(new Uri(url)));
    }

    [Fact]
    public async Task AServerOnThisPcStreamsAnAnswerAndIsLabelledSo()
    {
        await using var store = await ScratchStore.CreateAsync();
        await SetAsync(store, s => { s.AssistantServerUrl = "http://localhost:11434/v1/"; s.AssistantServerModel = "llama3.2"; });

        var stub = new Stub((_, _) => Stream("You read ", "the docs [1]."));
        var model = new ServerLanguageModel(store.Settings, http: new HttpClient(stub));

        Assert.True(model.IsAvailable);
        Assert.False(model.LeavesThisPc);
        Assert.Equal("on this PC", model.Location);

        var partial = new List<string>();
        var text = await model.GenerateAsync(new LanguageModelRequest("system", "question"), partial.Add);

        Assert.Equal("You read the docs [1].", text);
        Assert.Equal(["You read ", "You read the docs [1]."], partial);

        var (request, body) = stub.Seen.Single();
        Assert.Equal("http://localhost:11434/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.Equal("llama3.2", JsonNode.Parse(body)!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task AServerOnThisPcIsToldNotToSpendItsAllowanceReasoning()
    {
        await using var store = await ScratchStore.CreateAsync();
        await SetAsync(store, s => { s.AssistantServerUrl = "http://localhost:11434/v1"; s.AssistantServerModel = "qwen3:8b"; });

        var stub = new Stub((_, _) => Stream("Answer."));
        await new ServerLanguageModel(store.Settings, http: new HttpClient(stub)).GenerateAsync(new LanguageModelRequest("system", "question"));

        Assert.Equal("none", JsonNode.Parse(stub.Seen.Single().Body)!["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACloudServerGetsThePlainRequestAndOneRetryIfItObjects()
    {
        await using var store = await ScratchStore.CreateAsync();
        await SetAsync(store, s =>
        {
            s.AssistantServerUrl = "https://api.example.com/v1";
            s.AssistantServerModel = "gpt";
            s.AssistantRemoteConsentHost = "api.example.com";
        });

        var calls = 0;
        var stub = new Stub((_, _) => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.BadRequest) : Stream("Answer."));
        var text = await new ServerLanguageModel(store.Settings, http: new HttpClient(stub)).GenerateAsync(new LanguageModelRequest("system", "question"));

        Assert.Equal("Answer.", text);
        Assert.Equal(2, stub.Seen.Count);

        var first = JsonNode.Parse(stub.Seen[0].Body)!;
        Assert.Null(first["reasoning_effort"]);
        Assert.NotNull(first["max_tokens"]);

        var second = JsonNode.Parse(stub.Seen[1].Body)!;
        Assert.Null(second["max_tokens"]);
        Assert.NotNull(second["max_completion_tokens"]);
    }

    [Fact]
    public async Task ACloudServerStaysOffUntilThatExactHostIsAgreedTo()
    {
        await using var store = await ScratchStore.CreateAsync();
        await SetAsync(store, s =>
        {
            s.AssistantServerUrl = "https://api.example.com/v1";
            s.AssistantServerModel = "big-model";
            s.AssistantServerKey = UserSecrets.Protect("sk-test-key");
        });

        var stub = new Stub((_, _) => Stream("ok"));
        var model = new ServerLanguageModel(store.Settings, http: new HttpClient(stub));

        Assert.True(model.LeavesThisPc);
        Assert.False(model.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.GenerateAsync(new LanguageModelRequest("s", "u")));
        Assert.Empty(stub.Seen);

        await SetAsync(store, s => s.AssistantRemoteConsentHost = "other.example.com");
        Assert.False(model.IsAvailable);

        await SetAsync(store, s => s.AssistantRemoteConsentHost = "api.example.com");
        Assert.True(model.IsAvailable);
        Assert.Equal("at api.example.com", model.Location);

        await model.GenerateAsync(new LanguageModelRequest("s", "u"));
        Assert.Equal("sk-test-key", stub.Seen.Single().Request.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task PlainHttpToAnotherMachineIsNeverUsedEvenWithConsent()
    {
        await using var store = await ScratchStore.CreateAsync();
        await SetAsync(store, s =>
        {
            s.AssistantServerUrl = "http://api.example.com/v1";
            s.AssistantServerModel = "m";
            s.AssistantRemoteConsentHost = "api.example.com";
        });

        Assert.False(new ServerLanguageModel(store.Settings).IsAvailable);
    }

    [Fact]
    public async Task AServerThatRefusesFailsWithoutEchoingItsBody()
    {
        await using var store = await ScratchStore.CreateAsync();
        await SetAsync(store, s => { s.AssistantServerUrl = "http://localhost:1/v1"; s.AssistantServerModel = "m"; });

        var model = new ServerLanguageModel(store.Settings, http: new HttpClient(new Stub((_, _) =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret stuff") })));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => model.GenerateAsync(new LanguageModelRequest("s", "u")));
        Assert.DoesNotContain("secret stuff", failure.Message);
    }

    [Fact]
    public async Task TheRouterFallsBackToTheLocalModelIfTheServerIsNotUsable()
    {
        await using var store = await ScratchStore.CreateAsync();
        var paths = new Common.RecallPaths(store.Root);
        var local = new LlamaLanguageModel(new ModelManager(paths, store.Settings, new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound))), store.Settings);
        var server = new ServerLanguageModel(store.Settings, http: new HttpClient(new Stub((_, _) => Stream("x"))));
        var router = new LanguageModelRouter(store.Settings, local, server);

        await SetAsync(store, s => { s.AssistantProvider = AssistantProvider.Server; s.AssistantServerUrl = "https://api.example.com/v1"; s.AssistantServerModel = "m"; });
        Assert.False(router.LeavesThisPc);

        await SetAsync(store, s => s.AssistantRemoteConsentHost = "api.example.com");
        Assert.True(router.LeavesThisPc);
        Assert.Equal("m", router.ModelName);
    }

    [Fact]
    public void ASecretIsWrappedAndComesBack()
    {
        var wrapped = UserSecrets.Protect("sk-live-abc");
        Assert.DoesNotContain("sk-live-abc", wrapped);
        Assert.Equal("sk-live-abc", UserSecrets.Unprotect(wrapped));
        Assert.Equal(string.Empty, UserSecrets.Unprotect("not wrapped"));
        Assert.Equal(string.Empty, UserSecrets.Protect(""));
    }
}

// ---- the local API --------------------------------------------------------------------------

public sealed class LocalApiTests
{
    private static async Task<(ScratchStore Store, McpHandler Handler, CaptureFrame Frame)> SetUpAsync()
    {
        var store = await ScratchStore.CreateAsync();
        var frame = await store.AddAsync(DateTimeOffset.UtcNow.AddMinutes(-5), "code", "AuthService.cs - RecallOS", "The PKCE flow creates a code verifier for every request.");
        var search = new HybridSearchService(store.Factory, store.Frames, new HashingEmbeddingProvider(128));
        return (store, new McpHandler(search, store.Frames, store.Memory), frame);
    }

    private static JsonObject Call(string tool, JsonObject arguments) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = 1,
        ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments }
    };

    [Fact]
    public async Task ItSpeaksTheProtocolAndOnlyOffersReadOnlyTools()
    {
        var (store, handler, _) = await SetUpAsync();
        await using var _ = store;

        var hello = await handler.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = "a", ["method"] = "initialize",
            ["params"] = new JsonObject { ["protocolVersion"] = "2025-03-26" }
        });
        Assert.Equal("a", hello!["id"]!.GetValue<string>());
        Assert.Equal("2025-03-26", hello["result"]!["protocolVersion"]!.GetValue<string>());

        var list = await handler.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" });
        var tools = list!["result"]!["tools"]!.AsArray();
        Assert.Equal(["search_memory", "get_moment", "recent_activity"], tools.Select(t => t!["name"]!.GetValue<string>()));
        Assert.All(tools, t => Assert.True(t!["annotations"]!["readOnlyHint"]!.GetValue<bool>()));

        Assert.Null(await handler.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }));
        Assert.Equal(-32601, (await handler.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "frames/delete" }))!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task SearchAndGetReturnWhatWasOnScreen()
    {
        var (store, handler, frame) = await SetUpAsync();
        await using var _ = store;

        var found = await handler.HandleAsync(Call("search_memory", new JsonObject { ["query"] = "verifier" }));
        var match = found!["result"]!["structuredContent"]!["matches"]!.AsArray().Single()!;
        Assert.Equal(frame.Id, match["id"]!.GetValue<long>());
        Assert.Equal("VS Code", match["app"]!.GetValue<string>());

        var moment = await handler.HandleAsync(Call("get_moment", new JsonObject { ["id"] = frame.Id }));
        Assert.Contains("code verifier", moment!["result"]!["structuredContent"]!["text"]!.GetValue<string>());

        var missing = await handler.HandleAsync(Call("get_moment", new JsonObject { ["id"] = 99999 }));
        Assert.True(missing!["result"]!["isError"]!.GetValue<bool>());

        var bad = await handler.HandleAsync(Call("search_memory", new JsonObject()));
        Assert.Equal(-32602, bad!["error"]!["code"]!.GetValue<int>());

        var wrongType = await handler.HandleAsync(Call("get_moment", new JsonObject { ["id"] = "seven" }));
        Assert.Equal(-32602, wrongType!["error"]!["code"]!.GetValue<int>());
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task OverHttpItNeedsTheTokenAndALocalOrigin()
    {
        var (store, handler, _) = await SetUpAsync();
        await using var _ = store;

        using var server = new LocalApiServer(handler);
        var token = LocalApiServer.NewToken();
        var port = FreePort();
        Assert.True(server.Start(port, token));

        using var http = new HttpClient();
        var url = $"http://localhost:{port}/mcp";
        const string ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";

        Task<HttpResponseMessage> Send(string? bearer, string? origin = null, HttpMethod? method = null, string? path = null)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Post, path ?? url);
            if ((method ?? HttpMethod.Post) == HttpMethod.Post)
            {
                request.Content = new StringContent(ping, Encoding.UTF8, "application/json");
            }

            if (bearer is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
            }

            if (origin is not null)
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }

            return http.SendAsync(request);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("wrong-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(token, "https://evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(token, method: HttpMethod.Get)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(token, path: $"http://localhost:{port}/other")).StatusCode);

        var ok = await Send(token, "http://localhost:3000");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.NotNull(JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["result"]);

        server.Stop();
        Assert.False(server.IsRunning);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Send(token));
    }

    [Fact]
    public void TheSetupCommandCarriesThePortAndToken()
    {
        var command = LocalApiServer.ClaudeCodeCommand(47321, "abc");
        Assert.Contains("http://localhost:47321/mcp", command);
        Assert.Contains("Authorization: Bearer abc", command);
    }
}

// ---- connected services and imports ---------------------------------------------------------

public sealed class ConnectionsTests
{
    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Timesheet Sheet() => new()
    {
        From = new DateOnly(2026, 10, 12),
        To = new DateOnly(2026, 10, 18),
        MaxGap = TimeSpan.FromMinutes(1),
        Entries =
        [
            new TimesheetEntry(new DateOnly(2026, 10, 14), "Acme Portal", "code", TimeSpan.FromMinutes(90), 50,
                [new TimesheetTitle("Api.cs, part 1", TimeSpan.FromMinutes(90))]),
            new TimesheetEntry(new DateOnly(2026, 10, 15), Timesheet.NoProject, "chrome", TimeSpan.FromMinutes(30), 20,
                [new TimesheetTitle("=HYPERLINK(evil)", TimeSpan.FromMinutes(30))])
        ]
    };

    [Fact]
    public async Task SlackOnlyPostsToSlackItself()
    {
        var recorder = new Recorder(_ => Json("ok"));
        var connections = new Connections(new HttpClient(recorder));

        await Assert.ThrowsAsync<ConnectionException>(() => connections.PostToSlackAsync("https://evil.example/hook", "draft"));
        await Assert.ThrowsAsync<ConnectionException>(() => connections.PostToSlackAsync("http://hooks.slack.com/x", "draft"));
        Assert.Empty(recorder.Seen);

        await connections.PostToSlackAsync("https://hooks.slack.com/services/T/B/x", "draft text");
        Assert.Equal("draft text", JsonNode.Parse(recorder.Seen.Single().Body)!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task APullRequestCommentGoesToThatPullRequest()
    {
        var recorder = new Recorder(_ => Json("""{"html_url":"https://github.com/a/b/pull/12#c1"}""", HttpStatusCode.Created));
        var connections = new Connections(new HttpClient(recorder));

        await Assert.ThrowsAsync<ConnectionException>(() => connections.CommentOnPullRequestAsync("t", "https://github.com/a/b/issues/12", "x"));

        var url = await connections.CommentOnPullRequestAsync("tok", "https://github.com/a/b/pull/12/files", "the draft");
        Assert.Equal("https://github.com/a/b/pull/12#c1", url);

        var (request, body) = recorder.Seen.Single();
        Assert.Equal("https://api.github.com/repos/a/b/issues/12/comments", request.RequestUri!.ToString());
        Assert.Equal("tok", request.Headers.Authorization!.Parameter);
        Assert.Equal("the draft", JsonNode.Parse(body)!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task AStripeInvoiceIsOnlyADraftWithALinePerRatedProject()
    {
        var recorder = new Recorder(r => r.RequestUri!.AbsolutePath.EndsWith("/invoices") ? Json("""{"id":"in_1"}""") : Json("""{"id":"ii_1"}"""));
        var connections = new Connections(new HttpClient(recorder));
        var options = new TimesheetExportOptions("Acme", "$", new Dictionary<string, decimal> { ["Acme Portal"] = 100m });

        var url = await connections.CreateStripeDraftInvoiceAsync("sk", "cus_1", Sheet(), options);
        Assert.Equal("https://dashboard.stripe.com/invoices/in_1", url);

        Assert.Equal(2, recorder.Seen.Count);
        Assert.Contains("auto_advance=false", recorder.Seen[0].Body);
        Assert.Contains("collection_method=send_invoice", recorder.Seen[0].Body);
        Assert.Contains("amount=15000", recorder.Seen[1].Body);
        Assert.Contains("currency=usd", recorder.Seen[1].Body);
        Assert.Contains("invoice=in_1", recorder.Seen[1].Body);

        await Assert.ThrowsAsync<ConnectionException>(() => connections.CreateStripeDraftInvoiceAsync("sk", "bob", Sheet(), options));
        await Assert.ThrowsAsync<ConnectionException>(() => connections.CreateStripeDraftInvoiceAsync("sk", "cus_1", Sheet(), new TimesheetExportOptions()));
        await Assert.ThrowsAsync<ConnectionException>(() => connections.CreateStripeDraftInvoiceAsync("sk", "cus_1", Sheet(), options with { Currency = "bananas" }));
    }

    [Fact]
    public async Task StripesOwnErrorIsShown()
    {
        var connections = new Connections(new HttpClient(new Recorder(_ => Json("""{"error":{"message":"No such customer"}}""", HttpStatusCode.BadRequest))));
        var options = new TimesheetExportOptions(null, "$", new Dictionary<string, decimal> { ["Acme Portal"] = 100m });

        var failure = await Assert.ThrowsAsync<ConnectionException>(() => connections.CreateStripeDraftInvoiceAsync("sk", "cus_1", Sheet(), options));
        Assert.Contains("No such customer", failure.Message);
    }

    [Theory]
    [InlineData(TimesheetFormat.Toggl, "Email,Start date,Start time,Duration,Project,Client,Description", "a@b.c,2026-10-14,09:00:00,01:30:00,Acme Portal,Acme,")]
    [InlineData(TimesheetFormat.Clockify, "Project,Client,Description,Email,Start Date,Start Time,Duration (h)", "Acme Portal,Acme,\"Api.cs, part 1\",a@b.c,2026-10-14,09:00,1.50")]
    [InlineData(TimesheetFormat.Harvest, "Date,Client,Project,Task,Notes,Hours,First name,Last name", "2026-10-14,Acme,Acme Portal,VS Code,\"Api.cs, part 1\",1.50,Sam,Lee")]
    [InlineData(TimesheetFormat.QuickBooks, "Date,Employee,Customer,Service Item,Hours,Description", "10/14/2026,Sam Lee,Acme,Acme Portal,1.50,")]
    public void EachToolGetsItsOwnLayout(TimesheetFormat format, string header, string startOfFirstRow)
    {
        var lines = TimesheetImports.ToCsv(format, Sheet(), "a@b.c", "Sam Lee", "Acme").Split(Environment.NewLine);
        Assert.Equal(header, lines[0]);
        Assert.StartsWith(startOfFirstRow, lines[1]);
    }

    [Fact]
    public void JiraWorklogsCarryTheirFourPartsAndSpreadsheetFormulasAreDefused()
    {
        var jira = TimesheetImports.ToCsv(TimesheetFormat.Jira, Sheet(), "a@b.c", "Sam", null);
        Assert.Contains("14/Oct/26 9:00 AM;a@b.c;5400", jira);

        var harvest = TimesheetImports.ToCsv(TimesheetFormat.Harvest, Sheet(), "a@b.c", "Sam", null);
        Assert.Contains("'=HYPERLINK(evil)", harvest);
    }
}

// ---- vault files ----------------------------------------------------------------------------

public sealed class VaultFileTests
{
    private static VaultSharingService Service(ScratchStore store) => new(
        store.Frames, store.Memory, store.Files, store.Settings, new TextChunker(), new HeuristicIntentClassifier());

    private static async Task<(long ProjectId, string Secret)> ProjectAsync(ScratchStore store)
    {
        const string word = "zygomorphicmitosisword";
        var at = new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);
        var ids = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await store.AddAsync(at.AddMinutes(i), "winword", "Lab report.docx", $"{word} observation {i}")).Id);
        }

        return ((await store.Memory.SaveProjectMemoryAsync("Biology", "biology", ids)).Id, word);
    }

    [Fact]
    public void TheCodeRoundTripsAndCatchesTypingMistakes()
    {
        var secret = VaultFile.NewSecret();
        var code = VaultFile.ToCode(secret);

        Assert.True(VaultFile.TryParseCode(code.ToLowerInvariant().Replace('-', ' '), out var back));
        Assert.Equal(secret, back);
        Assert.False(VaultFile.TryParseCode((code[0] == '2' ? '3' : '2') + code[1..], out _));
        Assert.False(VaultFile.TryParseCode("HELLO", out _));
    }

    [Fact]
    public async Task AProjectTravelsByFileAndArrivesWholeAndSeparate()
    {
        await using var sender = await ScratchStore.CreateAsync();
        await using var receiver = await ScratchStore.CreateAsync();
        var (project, word) = await ProjectAsync(sender);
        var path = Path.Combine(sender.Root, "biology" + VaultFile.Extension);

        var code = await Service(sender).ExportToFileAsync(project, "Ansh", path);

        // Nothing readable in the file itself.
        var raw = File.ReadAllBytes(path);
        Assert.True(raw.AsSpan().IndexOf(Encoding.UTF8.GetBytes(word)) < 0);
        Assert.True(raw.AsSpan().IndexOf("Biology"u8) < 0);

        var result = await Service(receiver).ImportFromFileAsync(path, code);

        Assert.Equal(3, result.Frames);
        Assert.Equal("Ansh", result.SharedBy);
        Assert.True(result.Project.IsShared);

        var search = new HybridSearchService(receiver.Factory, receiver.Frames, new HashingEmbeddingProvider(128));
        var hits = await search.SearchAsync(SearchQueryParser.Parse(word, SearchMode.Keyword));
        Assert.Equal(3, hits.Hits.Count);
        Assert.All(hits.Hits, h => Assert.Equal(CaptureOrigin.Shared, h.Frame.Origin));
    }

    [Fact]
    public async Task TheWrongCodeOpensNothing()
    {
        await using var sender = await ScratchStore.CreateAsync();
        await using var receiver = await ScratchStore.CreateAsync();
        var (project, _) = await ProjectAsync(sender);
        var path = Path.Combine(sender.Root, "p" + VaultFile.Extension);
        await Service(sender).ExportToFileAsync(project, "Ansh", path);

        var wrong = VaultFile.ToCode(VaultFile.NewSecret());
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => Service(receiver).ImportFromFileAsync(path, wrong));
        Assert.Contains("code", failure.Message);
        Assert.Empty(await receiver.Frames.GetRecentAsync(10));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAlteredOrTruncatedFileKeepsNothing(bool truncate)
    {
        await using var sender = await ScratchStore.CreateAsync();
        await using var receiver = await ScratchStore.CreateAsync();
        var (project, _) = await ProjectAsync(sender);
        var path = Path.Combine(sender.Root, "p" + VaultFile.Extension);
        var code = await Service(sender).ExportToFileAsync(project, "Ansh", path);

        var data = File.ReadAllBytes(path);
        if (truncate)
        {
            // Past at least one whole screen, so some of it decrypts before the file runs out.
            File.WriteAllBytes(path, data[..^25]);
        }
        else
        {
            data[^40] ^= 0xFF;
            File.WriteAllBytes(path, data);
        }

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => Service(receiver).ImportFromFileAsync(path, code));
        Assert.Empty(await receiver.Frames.GetRecentAsync(10));
        Assert.Empty(await receiver.Memory.GetProjectMemoriesAsync());
    }

    [Fact]
    public async Task SomethingThatIsNotAVaultFileIsRejected()
    {
        await using var receiver = await ScratchStore.CreateAsync();
        var path = Path.Combine(receiver.Root, "notes.recallvault");
        File.WriteAllText(path, "hello there, this is not a vault file at all");

        await Assert.ThrowsAsync<InvalidDataException>(() => Service(receiver).ImportFromFileAsync(path, VaultFile.ToCode(VaultFile.NewSecret())));
    }
}

// ---- meetings ---------------------------------------------------------------------------------

public sealed class MeetingTranscriptionTests
{
    private sealed class FakeTranscriber(params string[] lines) : ISpeechTranscriber
    {
        private int _next;

        public bool IsReady { get; set; } = true;

        public int Calls { get; private set; }

        public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(lines[Math.Min(_next++, lines.Length - 1)]);
        }
    }

    private sealed class FakeSource : IAudioSource
    {
        public Action<float[]>? Deliver { get; private set; }

        public bool Stopped { get; private set; }

        public void Start(Action<float[]> onSamples) => Deliver = onSamples;

        public void Stop() => Stopped = true;

        public void Dispose()
        {
        }
    }

    private static float[] Tone(double seconds, float level = 0.1f)
    {
        var samples = new float[(int)(seconds * 16_000)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = level * MathF.Sin(i * 0.05f);
        }

        return samples;
    }

    private static async Task<(ScratchStore Store, MeetingTranscriptionService Service, FakeTranscriber Speech, FakeSource Source)> SetUpAsync(params string[] lines)
    {
        var store = await ScratchStore.CreateAsync();
        var settings = store.Settings.Current.Clone();
        settings.AudioTranscriptionEnabled = true;
        await store.Settings.SaveAsync(settings);

        var speech = new FakeTranscriber(lines);
        var source = new FakeSource();
        var service = new MeetingTranscriptionService(speech, store.Frames, store.Settings, new HashingEmbeddingProvider(128), new TextChunker(), null, _ => source);
        return (store, service, speech, source);
    }

    [Fact]
    public async Task ItRefusesToStartUnlessTurnedOnAndReady()
    {
        var (store, service, speech, _) = await SetUpAsync("x");
        await using var _ = store;

        speech.IsReady = false;
        Assert.Throws<InvalidOperationException>(() => service.Start());
        Assert.False(service.IsRunning);

        speech.IsReady = true;
        var off = store.Settings.Current.Clone();
        off.AudioTranscriptionEnabled = false;
        await store.Settings.SaveAsync(off);
        Assert.Throws<InvalidOperationException>(() => service.Start());
    }

    [Fact]
    public async Task WhatWasSaidBecomesSearchableMemoryWithNoPicture()
    {
        var (store, service, _, source) = await SetUpAsync("The client asked to move the launch to March.");
        await using var _ = store;

        service.Start("Acme weekly");
        Assert.True(service.IsRunning);

        source.Deliver!(Tone(5));
        await service.StopAsync();

        Assert.True(source.Stopped);
        Assert.False(service.IsRunning);
        Assert.Equal(1, service.Segments);

        var frame = Assert.Single(await store.Frames.GetRecentAsync(10));
        Assert.Equal(CaptureOrigin.Audio, frame.Origin);
        Assert.Equal("meeting", frame.ProcessName);
        Assert.Equal("Acme weekly", frame.WindowTitle);
        Assert.Equal(string.Empty, frame.ImagePath);

        var search = new HybridSearchService(store.Factory, store.Frames, new HashingEmbeddingProvider(128));
        Assert.Single((await search.SearchAsync(SearchQueryParser.Parse("launch march", SearchMode.Keyword))).Hits);
        Assert.NotEmpty((await search.SearchAsync(SearchQueryParser.Parse("client launch", SearchMode.Semantic))).Hits);

        // And it can be forgotten like anything else.
        await store.Forget().ForgetAsync(new ForgetRequest { Application = "meeting" });
        Assert.Empty(await store.Frames.GetRecentAsync(10));
    }

    [Fact]
    public async Task EveryHalfMinuteIsFiledAsItGoes()
    {
        var (store, service, speech, source) = await SetUpAsync("first part", "second part", "last part");
        await using var _ = store;

        service.Start();
        source.Deliver!(Tone(31));
        source.Deliver!(Tone(31));
        source.Deliver!(Tone(3));
        await service.StopAsync();

        Assert.Equal(3, speech.Calls);
        Assert.Equal(3, (await store.Frames.GetRecentAsync(10)).Count);
    }

    [Fact]
    public async Task SilenceAndNonSpeechFileNothing()
    {
        var (store, service, speech, source) = await SetUpAsync("[BLANK_AUDIO]");
        await using var _ = store;

        service.Start();
        source.Deliver!(Tone(5, level: 0));
        await service.StopAsync();
        Assert.Equal(0, speech.Calls);

        service.Start();
        source.Deliver!(Tone(5));
        await service.StopAsync();
        Assert.Equal(1, speech.Calls);
        Assert.Empty(await store.Frames.GetRecentAsync(10));
    }

    [Fact]
    public async Task SecretsReadAloudAreRedactedLikeSecretsOnScreen()
    {
        var (store, service, _, source) = await SetUpAsync("my card is 4111111111111111 thanks");
        await using var _ = store;

        service.Start();
        source.Deliver!(Tone(5));
        await service.StopAsync();

        var frame = Assert.Single(await store.Frames.GetRecentAsync(10));
        Assert.DoesNotContain("4111111111111111", frame.Text);
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData(" (music)  ♪ ", "")]
    [InlineData("Hello [inaudible] there", "Hello there")]
    public void NonSpeechMarkersAreRemoved(string input, string expected)
    {
        Assert.Equal(expected, MeetingTranscriptionService.Clean(input));
    }
}
