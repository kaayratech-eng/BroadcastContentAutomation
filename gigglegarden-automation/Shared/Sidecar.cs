using System.Text.Json;
using System.Text.Json.Serialization;

namespace GiggleGarden.Shared;

public static class Platforms
{
    public const string YouTube = "youtube";
    public const string Instagram = "instagram";
    public const string Facebook = "facebook";
    public const string TikTok = "tiktok";

    public static readonly string[] All = [YouTube, Instagram, Facebook, TikTok];
}

public enum PublishStatus
{
    Pending,
    Published,
    Failed,      // retryable — will be attempted again next run
    Abandoned,   // permanently failed, gave up
    Skipped,     // not a target for this asset, or platform disabled
}

public sealed class Publication
{
    public PublishStatus Status { get; set; } = PublishStatus.Pending;
    public string? Id { get; set; }
    public string? Url { get; set; }
    public DateTimeOffset? At { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }

    [JsonIgnore]
    public bool IsTerminal => Status is PublishStatus.Published or PublishStatus.Abandoned or PublishStatus.Skipped;
}

// The sidecar is the unit of state for the whole pipeline. VideoGen writes it next to
// each render; the Uploader reads it, publishes to each target that is not yet
// terminal, and writes it back. The video file only moves to \done once every target
// has reached a terminal state — which is what makes publishing to more than one
// platform possible at all.
public sealed class Sidecar
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public bool? Approved { get; set; }

    public string Language { get; set; } = "en";
    public string Aspect { get; set; } = "landscape";      // landscape | vertical
    public List<string> Targets { get; set; } = [];
    public Dictionary<string, Publication> Publications { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Optional per-platform caption override. Absent means "derive from Description".
    public Dictionary<string, string> CaptionOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public bool IsVertical => Aspect.Equals("vertical", StringComparison.OrdinalIgnoreCase);

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string PathFor(string videoPath) => Path.ChangeExtension(videoPath, ".json");

    public static async Task<Sidecar?> LoadAsync(string videoPath)
    {
        var path = PathFor(videoPath);
        if (!File.Exists(path)) return null;

        var sidecar = JsonSerializer.Deserialize<Sidecar>(await File.ReadAllTextAsync(path), Options)
                      ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not valid sidecar JSON.");

        // Backward compatibility with v1 sidecars (title/description/tags/approved only):
        // an absent Targets list means the file predates multi-platform support, so it
        // keeps the original YouTube-only behaviour rather than silently fanning out.
        if (sidecar.Targets.Count == 0) sidecar.Targets.Add(Platforms.YouTube);

        return sidecar;
    }

    public async Task SaveAsync(string videoPath)
    {
        var path = PathFor(videoPath);
        var temp = path + ".tmp";

        // Write-then-replace: a crash mid-write must not destroy the record of what
        // has already been published, which would cause duplicate posts on the retry.
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, path, overwrite: true);
    }

    public Publication PublicationFor(string platform)
    {
        if (!Publications.TryGetValue(platform, out var publication))
            Publications[platform] = publication = new Publication();
        return publication;
    }

    public IEnumerable<string> OutstandingTargets() =>
        Targets.Where(t => !PublicationFor(t).IsTerminal);

    public bool AllTargetsTerminal() =>
        Targets.Count > 0 && Targets.All(t => PublicationFor(t).IsTerminal);

    public bool AnyPublished() =>
        Publications.Values.Any(p => p.Status == PublishStatus.Published);

    public string CaptionFor(string platform, int maxLength)
    {
        var text = CaptionOverrides.TryGetValue(platform, out var custom) && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : Description;
        return Text.Fit(text, maxLength);
    }
}
