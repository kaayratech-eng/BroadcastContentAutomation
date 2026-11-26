// Regression tests for fix sequence #2 ITEM 5 (B19 + B20 + R3).
//
// Part A (B19): an empty Facebook prior-video status must NOT burn an
// attempt. The old code returned Retry("still processing (status: )") for
// an empty phase, so a fine video burned all 3 attempts and landed
// Abandoned. The new code returns PublishResult.Defer, and Program.cs
// undoes the attempt increment for a Defer, so the target waits without
// consuming budget.
//
// Part B (B20): TikTok WaitForPublishAsync returned Fatal on FAILED,
// killing the target permanently. It now returns Retry, consistent with
// CheckPriorPublishAsync (FAILED prior publish = start fresh).
//
// Part C (R3): the Short<->long-form cross-link is reconciled on EVERY
// uploader run, not just at publish time. A Short-first ordering (Short
// publishes before its long-form) used to leave NEITHER direction linked;
// a second run now links both, idempotently, patching only the
// description (live title/tags/category echoed back, never the sidecar's
// stale values) and never re-uploading anything.
//
// The sandbox runner invokes [Fact] methods without awaiting a returned
// Task, so these tests are synchronous and block on the async calls with
// GetAwaiter().GetResult(). Publisher internals are reached via
// reflection (same pattern as UploaderTests / ReflectionHelper).
// The Defer factory/IsDeferred flag are also reached via reflection (see
// DeferResult/IsDefer) so this file compiles — and fails — against the
// pre-fix code, where PublishResult has no Defer member at all.
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;
using GiggleGarden.Uploader;
using GiggleGarden.Uploader.Publishing;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;

namespace BroadcastContentAutomation.Tests;

public class AttemptAndCrossLinkTests
{
    // ------------------------------------------------------------------
    // Shared helpers
    // ------------------------------------------------------------------

    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tn-item5-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Logger NewLogger(string dir) => new(Path.Combine(dir, "logs"));

