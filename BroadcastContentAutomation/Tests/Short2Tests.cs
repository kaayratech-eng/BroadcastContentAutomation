// Fix #5 (Rishi 2026-11-27 — cadence: 1 long-form/day, the freed slot is
// a second Short, not a second long-form).
//
// The second Short ("-short2") is a plain ffmpeg tail cut of the finished
// vertical master. These tests pin the three uploader-side behaviours:
// the "-short2" naming is recognised wherever "-short" is, the sibling
// lookup finds the long-form for a second Short (so the bridge works for
// it), and the YouTube quota guard only allows the bonus dispatch when
// the day's headroom fits it.
//
// The sandbox runner awaits async [Fact] methods, so a test that awaits
// uses async Task (xUnit1031: never block with GetAwaiter().GetResult()).
using System.Reflection;
using GiggleGarden.Shared;

namespace BroadcastContentAutomation.Tests;

public class Short2Tests
{
    private static string TempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tn-short2-{tag}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ShortVideo_IsShort_RecognisesBothSuffixes()
    {
        Assert.True(ShortVideo.IsShort("test-video-en-short.mp4"));
        Assert.True(ShortVideo.IsShort("test-video-en-short2.mp4"));
        Assert.False(ShortVideo.IsShort("test-video-en.mp4"));
        Assert.False(ShortVideo.IsShort("test-video-en-story60.mp4"));
        Assert.False(ShortVideo.IsShort("test-video-en-short2.json"));
    }

    [Fact]
    public void ShortVideo_IsSecondShort_OnlySecond()
    {
        Assert.True(ShortVideo.IsSecondShort("test-video-en-short2.mp4"));
        Assert.False(ShortVideo.IsSecondShort("test-video-en-short.mp4"));
        Assert.False(ShortVideo.IsSecondShort("test-video-en.mp4"));
    }

    [Fact]
    public void ShortVideo_LongFormSlug_StripsBothSuffixes()
    {
        Assert.Equal("test-video-en", ShortVideo.LongFormSlug("test-video-en-short"));
        Assert.Equal("test-video-en", ShortVideo.LongFormSlug("test-video-en-short2"));
        Assert.Null(ShortVideo.LongFormSlug("test-video-en"));
        Assert.Null(ShortVideo.LongFormSlug("test-video-en-story60"));
    }

    [Fact]
    public void YouTubeShort2QuotaAllows_Math()
    {
        // One dispatch ≈ 1,700 units of the 10,000/day project quota, so
        // the guard allows while (today + this run + this dispatch) <= 5.
        Assert.True(QuotaAllows(0, 0));   // 1 x 1,700
        Assert.True(QuotaAllows(2, 2));   // 5 x 1,700 = 8,500 (a normal day: long + Short + this run)
        Assert.True(QuotaAllows(3, 1));   // 5 x 1,700 = 8,500
        Assert.False(QuotaAllows(3, 2));  // 6 x 1,700 = 10,200 — over
        Assert.False(QuotaAllows(5, 0));  // 6 x 1,700 = 10,200 — over
    }

    private static bool QuotaAllows(int todayRows, int runDispatches) =>
        ReflectionHelper.Invoke<bool>(ReflectionHelper.Uploader, "YouTubeShort2QuotaAllows", todayRows, runDispatches);

    [Fact]
    public async Task FindSiblingSidecar_Short2_FindsLongForm()
    {
        var dir = TempDir("sibling");
        try
        {
            // Long-form sidecar already moved to done/<slug>/, published
            // on YouTube — the state the reconciler and the footer need.
            var longFormDir = Path.Combine(dir, "done", "test-video-en");
            Directory.CreateDirectory(longFormDir);
            var longFormMp4 = Path.Combine(longFormDir, "test-video-en.mp4");
            var longForm = new Sidecar
            {
                Title = "Test Video",
                Channel = "trendlinenow",
                Aspect = "landscape",
                Publications =
                {
                    ["youtube"] = new Publication { Status = PublishStatus.Published, Id = "long123" },
                },
            };
            await longForm.SaveAsync(longFormMp4);

            // The second Short's own file in the watch dir.
            var short2Path = Path.Combine(dir, "test-video-en-short2.mp4");
            File.WriteAllText(short2Path, "");

            var sibling = await InvokeFindSiblingAsync(short2Path, wantShort: false);
            Assert.NotNull(sibling);
            Assert.Equal("long123", sibling.Value.Sidecar.Publications["youtube"].Id);
            Assert.Equal(longFormMp4, sibling.Value.VideoPath);

            // A Short (main or second) has no Short sibling to find.
            Assert.Null(await InvokeFindSiblingAsync(short2Path, wantShort: true));
            var shortPath = Path.Combine(dir, "test-video-en-short.mp4");
            File.WriteAllText(shortPath, "");
            Assert.Null(await InvokeFindSiblingAsync(shortPath, wantShort: true));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<(Sidecar Sidecar, string VideoPath)?> InvokeFindSiblingAsync(string videoPath, bool wantShort)
    {
        var method = ReflectionHelper.FindMethod(ReflectionHelper.Uploader, "FindSiblingSidecarAsync");
        var task = (Task)method.Invoke(null, [videoPath, wantShort, null])!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        return result is null ? null : ((Sidecar, string))result;
    }
}
