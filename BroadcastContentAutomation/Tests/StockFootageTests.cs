// L51 — Pexels null returns must log the query, the orientation and
// the reason ("no results" vs "no file matches orientation" vs "empty
// download"), instead of returning null silently. The helper client is
// exercised behaviourally through a stub pexels_search.py; the direct
// client has no HTTP seam, so its identical branches are pinned by a
// source guard.
namespace BroadcastContentAutomation.Tests;

public class StockFootageTests
{
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
    public async Task StockFootage_NoMatchLogsQueryAndReason()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stock-nomatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stub = Path.Combine(dir, "pexels-stub.py");
            await File.WriteAllTextAsync(stub, """
                import json, sys
                cmd = sys.argv[1]
                if cmd == "search-video":
                    print(json.dumps({"videos": [], "total_results": 0}))
                else:
                    print(json.dumps({"photos": [], "total_results": 0}))
                """);
            var cfg = new GenConfig { PexelsHelperPath = stub };
            var client = new StockFootageHelperClient(cfg);

            var sb = new System.Text.StringBuilder();
            var oldOut = Console.Out;
            StockDownload? result;
            try
            {
                Console.SetOut(new StringWriter(sb));
                result = await client.DownloadBestMatchDetailedAsync(
                    "cristiano ronaldo goal", Path.Combine(dir, "scene00-stock"),
                    ImageClient.Orientation.Landscape);
            }
            finally
            {
                Console.SetOut(oldOut);
            }

            Assert.Null(result);
            var log = sb.ToString();
            Assert.Contains("cristiano ronaldo goal", log);
            Assert.Contains("no video results", log);
            Assert.Contains("Landscape", log);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StockFootageClient_NullBranchesLogQueryAndReason()
    {
        // The direct client has no HTTP seam, so pin its log lines by
        // source: every null branch must name the query, orientation
        // and reason, mirroring the helper client.
        var source = File.ReadAllText(Path.Combine(RepoRoot, "BroadcastContentAutomation", "VideoGen", "StockFootageClient.cs"));
        Assert.Contains("no video results", source);
        Assert.Contains("no file matches orientation", source);
        Assert.Contains("no photo results", source);
        Assert.Contains("empty download", source);
        Assert.Contains("Pexels {query} {orientation}", source);
    }
}
