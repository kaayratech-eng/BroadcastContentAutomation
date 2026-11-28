// Second-Short 12h hold + own thumbnail (Rishi 2026-11-27, 2-Shorts fix:
// keep 2 Shorts, but the 2nd is a different moment, carries its own title
// and thumbnail, and goes out ~12h after the main Short — corrected from
// 24h at 18:07 EST; cadence set 18:19 EST: something every 12h, long-form
// + main Short together, 2nd Short 12h later, next story 12h after that).
//
// The hold is SIBLING-ANCHORED: it is computed from the main Short's live
// YouTube publish time (At + 12h), not from the finish-written
// PublishAfterUtc — that value is only the fallback when the main Short's
// sidecar cannot be found. A held file is skipped whole in the dispatch
// loop (Program.cs) before any target is dispatched — no target marked,
// no MaxPerRun slot or YouTube dispatch count consumed, file left in the
// watch dir. When the main Short IS published, the same check also
// re-anchors the short2 sidecar's stored PublishAfterUtc to At + 12h
// (saved only when it differs), so the sidecar always shows the governing
// hold rather than the finish-written provisional. The loop itself is not
// restructured to be testable; the pure SecondShortHoldReason helper,
// the ReanchorPublishAfterUtc helper, and the FindMainShortSidecarAsync
// lookup are pinned here instead. The thumbnail resolution needs no code change
// — ResolveThumbnailPath's direct same-stem check already maps
// "<slug>-en-short2.mp4" to "<slug>-en-short2.thumb.jpg" — so these tests
// prove that path instead of a new branch.
using GiggleGarden.Shared;

namespace BroadcastContentAutomation.Tests;

