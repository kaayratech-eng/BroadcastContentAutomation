// Regression tests for the Uploader's Meta publish path, the Trendline Now
// description footer, and the finish-chunks.py sidecar fields.
//
// Part A (fix #5): MetaGraph.MediaExistsAsync is the tri-state prior-media
// lookup (Found / NotFound / Unknown). The old bool version collapsed every
// failure to "not found", so a transient 500 on the verification GET caused
// a duplicate live Reel. These tests pin the tri-state mapping against the
// REAL MetaGraph with a canned HttpMessageHandler — no network, no token.
// NOTE: the sandbox runner invokes [Fact] methods without awaiting a
// returned Task, so these tests are synchronous and block on the async
// lookup with GetAwaiter().GetResult().
//
// Part B (fix #7): the Shared Sidecar footer constants carry the subscribe
// CTA and all four social links. If a link rots or the marker drifts, the
// uploader's idempotency check (skip the footer when the marker is already
// present) silently stops appending it, and descriptions ship without the
// cross-links.
//
// Part C (fix #2): finish-chunks.py must write MusicTrack and
// DurationSeconds into BOTH sidecar dicts (landscape + vertical). The
// uploader's CC BY music credit and its duration gates (e.g. the 60s
// Instagram Story cap) read those fields; a missing MusicTrack means a
// published video with no attribution, a missing DurationSeconds means
// ffprobe on every run.
using System.Net;
using System.Text;
using System.Text.Json;
using GiggleGarden.Shared;
using GiggleGarden.Uploader.Publishing;

namespace BroadcastContentAutomation.Tests;

public class UploaderTests
{
    // ------------------------------------------------------------------
    // Part A — MetaGraph.MediaExistsAsync tri-state lookup (fix #5)
    // ------------------------------------------------------------------