    // Internal config records (FacebookConfig / TikTokConfig / YouTubeConfig)
    // have no public constructor visible to tests; build one by reflection
    // and set the init-only properties the same way.
    private static object NewConfig(string typeName)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.Uploader, typeName);
        return Activator.CreateInstance(type)!;
    }

    private static void SetProp(object obj, string name, object? value)
    {
        var prop = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(obj.GetType().Name, name);
        prop.SetValue(obj, value);
    }

    private static object NewPublisher(string typeName, object config, Logger log)
    {
        var type = ReflectionHelper.FindType(ReflectionHelper.Uploader, typeName);
        return Activator.CreateInstance(type, config, log)!;
    }

    private static MethodInfo FindInstanceMethod(Type type, string name) =>
        type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
        ?? throw new MissingMethodException($"No instance method '{name}' on {type.Name}.");

    // A handler that answers every request from a canned responder.
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // PublishResult.Defer / .Defer via reflection: the members do not exist
    // on pre-fix code, so a direct reference would not compile there and the
    // fail-first run could not be made. Reflection keeps this file
    // compilable against old code (where these helpers throw / return
    // false, failing the tests as intended).
    private static PublishResult DeferResult(string error)
    {
        var factory = typeof(PublishResult).GetMethod("Defer", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException("PublishResult has no Defer factory (B19 fix not applied).");
        return (PublishResult)factory.Invoke(null, [error])!;
    }

    private static bool IsDefer(PublishResult result)
    {
        var prop = typeof(PublishResult).GetProperty("IsDeferred", BindingFlags.Public | BindingFlags.Instance);
        return prop is not null && prop.GetValue(result) is true;
    }

    // ------------------------------------------------------------------
    // Part A — B19: empty Facebook status defers without burning an attempt
    // ------------------------------------------------------------------

    private static object FacebookPublisherWith(string pageId = "111", string token = "tok")
    {
        var dir = TempDir("fb");
        var cfg = NewConfig("FacebookConfig");
        SetProp(cfg, "PageId", pageId);
        SetProp(cfg, "AccessToken", token);
        SetProp(cfg, "GraphVersion", "v21.0");
        return NewPublisher("FacebookPublisher", cfg, NewLogger(dir));
    }

    private static object FacebookStoryPublisherWith(string pageId = "111", string token = "tok")
    {
        var dir = TempDir("fb-story");
        var cfg = NewConfig("FacebookConfig");
        SetProp(cfg, "PageId", pageId);
        SetProp(cfg, "AccessToken", token);
        SetProp(cfg, "GraphVersion", "v21.0");
        return NewPublisher("FacebookStoryPublisher", cfg, NewLogger(dir));
    }

    private static PublishResult? CheckPriorVideo(object publisher, HttpClient http, string videoId)
    {
        var method = FindInstanceMethod(publisher.GetType(), "CheckPriorVideoAsync");
        var task = (Task<PublishResult?>)method.Invoke(publisher, [http, videoId, CancellationToken.None])!;
        return task.GetAwaiter().GetResult();
    }

    [Fact]
    public void FacebookEmptyPhase_MissingVideoStatus_ReturnsDefer()
    {
        // The B19 case: Facebook answered 200 but the status object carries
        // no video_status at all. The old code fell through to
        // Retry("still processing (status: )") and burned an attempt.
        var publisher = FacebookPublisherWith();
        using var http = new HttpClient(new CannedHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"status":{}}""")));
        var result = CheckPriorVideo(publisher, http, "vid-1");
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.False(result.Permanent);
        Assert.True(IsDefer(result));
        Assert.Contains("without burning an attempt", result.Error);
    }

    [Fact]
    public void FacebookEmptyPhase_MissingStatusObject_ReturnsDefer()
    {
        // No "status" object at all — same "no state reported" case.
        var publisher = FacebookPublisherWith();
        using var http = new HttpClient(new CannedHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{}""")));
        var result = CheckPriorVideo(publisher, http, "vid-1");
        Assert.NotNull(result);
        Assert.True(IsDefer(result));
    }

    [Fact]
    public void FacebookEmptyPhase_ExplicitEmptyString_ReturnsDefer()
    {
        // video_status present but an empty string — still no state.
        var publisher = FacebookPublisherWith();
        using var http = new HttpClient(new CannedHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"status":{"video_status":""}}""")));
        var result = CheckPriorVideo(publisher, http, "vid-1");
        Assert.NotNull(result);
        Assert.True(IsDefer(result));
    }

    [Fact]
    public void FacebookNonEmptyPhase_StillProcessing_ReturnsRetryNotDefer()
    {
        // A real non-empty phase (genuinely still processing) keeps the old
        // behaviour: Retry, which burns an attempt — only the EMPTY status
        // defers. This pins the boundary so the fix does not swallow real
        // processing states into a free wait.
        var publisher = FacebookPublisherWith();
        using var http = new HttpClient(new CannedHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"status":{"video_status":"processing"}}""")));
        var result = CheckPriorVideo(publisher, http, "vid-1");
        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.False(IsDefer(result));
        Assert.Contains("still processing", result.Error);
    }

    [Fact]
    public void FacebookReadyPhase_ReturnsOk()
    {
        // Control: a ready video is reused, as before.
        var publisher = FacebookPublisherWith();
        using var http = new HttpClient(new CannedHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"status":{"video_status":"ready"}}""")));
        var result = CheckPriorVideo(publisher, http, "vid-1");
        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal("vid-1", result.Id);
    }

    [Fact]
    public void FacebookFailedPhase_ReturnsNullForFreshUpload()
    {
        // Control: a failed video means "upload fresh", as before.
        var publisher = FacebookPublisherWith();
        using var http = new HttpClient(new CannedHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"status":{"video_status":"failed"}}""")));
        var result = CheckPriorVideo(publisher, http, "vid-1");
        Assert.Null(result);
    }

    [Fact]
    public void FacebookStoryEmptyPhase_ReturnsDefer()
    {
        // Same B19 case on the Story publisher, whose own CheckPriorVideoAsync
        // had the identical fall-through (its post-upload poll already treats
        // an absent status as done — this is the prior-video check only).
        var publisher = FacebookStoryPublisherWith();
        using var http = new HttpClient(new CannedHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"status":{}}""")));
        var result = CheckPriorVideo(publisher, http, "vid-1");
        Assert.NotNull(result);
        Assert.True(IsDefer(result));
    }

    // Program.cs's per-target result accounting, extracted as the local
    // function RecordTargetResult (found by substring on the built
    // Uploader.dll). The publication is handed in with Attempts already
    // incremented, exactly as the dispatch loop does before PublishAsync.
    private static void RecordResult(Publication publication, PublishResult result, int maxAttempts, Logger log)
    {
        var method = ReflectionHelper.FindMethod(ReflectionHelper.Uploader, "RecordTargetResult");
        method.Invoke(null, [publication, result, maxAttempts, log, "trendlinenow", "facebook"]);
    }

    [Fact]
    public void RecordTargetResult_DeferDoesNotBurnAttempt()
    {
        // The core B19 accounting: a Defer undoes the increment, so the
        // target stays Failed (retryable) with 0 attempts used.
        var dir = TempDir("record-defer");
        var log = NewLogger(dir);
        var pub = new Publication { Attempts = 1, Status = PublishStatus.Failed };
        RecordResult(pub, DeferResult("Facebook video vid-1 returned no status; waiting without burning an attempt."), 3, log);
        Assert.Equal(0, pub.Attempts);
        Assert.Equal(PublishStatus.Failed, pub.Status);
        Assert.NotNull(pub.Error);
    }

    [Fact]
    public void RecordTargetResult_DeferThreeRunsNeverAbandons()
    {
        // The exact B19 failure mode, replayed: three consecutive runs each
        // increment then defer. The old code abandoned after the third Retry;
        // a Defer must never reach Abandoned, however many runs defer.
        var dir = TempDir("record-defer3");
        var log = NewLogger(dir);
        var pub = new Publication { Attempts = 0, Status = PublishStatus.Pending };
        for (var run = 1; run <= 3; run++)
        {
            pub.Attempts++; // the dispatch loop increments before PublishAsync
            RecordResult(pub, DeferResult("no status; waiting without burning an attempt."), 3, log);
            Assert.Equal(0, pub.Attempts);
            Assert.NotEqual(PublishStatus.Abandoned, pub.Status);
        }
        Assert.Equal(PublishStatus.Failed, pub.Status);
    }

    [Fact]
    public void RecordTargetResult_RetryBurnsAttemptAndAbandonsAtMax()
    {
        // Control: a genuine Retry still burns an attempt and abandons at
        // the cap — the Defer path must not leak into normal failures.
        var dir = TempDir("record-retry");
        var log = NewLogger(dir);
        var pub = new Publication { Attempts = 3, Status = PublishStatus.Failed };
        RecordResult(pub, PublishResult.Retry("still processing"), 3, log);
        Assert.Equal(3, pub.Attempts);
        Assert.Equal(PublishStatus.Abandoned, pub.Status);
    }

    [Fact]
    public void RecordTargetResult_RetryBelowMaxStaysFailed()
    {
        var dir = TempDir("record-retry2");
        var log = NewLogger(dir);
        var pub = new Publication { Attempts = 1, Status = PublishStatus.Failed };
        RecordResult(pub, PublishResult.Retry("still processing"), 3, log);
        Assert.Equal(1, pub.Attempts);
        Assert.Equal(PublishStatus.Failed, pub.Status);
    }

    [Fact]
    public void RecordTargetResult_FatalAbandonsImmediately()
    {
        var dir = TempDir("record-fatal");
        var log = NewLogger(dir);
        var pub = new Publication { Attempts = 1, Status = PublishStatus.Failed };
        RecordResult(pub, PublishResult.Fatal("rejected content"), 3, log);
        Assert.Equal(PublishStatus.Abandoned, pub.Status);
    }

    [Fact]
    public void RecordTargetResult_SuccessPublishes()
    {
        var dir = TempDir("record-ok");
        var log = NewLogger(dir);
        var pub = new Publication { Attempts = 1, Status = PublishStatus.Failed, Error = "old error" };
        RecordResult(pub, PublishResult.Ok("id123", "https://youtu.be/id123"), 3, log);
        Assert.Equal(PublishStatus.Published, pub.Status);
        Assert.Equal("id123", pub.Id);
        Assert.Equal("https://youtu.be/id123", pub.Url);
        Assert.Null(pub.Error);
        Assert.Equal("old error", pub.LastError);
    }

    // ------------------------------------------------------------------
    // Part B — B20: TikTok FAILED returns Retry, not Fatal
    // ------------------------------------------------------------------

    private static PublishResult WaitForPublish(object publisher, HttpClient http, string publishId)
    {
        var method = FindInstanceMethod(publisher.GetType(), "WaitForPublishAsync");
        var task = (Task<PublishResult>)method.Invoke(publisher, [http, publishId, CancellationToken.None])!;
        return task.GetAwaiter().GetResult();
    }

    private static object TikTokPublisherWith()
    {
        var dir = TempDir("tiktok");
        var cfg = NewConfig("TikTokConfig");
        SetProp(cfg, "ProcessingTimeoutSeconds", 600);
        return NewPublisher("TikTokPublisher", cfg, NewLogger(dir));
    }

    [Fact]
    public void TikTokFailedStatus_ReturnsRetryNotFatal()
    {
        // One FAILED poll used to return Fatal immediately, permanently
        // killing the target — inconsistent with CheckPriorPublishAsync,
        // which treats a FAILED prior publish as "start fresh". A Retry
        // lets the next run re-init instead.
        // (WaitForPublishAsync waits 5s before its first poll.)
        var publisher = TikTokPublisherWith();
        using var http = new HttpClient(new CannedHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"data":{"status":"FAILED","fail_reason":"transcode_failed"}}""")));
        var result = WaitForPublish(publisher, http, "pub-1");
        Assert.False(result.Success);
        Assert.False(result.Permanent);
        Assert.Contains("failed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TikTokPublishComplete_ReturnsOk()
    {
        // Control: the terminal success state still returns Ok.
        var publisher = TikTokPublisherWith();
        using var http = new HttpClient(new CannedHandler(_ =>
            JsonResponse(HttpStatusCode.OK, """{"data":{"status":"PUBLISH_COMPLETE"}}""")));
        var result = WaitForPublish(publisher, http, "pub-1");
        Assert.True(result.Success);
        Assert.Equal("pub-1", result.Id);
    }

    // ------------------------------------------------------------------
    // Part C — R3: cross-link reconciliation on every run
    // ------------------------------------------------------------------

    private sealed class FakeVideo
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> Tags { get; set; } = [];
        public string CategoryId { get; set; } = "24";
    }

    // Canned YouTube Data API: serves Videos.List from an in-memory store
    // and applies Videos.Update to it, so reconciliation can be driven
    // end-to-end with no network, no token, and no upload. Every request
    // is recorded, so tests can prove nothing but GET + PUT /videos ever
    // happens (no re-upload).
    private sealed class FakeYouTubeHandler : HttpMessageHandler
    {
        public Dictionary<string, FakeVideo> Videos { get; } = new(StringComparer.Ordinal);
        public List<(string Method, string Url, string Body)> Requests { get; } = [];
        public int PutCount => Requests.Count(r => r.Method == "PUT");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // The Google client gzips request bodies by default (GZipEnabled),
            // so a PUT body may arrive compressed — decompress before parsing.
            var body = await ReadBodyAsync(request, ct);
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));
            var path = request.RequestUri.AbsolutePath;

            if (request.Method == HttpMethod.Get && path.Contains("/youtube/v3/videos", StringComparison.Ordinal))
            {
                var id = GetQuery(request.RequestUri.Query, "id") ?? "";
                if (Videos.TryGetValue(id, out var v))
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        items = new[]
                        {
                            new
                            {
                                id,
                                snippet = new
                                {
                                    title = v.Title,
                                    description = v.Description,
                                    tags = v.Tags,
                                    categoryId = v.CategoryId,
                                    defaultLanguage = "en",
                                    defaultAudioLanguage = "en",
                                },
                            },
                        },
                    });
                    return JsonResponse(HttpStatusCode.OK, json);
                }
                return JsonResponse(HttpStatusCode.OK, """{"items":[]}""");
            }

            if (request.Method == HttpMethod.Put && path.Contains("/youtube/v3/videos", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(body);
                var id = doc.RootElement.GetProperty("id").GetString()!;
                var snippet = doc.RootElement.GetProperty("snippet");
                if (Videos.TryGetValue(id, out var v))
                    v.Description = snippet.GetProperty("description").GetString() ?? "";
                return JsonResponse(HttpStatusCode.OK,
                    JsonSerializer.Serialize(new { id }));
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"error":{"code":404,"message":"not found"}}""", Encoding.UTF8, "application/json"),
            };
        }

        private static async Task<string> ReadBodyAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is null)
                return "";
            var bytes = await request.Content.ReadAsByteArrayAsync(ct);
            if (request.Content.Headers.ContentEncoding.Contains("gzip"))
            {
                using var input = new MemoryStream(bytes);
                using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                return await reader.ReadToEndAsync(ct);
            }
            return Encoding.UTF8.GetString(bytes);
        }

        private static string? GetQuery(string query, string key)
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && Uri.UnescapeDataString(kv[0]) == key)
                    return Uri.UnescapeDataString(kv[1]);
            }
            return null;
        }
    }

    private static YouTubeService CannedYouTube(FakeYouTubeHandler handler) =>
        new(new BaseClientService.Initializer
        {
            // Google.Apis injects HTTP via an IHttpClientFactory, not an
            // HttpClient property — the canned handler rides in this way.
            HttpClientFactory = new CannedHttpClientFactory(handler),
            ApplicationName = "item5-test",
        });

    private sealed class CannedHttpClientFactory : Google.Apis.Http.IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public CannedHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public Google.Apis.Http.ConfigurableHttpClient CreateHttpClient(Google.Apis.Http.CreateHttpClientArgs args) =>
            new(new Google.Apis.Http.ConfigurableMessageHandler(_handler));
    }

    private static object YouTubePublisherWith(string dir)
    {
        var cfg = NewConfig("YouTubeConfig");
        return NewPublisher("YouTubePublisher", cfg, NewLogger(dir));
    }

    private static Task Reconcile(object publisher, YouTubeService youtube, string videoPath, Sidecar sidecar)
    {
        var method = FindInstanceMethod(publisher.GetType(), "ReconcileCrossLinksWithServiceAsync");
        var task = (Task)method.Invoke(publisher, [youtube, videoPath, sidecar, CancellationToken.None])!;
        return task;
    }

    private static void RunReconcile(object publisher, YouTubeService youtube, string videoPath, Sidecar sidecar) =>
        Reconcile(publisher, youtube, videoPath, sidecar).GetAwaiter().GetResult();

    private static Sidecar WriteSidecar(string videoPath, string title, string description, string channel,
        string aspect, (string Platform, PublishStatus Status, string? Id)[] publications)
    {
        File.WriteAllText(videoPath, ""); // the video file itself is never read here
        var sidecar = new Sidecar
        {
            Title = title,
            Description = description,
            Channel = channel,
            Aspect = aspect,
            Targets = [Platforms.YouTube],
        };
        foreach (var (platform, status, id) in publications)
            sidecar.Publications[platform] = new Publication { Status = status, Id = id };
        File.WriteAllText(Sidecar.PathFor(videoPath), JsonSerializer.Serialize(sidecar, Sidecar.Options));
        return sidecar;
    }

    private static Sidecar LoadSidecar(string videoPath) =>
        Sidecar.LoadAsync(videoPath).GetAwaiter().GetResult()!;

    // Blocking sidecar save, kept out of the [Fact] bodies so the
    // xUnit1031 analyzer (blocking calls in test methods) stays quiet —
    // the sandbox runner never awaits a returned Task, so tests block in
    // helpers instead, the same pattern UploaderTests uses.
    private static void SaveSidecar(Sidecar sidecar, string videoPath) =>
        sidecar.SaveAsync(videoPath).GetAwaiter().GetResult();

    private const string ShortDescWithoutLink =
        "GTA 6 story.\n\nDisclaimer: This video is for informational and entertainment purposes.\n\n" +
        "Subscribe for a new star story every day: https://www.youtube.com/@spotlightnowofficial";

    private const string LongDescWithoutLink =
        "GTA 6 long-form.\n\nDisclaimer: This video is for informational and entertainment purposes.\n\n" +
        "Subscribe for a new star story every day: https://www.youtube.com/@spotlightnowofficial";

    [Fact]
    public void CrossLink_ShortFirstOrdering_BothDirectionsLinkedAfterSecondRun()
    {
        // The R3 scenario, replayed: run 1 publishes the Short while the
        // long-form is not published yet, so the Short footer ships without
        // its "Watch the full video" link. Run 2 (long-form now Published)
        // must link BOTH directions; run 3 must be a no-op. No request may
        // ever be an upload — reconciliation only patches descriptions.
        var dir = TempDir("xlink-short-first");
        var channelDir = Path.Combine(dir, "trendlinenow");
        Directory.CreateDirectory(channelDir);

        var shortPath = Path.Combine(channelDir, "slug-en-short.mp4");
        var longDir = Path.Combine(channelDir, "done", "slug-en");
        Directory.CreateDirectory(longDir);
        var longPath = Path.Combine(longDir, "slug-en.mp4");

        WriteSidecar(shortPath, "Short title", ShortDescWithoutLink, "trendlinenow", "vertical",
            [("youtube", PublishStatus.Published, "short123")]);
        WriteSidecar(longPath, "Long title", LongDescWithoutLink, "trendlinenow", "landscape", []);

        var handler = new FakeYouTubeHandler();
        handler.Videos["short123"] = new FakeVideo { Title = "Short title", Description = ShortDescWithoutLink };
        handler.Videos["long456"] = new FakeVideo { Title = "Long title", Description = LongDescWithoutLink };
        var youtube = CannedYouTube(handler);
        var publisher = YouTubePublisherWith(dir);

        // Run 1: long-form not published — nothing to link yet.
        RunReconcile(publisher, youtube, shortPath, LoadSidecar(shortPath));
        Assert.Equal(0, handler.PutCount);
        Assert.DoesNotContain("youtu.be/long456", handler.Videos["short123"].Description);

        // The long-form publishes between runs (its sidecar now records it).
        var longSidecar = LoadSidecar(longPath);
        longSidecar.Publications[Platforms.YouTube] = new Publication { Status = PublishStatus.Published, Id = "long456" };
        SaveSidecar(longSidecar, longPath);

        // Run 2: both directions must now be linked.
        RunReconcile(publisher, youtube, shortPath, LoadSidecar(shortPath));
        Assert.Equal(2, handler.PutCount);
        Assert.Contains("Watch the full video: https://youtu.be/long456", handler.Videos["short123"].Description);
        Assert.Contains("Watch the 60-second version: https://youtu.be/short123", handler.Videos["long456"].Description);
        Assert.Contains("Watch the full video: https://youtu.be/long456", LoadSidecar(shortPath).Description);
        Assert.Contains("Watch the 60-second version: https://youtu.be/short123", LoadSidecar(longPath).Description);

        // Run 3: idempotent — no further updates.
        RunReconcile(publisher, youtube, shortPath, LoadSidecar(shortPath));
        Assert.Equal(2, handler.PutCount);

        // No re-upload ever happened: every request was a GET or a PUT on
        // the videos endpoint (list/update), never an upload initiation.
        Assert.DoesNotContain(handler.Requests, r => r.Method == "POST");
        Assert.DoesNotContain(handler.Requests, r => r.Url.Contains("uploadType", StringComparison.Ordinal));
        Assert.All(handler.Requests, r => Assert.Contains("/youtube/v3/videos", r.Url, StringComparison.Ordinal));
    }

    [Fact]
    public void CrossLink_LongFormSideReconcilesToo()
    {
        // Reconciliation is not Short-only: when the long-form is the video
        // being processed, it must link both directions as well (this is
        // the run where a Short-first ordering finally heals).
        var dir = TempDir("xlink-long-side");
        var channelDir = Path.Combine(dir, "trendlinenow");
        Directory.CreateDirectory(channelDir);

        var longPath = Path.Combine(channelDir, "slug-en.mp4");
        var shortPath = Path.Combine(channelDir, "slug-en-short.mp4");
        WriteSidecar(longPath, "Long title", LongDescWithoutLink, "trendlinenow", "landscape",
            [("youtube", PublishStatus.Published, "long456")]);
        WriteSidecar(shortPath, "Short title", ShortDescWithoutLink, "trendlinenow", "vertical",
            [("youtube", PublishStatus.Published, "short123")]);

        var handler = new FakeYouTubeHandler();
        handler.Videos["short123"] = new FakeVideo { Title = "Short title", Description = ShortDescWithoutLink };
        handler.Videos["long456"] = new FakeVideo { Title = "Long title", Description = LongDescWithoutLink };
        var youtube = CannedYouTube(handler);
        var publisher = YouTubePublisherWith(dir);

        RunReconcile(publisher, youtube, longPath, LoadSidecar(longPath));
        Assert.Equal(2, handler.PutCount);
        Assert.Contains("Watch the full video: https://youtu.be/long456", handler.Videos["short123"].Description);
        Assert.Contains("Watch the 60-second version: https://youtu.be/short123", handler.Videos["long456"].Description);
    }

    [Fact]
    public void CrossLink_UpdateEchoesLiveSnippetNotSidecarValues()
    {
        // R4 mechanism, as required for the reconciliation updates: the
        // update must only change the description. The sidecar's title is
        // deliberately stale (a manual Studio edit changed it live); the
        // PUT body must carry the LIVE title/tags/category, never the
        // sidecar's values — otherwise reconciliation would clobber the
        // Studio edit it was supposed to preserve.
        var dir = TempDir("xlink-live-echo");
        var channelDir = Path.Combine(dir, "trendlinenow");
        Directory.CreateDirectory(channelDir);

        var shortPath = Path.Combine(channelDir, "slug-en-short.mp4");
        var longDir = Path.Combine(channelDir, "done", "slug-en");
        Directory.CreateDirectory(longDir);
        var longPath = Path.Combine(longDir, "slug-en.mp4");

        WriteSidecar(shortPath, "STALE SIDECAR TITLE", ShortDescWithoutLink, "trendlinenow", "vertical",
            [("youtube", PublishStatus.Published, "short123")]);
        // Long-form already linked live, but its sidecar is behind — the
        // long-form direction must sync the sidecar with no PUT.
        WriteSidecar(longPath, "Long title", LongDescWithoutLink, "trendlinenow", "landscape",
            [("youtube", PublishStatus.Published, "long456")]);

        var handler = new FakeYouTubeHandler();
        handler.Videos["short123"] = new FakeVideo
        {
            Title = "Live Studio Title",
            Description = ShortDescWithoutLink,
            Tags = ["live", "tag"],
            CategoryId = "24",
        };
        handler.Videos["long456"] = new FakeVideo
        {
            Title = "Long title",
            Description = LongDescWithoutLink + "\n\nWatch the 60-second version: https://youtu.be/short123",
        };
        var youtube = CannedYouTube(handler);
        var publisher = YouTubePublisherWith(dir);

        RunReconcile(publisher, youtube, shortPath, LoadSidecar(shortPath));

        // Exactly one PUT (the Short); the long-form direction synced its
        // sidecar from the live description instead.
        Assert.Equal(1, handler.PutCount);
        Assert.Contains("Watch the 60-second version: https://youtu.be/short123", LoadSidecar(longPath).Description);

        var put = handler.Requests.First(r => r.Method == "PUT");
        using var body = JsonDocument.Parse(put.Body);
        var snippet = body.RootElement.GetProperty("snippet");
        Assert.Equal("Live Studio Title", snippet.GetProperty("title").GetString());
        Assert.DoesNotContain("STALE SIDECAR TITLE", put.Body);
        Assert.Equal("24", snippet.GetProperty("categoryId").GetString());
        Assert.Contains("Watch the full video: https://youtu.be/long456",
            snippet.GetProperty("description").GetString());
    }

    [Fact]
    public void CrossLink_AlreadyLinked_IsNoop()
    {
        // Both directions already live and both sidecars already carry the
        // links: reconciliation must not touch anything.
        var dir = TempDir("xlink-noop");
        var channelDir = Path.Combine(dir, "trendlinenow");
        Directory.CreateDirectory(channelDir);

        var shortDesc = ShortDescWithoutLink + "\n\nWatch the full video: https://youtu.be/long456";
        var longDesc = LongDescWithoutLink + "\n\nWatch the 60-second version: https://youtu.be/short123";
        var shortPath = Path.Combine(channelDir, "slug-en-short.mp4");
        var longPath = Path.Combine(channelDir, "slug-en.mp4");
        WriteSidecar(shortPath, "Short title", shortDesc, "trendlinenow", "vertical",
            [("youtube", PublishStatus.Published, "short123")]);
        WriteSidecar(longPath, "Long title", longDesc, "trendlinenow", "landscape",
            [("youtube", PublishStatus.Published, "long456")]);

        var handler = new FakeYouTubeHandler();
        handler.Videos["short123"] = new FakeVideo { Title = "Short title", Description = shortDesc };
        handler.Videos["long456"] = new FakeVideo { Title = "Long title", Description = longDesc };
        var youtube = CannedYouTube(handler);
        var publisher = YouTubePublisherWith(dir);

        RunReconcile(publisher, youtube, shortPath, LoadSidecar(shortPath));
        Assert.Equal(0, handler.PutCount);
    }

    [Fact]
    public void EnsureLinkInDescription_AddsOnceThenNoop()
    {
        // The single-direction helper directly: first call adds the link
        // (returns true, one PUT, sidecar saved); second call sees it live
        // and does nothing (returns false, no PUT).
        var dir = TempDir("xlink-helper");
        var channelDir = Path.Combine(dir, "trendlinenow");
        Directory.CreateDirectory(channelDir);
        var shortPath = Path.Combine(channelDir, "slug-en-short.mp4");
        WriteSidecar(shortPath, "Short title", ShortDescWithoutLink, "trendlinenow", "vertical",
            [("youtube", PublishStatus.Published, "short123")]);

        var handler = new FakeYouTubeHandler();
        handler.Videos["short123"] = new FakeVideo { Title = "Short title", Description = ShortDescWithoutLink };
        var youtube = CannedYouTube(handler);
        var publisher = YouTubePublisherWith(dir);

        var method = FindInstanceMethod(publisher.GetType(), "EnsureLinkInDescriptionAsync");
        bool Call() => CallEnsureLink(method, publisher, youtube, shortPath);

        Assert.True(Call());
        Assert.Equal(1, handler.PutCount);
        Assert.Contains("Watch the full video: https://youtu.be/long456", LoadSidecar(shortPath).Description);
        Assert.False(Call());
        Assert.Equal(1, handler.PutCount);
    }

    private static bool CallEnsureLink(MethodInfo method, object publisher, YouTubeService youtube, string videoPath)
    {
        var task = (Task<bool>)method.Invoke(publisher,
            [youtube, videoPath, LoadSidecar(videoPath),
             "Watch the full video: https://youtu.be/long456", "https://youtu.be/long456",
             CancellationToken.None])!;
        return task.GetAwaiter().GetResult();
    }

    [Fact]
    public void ProgramCallsReconcileCrossLinks()
    {
        // Source guard: the uploader's run loop must invoke the
        // reconciliation after a video's targets are processed, or R3's
        // per-run pass never happens in production.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "production", "finish-chunks.py")))
            dir = dir.Parent;
        var repoRoot = dir?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate repo root (production/finish-chunks.py).");
        var source = File.ReadAllText(Path.Combine(repoRoot, "BroadcastContentAutomation", "Uploader", "Program.cs"));
        Assert.Contains("ReconcileCrossLinksAsync", source);
    }
}