public class SecondShortUploadTests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tn-short2up-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // --- 12h hold: IsPublishHeld(publishAfterUtc, now). ---

    [Fact]
    public void IsPublishHeld_FutureHold_Held()
    {
        // The hold the finish writes: finish time + 12h.
        var now = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
        Assert.True(IsHeld(now.AddHours(12), now));
    }

    [Fact]
    public void IsPublishHeld_PastHold_NotHeld()
    {
        var now = new DateTimeOffset(2026, 11, 28, 7, 0, 0, TimeSpan.Zero);
        Assert.False(IsHeld(now.AddHours(-12), now));
    }

    [Fact]
    public void IsPublishHeld_NullHold_NotHeld()
    {
        // Null = no hold: every long-form, main Short, and old sidecar.
        var now = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
        Assert.False(IsHeld(null, now));
    }

    [Fact]
    public void IsPublishHeld_HoldExactlyNow_NotHeld()
    {
        // The boundary: the hold has passed the instant it is reached.
        var now = new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero);
        Assert.False(IsHeld(now, now));
    }

    private static bool IsHeld(DateTimeOffset? publishAfterUtc, DateTimeOffset now) =>
        ReflectionHelper.Invoke<bool>(ReflectionHelper.Uploader, "IsPublishHeld", publishAfterUtc, now);

    // --- Sibling-anchored hold: SecondShortHoldReason(mainShortSidecar,
    // fallbackPublishAfterUtc, now). The anchor is the main Short's live
    // YouTube publish time (At + 12h). ---

    private static Sidecar MainShortPublishedAt(DateTimeOffset at) => new()
    {
        Title = "Main Short",
        Publications =
        {
            [Platforms.YouTube] = new Publication { Status = PublishStatus.Published, At = at },
        },
    };

    [Fact]
    public void HoldReason_SiblingPublished_HeldBefore12h()
    {
        var publishedAt = new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero);
        var reason = HoldReason(MainShortPublishedAt(publishedAt), null, publishedAt.AddHours(11));
        Assert.NotNull(reason);
        Assert.Contains("12h hold until 2026-11-28 18:00:00 UTC", reason);
        Assert.Contains("anchored off the main Short's publish time", reason);
        Assert.Contains("waiting for the next uploader run after the hold passes", reason);
    }

    [Fact]
    public void HoldReason_SiblingPublished_ReleasedAt12h()
    {
        // The boundary: at exactly At + 12h the hold has passed.
        var publishedAt = new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero);
        Assert.Null(HoldReason(MainShortPublishedAt(publishedAt), null, publishedAt.AddHours(12)));
        Assert.Null(HoldReason(MainShortPublishedAt(publishedAt), null, publishedAt.AddHours(13)));
    }

    [Fact]
    public void HoldReason_SiblingUnpublished_Skip()
    {
        var now = new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero);
        // Still pending on YouTube.
        var pending = new Sidecar
        {
            Title = "Main Short",
            Publications = { [Platforms.YouTube] = new Publication { Status = PublishStatus.Pending } },
        };
        var reason = HoldReason(pending, null, now);
        Assert.NotNull(reason);
        Assert.Contains("main Short not published yet", reason);

        // No YouTube publication at all.
        Assert.Contains("main Short not published yet", HoldReason(new Sidecar { Title = "Main Short" }, null, now)!);

        // Published but with no timestamp: no anchor to compute from.
        var noAt = MainShortPublishedAt(now);
        noAt.Publications[Platforms.YouTube].At = null;
        Assert.Contains("main Short not published yet", HoldReason(noAt, null, now)!);
    }

    [Fact]
    public void HoldReason_SiblingMissing_FallbackFuture_Held()
    {
        var now = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
        var reason = HoldReason(null, now.AddHours(12), now);
        Assert.NotNull(reason);
        Assert.Contains("fallback PublishAfterUtc hold until 2026-11-28 06:00:00 UTC governs", reason);
        Assert.Contains("waiting for the next uploader run after the hold passes", reason);
    }

    [Fact]
    public void HoldReason_SiblingMissing_FallbackNullOrPast_Proceed()
    {
        var now = new DateTimeOffset(2026, 11, 28, 7, 0, 0, TimeSpan.Zero);
        Assert.Null(HoldReason(null, null, now));
        Assert.Null(HoldReason(null, now.AddHours(-1), now));
    }

    private static string? HoldReason(Sidecar? mainShortSidecar, DateTimeOffset? fallbackPublishAfterUtc, DateTimeOffset now) =>
        ReflectionHelper.Invoke<string?>(ReflectionHelper.Uploader, "SecondShortHoldReason", mainShortSidecar, fallbackPublishAfterUtc, now);

    // --- Re-anchor: ReanchorPublishAfterUtc(short2Sidecar, mainShortSidecar).
    // Sibling published at T -> the stored PublishAfterUtc becomes T + 12h. ---

    [Fact]
    public void Reanchor_SiblingPublishedAtT_StoredBecomesTPlus12h()
    {
        // The finish-written provisional (finish + 12h) is replaced by
        // the governing hold (main Short's live publish time + 12h) —
        // e.g. the main Short went live late, 4.5h after the finish.
        var short2 = new Sidecar
        {
            Title = "Second Short",
            PublishAfterUtc = new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero),
        };
        var publishedAt = new DateTimeOffset(2026, 11, 28, 10, 30, 0, TimeSpan.Zero);
        Assert.True(Reanchor(short2, MainShortPublishedAt(publishedAt)));
        Assert.Equal(publishedAt.AddHours(12), short2.PublishAfterUtc);
    }

    [Fact]
    public void Reanchor_AlreadyAnchored_NoChange()
    {
        var publishedAt = new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero);
        var short2 = new Sidecar { Title = "Second Short", PublishAfterUtc = publishedAt.AddHours(12) };
        Assert.False(Reanchor(short2, MainShortPublishedAt(publishedAt)));
        Assert.Equal(publishedAt.AddHours(12), short2.PublishAfterUtc);
    }

    [Fact]
    public void Reanchor_SiblingUnpublishedOrMissing_NoChange()
    {
        var stored = new DateTimeOffset(2026, 11, 28, 6, 0, 0, TimeSpan.Zero);
        var short2 = new Sidecar { Title = "Second Short", PublishAfterUtc = stored };
        var pending = new Sidecar
        {
            Title = "Main Short",
            Publications = { [Platforms.YouTube] = new Publication { Status = PublishStatus.Pending } },
        };
        Assert.False(Reanchor(short2, pending));
        Assert.False(Reanchor(short2, null));
        Assert.Equal(stored, short2.PublishAfterUtc);
    }

    private static bool Reanchor(Sidecar short2Sidecar, Sidecar? mainShortSidecar) =>
        ReflectionHelper.Invoke<bool>(ReflectionHelper.Uploader, "ReanchorPublishAfterUtc", short2Sidecar, mainShortSidecar);

    // --- Sibling lookup: FindMainShortSidecarAsync(short2Path). ---

    [Fact]
    public async Task FindMainShortSidecar_SameDir_Found()
    {
        var dir = TempDir("sibsame");
        try
        {
            var mainShort = Path.Combine(dir, "test-video-en-short.mp4");
            await new Sidecar { Title = "Main Short" }.SaveAsync(mainShort);
            var short2Path = Path.Combine(dir, "test-video-en-short2.mp4");
            File.WriteAllText(short2Path, "");

            var found = await InvokeFindMainShortAsync(short2Path);
            Assert.NotNull(found);
            Assert.Equal("Main Short", found!.Title);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FindMainShortSidecar_DoneDir_Found()
    {
        // By the time the uploader reaches the second Short on a later
        // run, the main Short has usually moved to done/.
        var dir = TempDir("sibdone");
        try
        {
            var doneShort = Path.Combine(dir, "done", "test-video-en-short", "test-video-en-short.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(doneShort)!);
            await new Sidecar { Title = "Main Short Done" }.SaveAsync(doneShort);
            var short2Path = Path.Combine(dir, "test-video-en-short2.mp4");
            File.WriteAllText(short2Path, "");

            var found = await InvokeFindMainShortAsync(short2Path);
            Assert.NotNull(found);
            Assert.Equal("Main Short Done", found!.Title);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FindMainShortSidecar_SameDirBeatsDoneDir()
    {
        var dir = TempDir("sibboth");
        try
        {
            var mainShort = Path.Combine(dir, "test-video-en-short.mp4");
            await new Sidecar { Title = "Main Short Here" }.SaveAsync(mainShort);
            var doneShort = Path.Combine(dir, "done", "test-video-en-short", "test-video-en-short.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(doneShort)!);
            await new Sidecar { Title = "Main Short Done" }.SaveAsync(doneShort);
            var short2Path = Path.Combine(dir, "test-video-en-short2.mp4");
            File.WriteAllText(short2Path, "");

            var found = await InvokeFindMainShortAsync(short2Path);
            Assert.NotNull(found);
            Assert.Equal("Main Short Here", found!.Title);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FindMainShortSidecar_Missing_ReturnsNull()
    {
        var dir = TempDir("sibmiss");
        try
        {
            var short2Path = Path.Combine(dir, "test-video-en-short2.mp4");
            File.WriteAllText(short2Path, "");
            Assert.Null(await InvokeFindMainShortAsync(short2Path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task FindMainShortSidecar_CorruptSibling_ReturnsNull()
    {
        // A corrupt main-Short sidecar must not throw into the second
        // Short's run — it falls back to PublishAfterUtc instead.
        var dir = TempDir("sibbad");
        try
        {
            File.WriteAllText(Path.Combine(dir, "test-video-en-short.json"), "{not json");
            var short2Path = Path.Combine(dir, "test-video-en-short2.mp4");
            File.WriteAllText(short2Path, "");
            Assert.Null(await InvokeFindMainShortAsync(short2Path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<Sidecar?> InvokeFindMainShortAsync(string short2Path)
    {
        var method = ReflectionHelper.FindMethod(ReflectionHelper.Uploader, "FindMainShortSidecarAsync");
        var task = (Task)method.Invoke(null, [short2Path, null])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        return (Sidecar?)result;
    }

    // --- Own thumbnail: a short2 resolves to its sibling .thumb.jpg. ---

    [Fact]
    public void ResolveThumbnailPath_Short2_UsesOwnSiblingThumb()
    {
        var dir = TempDir("ownthumb");
        try
        {
            var short2Path = Path.Combine(dir, "test-video-en-short2.mp4");
            File.WriteAllText(short2Path, "");
            var thumbPath = Path.Combine(dir, "test-video-en-short2.thumb.jpg");
            File.WriteAllText(thumbPath, "");

            Assert.Equal(thumbPath, ResolveThumbnail(short2Path, new Sidecar()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveThumbnailPath_Short2_OwnThumbBeatsLandscapeThumb()
    {
        // Both thumbs present: the second Short must take its OWN
        // thumbnail, never the long-form's.
        var dir = TempDir("ownfirst");
        try
        {
            var short2Path = Path.Combine(dir, "test-video-en-short2.mp4");
            File.WriteAllText(short2Path, "");
            File.WriteAllText(Path.Combine(dir, "test-video-en.thumb.jpg"), "");
            var ownThumb = Path.Combine(dir, "test-video-en-short2.thumb.jpg");
            File.WriteAllText(ownThumb, "");

            Assert.Equal(ownThumb, ResolveThumbnail(short2Path, new Sidecar()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string? ResolveThumbnail(string videoPath, Sidecar sidecar) =>
        ReflectionHelper.Invoke<string?>(ReflectionHelper.Uploader, "ResolveThumbnailPath", videoPath, sidecar);
}