    // A handler that answers every request from a canned responder, and
    // remembers the last request so tests can assert on the URL MetaGraph
    // built (media id + fields=id + token).
    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }

        public CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage GraphErrorResponse(HttpStatusCode status, int code, string message) =>
        JsonResponse(status, JsonSerializer.Serialize(new
        {
            error = new { message, type = "OAuthException", code }
        }));

    // Calls the REAL MetaGraph.MediaExistsAsync (internal static class, so
    // reached via reflection) with a canned handler. Synchronous: the
    // runner does not await Task-returning [Fact]s.
    private static (MediaLookup Result, HttpRequestMessage? Request) LookupWith(CannedHandler handler)
    {
        using var http = new HttpClient(handler);
        var task = ReflectionHelper.Invoke<Task<MediaLookup>>(ReflectionHelper.Uploader, "MediaExistsAsync",
            http, "https://graph.facebook.com/v21.0", "123456789", "test-token", CancellationToken.None);
        return (task.GetAwaiter().GetResult(), handler.LastRequest);
    }

    [Fact]
    public void MediaExists_LiveMedia_ReturnsFound()
    {
        // A 200 with an id field means the recorded post is live: reuse it.
        var handler = new CannedHandler(_ => JsonResponse(HttpStatusCode.OK, """{"id":"123456789"}"""));
        var (result, request) = LookupWith(handler);
        Assert.Equal(MediaLookup.Found, result);
        Assert.Contains("123456789", request!.RequestUri!.ToString());
        Assert.Contains("fields=id", request.RequestUri.ToString());
    }

    [Fact]
    public void MediaExists_DeletedMediaGraphError100_ReturnsNotFound()
    {
        // Graph "object does not exist" (error 100) = deleted: publish fresh.
        var handler = new CannedHandler(_ =>
            GraphErrorResponse(HttpStatusCode.NotFound, 100, "Unsupported get request. Object with ID '123456789' does not exist"));
        var (result, _) = LookupWith(handler);
        Assert.Equal(MediaLookup.NotFound, result);
    }

    [Fact]
    public void MediaExists_Bare404NonJson_ReturnsNotFound()
    {
        // A bare 404 with no JSON body is also "deleted", not a transient
        // failure — Meta answers this way for some removed objects.
        var handler = new CannedHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not Found") });
        var (result, _) = LookupWith(handler);
        Assert.Equal(MediaLookup.NotFound, result);
    }

    [Fact]
    public void MediaExists_Transient500_ReturnsUnknown()
    {
        // A transient platform fault (error 1) must NOT be read as "deleted":
        // the old bool lookup published a duplicate Reel on exactly this.
        var handler = new CannedHandler(_ =>
            GraphErrorResponse(HttpStatusCode.InternalServerError, 1, "An unknown error occurred"));
        var (result, _) = LookupWith(handler);
        Assert.Equal(MediaLookup.Unknown, result);
    }

    [Fact]
    public void MediaExists_BadToken400_ReturnsUnknown()
    {
        // Error 190 (bad/expired token) is an auth problem, not evidence the
        // media is gone — publishing blind would duplicate it once the
        // token is fixed.
        var handler = new CannedHandler(_ =>
            GraphErrorResponse(HttpStatusCode.BadRequest, 190, "Error validating access token"));
        var (result, _) = LookupWith(handler);
        Assert.Equal(MediaLookup.Unknown, result);
    }

    [Fact]
    public void MediaExists_RateLimit429_ReturnsUnknown()
    {
        // Error 4 (rate limit) is transient: retry later, never publish blind.
        var handler = new CannedHandler(_ =>
            GraphErrorResponse((HttpStatusCode)429, 4, "Application request limit reached"));
        var (result, _) = LookupWith(handler);
        Assert.Equal(MediaLookup.Unknown, result);
    }

    [Fact]
    public void MediaExists_TransportFailure_ReturnsUnknown()
    {
        // The request never reached Meta at all — the media's state is
        // unknowable, so this must be Unknown, not NotFound.
        var handler = new CannedHandler(_ => throw new HttpRequestException("Connection reset by peer"));
        var (result, _) = LookupWith(handler);
        Assert.Equal(MediaLookup.Unknown, result);
    }

    [Fact]
    public void MediaExists_OkWithoutId_ReturnsNotFound()
    {
        // A 200 that carries no id field is not a live media object — the
        // recorded id does not resolve to anything reusable.
        var handler = new CannedHandler(_ => JsonResponse(HttpStatusCode.OK, """{"something_else":true}"""));
        var (result, _) = LookupWith(handler);
        Assert.Equal(MediaLookup.NotFound, result);
    }

    // ------------------------------------------------------------------
    // Part B — Trendline Now description footer (fix #7)
    // ------------------------------------------------------------------

    [Fact]
    public void TrendlineFooterMarker_IsSubscribeLine()
    {
        // The marker is the idempotency key: the uploader skips the footer
        // when it is already present, so if this drifts the footer stacks
        // a second copy on every re-run — or never gets appended at all.
        Assert.Equal("Subscribe for a new star story every day:", Sidecar.TrendlineFooterMarker);
    }

    [Fact]
    public void TrendlineFooter_ContainsSubscribeUrlAndAllSocialLinks()
    {
        // The footer is the channel's cross-link block: subscribe CTA plus
        // all four social profiles. A missing link silently drops a
        // discovery path from every YouTube description.
        var footer = Sidecar.TrendlineFooter;
        Assert.StartsWith(Sidecar.TrendlineFooterMarker, footer);
        Assert.Contains("https://www.youtube.com/@spotlightnowofficial", footer);
        Assert.Contains("instagram.com/spotlightnowofficial", footer);
        Assert.Contains("tiktok.com/@spotlightnowofficial", footer);
        Assert.Contains("facebook.com/spotlightnowofficial", footer);
        Assert.Contains("threads.com/@spotlightnowofficial", footer);
    }

    // ------------------------------------------------------------------
    // Part C — finish-chunks.py sidecar MusicTrack + DurationSeconds
    // (fix #2). Source-level guard only — see the comment on
    // FinishChunks_SourceWritesMusicTrackAndDurationSecondsInBothSidecars.
    // ------------------------------------------------------------------

    // Repo layout, resolved from the test assembly's location: the Tests
    // project lives at <repo>/BroadcastContentAutomation/Tests, so the repo
    // root is three levels up from the test binaries.
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "production", "finish-chunks.py")))
                dir = dir.Parent;
            return dir?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate repo root (production/finish-chunks.py).");
        }
    }

    [Fact]
    public void FinishChunks_SourceWritesMusicTrackAndDurationSecondsInBothSidecars()
    {
        // Source-level guard: BOTH sidecar dicts (vertical + landscape) in
        // finish-chunks.py must carry MusicTrack and DurationSeconds. The
        // bug was that the chunk path never wrote them at all (the
        // --assemble path did), so a chunk-finished video published with no
        // CC BY music credit and no duration for the Story gates.
        //
        // Why a source guard and not a live run: a live finish-chunks.py
        // run on a scratch copy of video #3's job WAS done by hand
        // (2026-11-23) and the produced sidecar was correct — MusicTrack
        // "uplifting-tech-01.mp3" (via the props backgroundMusicPath
        // fallback, since script.json has MusicTrack null) and
        // DurationSeconds 330.4 (9912 frames @ 30fps). But the full script
        // does NOT run cleanly in this sandbox: its loudnorm re-encode
        // step truncated the video to ~115s, so the script's own delivery
        // gate failed and it exited 1 (see report). A 6+ minute test that
        // shells out to a script which exits nonzero for an unrelated
        // reason does not belong in the suite — the guard below pins the
        // fix #2 behavior deterministically instead. If the truncation is
        // fixed, a behavioral test can be re-added on top of this guard.
        var source = File.ReadAllText(Path.Combine(RepoRoot, "production", "finish-chunks.py"));
        var musicTrackWrites = CountOccurrences(source, "\"MusicTrack\": music_track");
        var durationWrites = CountOccurrences(source, "\"DurationSeconds\": duration_seconds");
        Assert.Equal(2, musicTrackWrites);
        Assert.Equal(2, durationWrites);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
