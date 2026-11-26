// Topic-aware styling for Spotlight Now (2026-11-21, Rishi; pivoted to
// entertainment 2026-11-24): the video's style depends on the topic. A gaming story gets a completely different look, tone and
// pace from a finance story, and politics gets yet another. The style lives in this
// data table — not hardcoded per video — so a new category is one table entry, not
// a code change. TrendlineNowProfile wires CategoryStyles = TopicCategoryStyles.All;
// ScriptGenerator injects the resolved category's block into the news prompt, and
// Program.cs uses the category's PacingRate/PacingPitch for TTS.
//
// Classification is keyword scoring on the trend title + top news headline
// (TrendResearch), deterministic and testable. The model is told the category and
// must use it; it does not re-classify, so the style a video gets is reproducible
// from its topic alone.
using System.Text.RegularExpressions;
sealed record TopicStyleProfile(
    string Id,
    string Label,
    string[] Keywords,
    // How the narration should sound for this category - injected into the script
    // prompt's tone block (e.g. "energetic, younger, hype-aware" vs "measured,
    // professional, numbers-first").
    string ToneGuidance,
    // Palette words injected into every scene-art prompt for this category.
    string ColourPalette,
    // What the art should show for this category (game art vs. stock charts vs.
    // newsroom b-roll).
    string ArtStyleGuidance,
    // TTS pacing for this category - Program.cs's TTS loops use these instead of
    // the format's Rate/Pitch when a category is resolved (DavisNeural stays the
    // voice throughout; only the delivery speed/energy shifts).
    string PacingRate,
    string PacingPitch,
    // Which data visuals this category leans on (finance: charts; politics:
    // timelines/maps; gaming: visuals over charts). Injected into the prompt's
    // visual rules so the model requests the right scene kinds.
    string VisualEmphasis);

static class TopicCategoryStyles
{
    public static readonly IReadOnlyDictionary<string, TopicStyleProfile> All =
        new Dictionary<string, TopicStyleProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["gaming"] = new(
                Id: "gaming",
                Label: "Gaming",
                Keywords: ["game", "gaming", "gta", "xbox", "playstation", "nintendo", "esports",
                    "fortnite", "minecraft", "steam", "rpg", "shooter", "gameplay", "trailer",
                    "patch", "dlc", "sony", "activision", "blizzard", "pokemon", "zelda", "mario"],
                ToneGuidance: "Energetic, hype-aware, younger - talk like someone who actually " +
                    "plays. Short punchy sentences, gaming slang where it lands naturally, " +
                    "genuine excitement about the reveal. Never dry, never corporate.",
                ColourPalette: "dark background with electric neon accents - cyan, magenta, " +
                    "lime - high contrast, arcade energy",
                ArtStyleGuidance: "game key-art energy: dramatic game-world scenes, bold " +
                    "character silhouettes, glowing UI-like elements, cinematic game-trailer " +
                    "framing. Prefer striking visuals over charts - a graph only earns a " +
                    "scene if the story has a real number (sales, player count, review score).",
                PacingRate: "+10%",
                PacingPitch: "+2%",
                VisualEmphasis: "Visuals first: bold on-screen text callouts and big numbers. " +
                    "Charts only for real figures the news actually reports (sales, player " +
                    "counts, scores) - never a chart for its own sake."),

            ["finance"] = new(
                Id: "finance",
                Label: "Finance",
                Keywords: ["stock", "market", "shares", "bank", "ipo", "crypto", "bitcoin",
                    "inflation", "interest rate", "federal reserve", "fed", "earnings",
                    "revenue", "recession", "dollar", "gold", "trading", "investor",
                    "unemployment", "gdp", "tariff", "nasdaq", "sensex", "nifty"],
                ToneGuidance: "Clean, professional, numbers-first - a sharp business explainer. " +
                    "State the number, then say what it means in plain language. Confident " +
                    "and precise, not breathless. Analogies that make money concrete.",
                ColourPalette: "clean muted blues and greens on a dark navy base, restrained " +
                    "and professional, white type",
                ArtStyleGuidance: "clean editorial business visuals: trading floors, city " +
                    "skylines, charts on screens, money and market imagery. Charts and " +
                    "tables are the stars here, not decoration.",
                PacingRate: "+10%",
                PacingPitch: "-2%",
                VisualEmphasis: "Charts and tables FIRST: price/index trend lines, earnings " +
                    "figures, comparison tables. Every claim with a number gets a visual " +
                    "showing that number - a finance video without visible data is a failed " +
                    "finance video."),

            ["politics"] = new(
                Id: "politics",
                Label: "Politics",
                // Global topic strategy (2026-11-22): the politics demotion
                // depends on Classify returning "politics", and headlines
                // say "Democrats"/"Republicans" (plural) far more than the
                // singular. Classify matches whole words, so the singular
                // never matches the plural — the dropped Democrats story
                // classified as "general" and escaped demotion. The plurals
                // are unambiguous politics words; adding them is safe.
                Keywords: ["election", "president", "prime minister", "parliament", "congress",
                    "senate", "minister", "vote", "campaign", "policy", "summit", "treaty",
                    "government", "law", "court", "supreme", "democrat", "democrats",
                    "republican", "republicans",
                    "biden", "trump", "modi", "putin", "war", "ceasefire", "sanctions",
                    "white house", "army", "military"],
                ToneGuidance: "Serious, measured, newsroom - a trusted anchor, not a pundit. " +
                    "Present what happened and what each side says, in plain language. No " +
                    "partisan spin, no snark. The drama comes from the stakes, not the tone.",
                ColourPalette: "serious newsroom palette - deep navy, restrained red and " +
                    "white, sober and high-contrast",
                ArtStyleGuidance: "newsroom documentary visuals: government buildings, " +
                    "capitals, press conferences, crowds, maps. Timelines and quoted lines " +
                    "as on-screen callouts.",
                PacingRate: "+10%",
                PacingPitch: "-4%",
                VisualEmphasis: "Timelines and maps first: a timeline of how events unfolded, " +
                    "a map when geography matters, and direct quotes as big text callouts. " +
                    "Charts only when the story has real polling or vote numbers."),

            ["sports"] = new(
                Id: "sports",
                Label: "Sports",
                Keywords: ["football", "soccer", "cricket", "nba", "nfl", "olympics", "world cup",
                    "match", "championship", "player", "goal", "tennis", "formula 1", "f1",
                    "grand prix", "boxing", "ufc", "hockey", "baseball", "athlete", "tournament"],
                ToneGuidance: "Dynamic and punchy, like a highlights show - energy up, " +
                    "sentences short, the big moment landed hard. Celebrate the drama of " +
                    "the game without slipping into a shouty sports-radio rant.",
                ColourPalette: "bold stadium energy - deep green or dark navy with bright " +
                    "white and a hot accent (red or orange), floodlit and vivid",
                ArtStyleGuidance: "dynamic sports visuals: action shots, stadiums under " +
                    "lights, athletes in motion, roaring crowds. Score and stat tables " +
                    "as clean overlays.",
                PacingRate: "+10%",
                PacingPitch: "+2%",
                VisualEmphasis: "Score tables and stat callouts: the scoreline, the key stats, " +
                    "the record broken - shown as bold on-screen numbers. A line chart " +
                    "only for a real trend (form over a season, a record chase)."),

            ["tech"] = new(
                Id: "tech",
                Label: "Tech",
                Keywords: ["ai", "artificial intelligence", "apple", "google", "microsoft",
                    "iphone", "android", "chip", "semiconductor", "openai", "robot", "spacex",
                    "tesla", "software", "app", "launch", "gadget", "smartphone", "chatgpt",
                    "nvidia", "meta", "amazon", "startup", "cyber", "cybersecurity"],
                ToneGuidance: "Sleek and curious, like a great product launch - clear, " +
                    "forward-looking, genuinely interested in how the thing works. Explain " +
                    "the tech in one vivid analogy instead of a spec sheet.",
                ColourPalette: "sleek modern palette - dark charcoal with electric blue and " +
                    "white, minimal and futuristic",
                ArtStyleGuidance: "sleek product visuals: devices, labs, clean futuristic " +
                    "spaces, glowing interfaces. Spec tables and clean diagrams as " +
                    "on-screen visuals.",
                PacingRate: "+10%",
                PacingPitch: "0%",
                VisualEmphasis: "Spec tables and clean comparison visuals: what changed, " +
                    "what it costs, how it stacks up - shown as a tidy table. A chart " +
                    "only for a real adoption or performance number."),

            ["entertainment"] = new(
                Id: "entertainment",
                Label: "Entertainment",
                Keywords: ["movie", "film", "celebrity", "actor", "actress", "music", "album",
                    "concert", "netflix", "trailer", "awards", "oscar", "grammy", "bollywood",
                    "hollywood", "k-pop", "show", "series", "singer", "rapper", "comedian", "box office",
                    "streaming", "taylor swift", "marvel", "premiere", "red carpet", "blockbuster",
                    "comeback", "debut", "ott", "web series", "filmfare", "cannes", "met gala",
                    "reality show", "star", "superstar", "fame", "famous"],
                ToneGuidance: "Warm, admiring fan energy - like a fan telling a friend why " +
                    "this star is amazing. Celebrate the journey, the hard work, the " +
                    "breakthrough. 'Did-you-see-that' excitement, genuine and upbeat. " +
                    "Never gossipy or mean about real people - praise, don't tear down.",
                ColourPalette: "vibrant and warm - rich purples, hot pinks and golds on a " +
                    "dark base, red-carpet energy",
                ArtStyleGuidance: "vibrant entertainment visuals: premieres, stages, " +
                    "concert lights, film-set drama. Bold text callouts for the big " +
                    "reveal (a release date, a record, a casting).",
                PacingRate: "+10%",
                PacingPitch: "+2%",
                VisualEmphasis: "Bold text callouts for the big beats: the date, the record, " +
                    "the number everyone is talking about. Tables only for a real list " +
                    "(chart positions, box-office figures)."),

            // SPOTLIGHT NOW pivot (2026-11-24): singers are a core beat, not a
            // sub-keyword of entertainment - their own style (stage, tour,
            // album, the voice) earns its own entry.
            ["music"] = new(
                Id: "music",
                Label: "Music",
                Keywords: ["song", "single", "tour", "billboard", "spotify", "playback",
                    "vocalist", "band", "lyrics", "music video", "chart-topper",
                    "chart topper", "grammy winner", "album launch", "world tour",
                    "concert tour", "hit song", "new song"],
                ToneGuidance: "Celebrate the artist and the song - the voice, the grind " +
                    "behind the hit, the crowd singing along. Warm, musical, a little " +
                    "awestruck. The song is the hero; the numbers (streams, charts) " +
                    "prove it. Never mean about real people.",
                ColourPalette: "stage-light energy - deep purple and gold on a dark base, " +
                    "concert glow, warm and vivid",
                ArtStyleGuidance: "concert and studio visuals: stages under lights, " +
                    "microphones, crowds with hands up, recording studios. Bold " +
                    "callouts for chart positions and stream counts.",
                PacingRate: "+10%",
                PacingPitch: "+2%",
                VisualEmphasis: "Bold text callouts for the big beats: the chart position, " +
                    "the stream count, the record. Tables only for a real list " +
                    "(chart positions, tour dates)."),

            // SPOTLIGHT NOW pivot (2026-11-24): fashion is a core beat - red
            // carpet looks, fashion week, the designer behind the moment.
            ["fashion"] = new(
                Id: "fashion",
                Label: "Fashion",
                Keywords: ["fashion", "style", "gown", "outfit", "look", "fashion week",
                    "runway", "designer", "vogue", "elle", "glamour", "couture",
                    "red carpet look", "best dressed", "stylist"],
                ToneGuidance: "Glamorous and admiring - like a front-row friend describing " +
                    "the look that stopped the room. Celebrate the design, the " +
                    "confidence, the moment. Warm and vivid, never snarky about " +
                    "real people.",
                ColourPalette: "runway glamour - black and gold on a dark base, rich " +
                    "and elegant, high contrast",
                ArtStyleGuidance: "fashion visuals: runways, red carpets, close-ups of " +
                    "the outfit, designers at work. Bold callouts for the designer " +
                    "and the moment.",
                PacingRate: "+10%",
                PacingPitch: "+2%",
                VisualEmphasis: "Bold text callouts for the big beats: the designer, the " +
                    "event, the look everyone is talking about. No charts - fashion " +
                    "is visual, not numeric."),

            ["general"] = new(
                Id: "general",
                Label: "General news",
                Keywords: [],
                ToneGuidance: "Clear, curious, and direct - a great explainer talking to a " +
                    "smart friend. The story's own drama carries the energy; the telling " +
                    "stays tight and human.",
                ColourPalette: "the channel's house palette - dark navy with a red rising-trend " +
                    "accent, white type",
                ArtStyleGuidance: "bold editorial news visuals: the real place, the real " +
                    "people, the real moment - cinematic and human, never generic stock " +
                    "cliché.",
                PacingRate: "+10%",
                PacingPitch: "-4%",
                VisualEmphasis: "A balanced mix: a chart when the story has a real trend, a " +
                    "table for key figures, a timeline when the sequence matters, and a " +
                    "bold callout for the one line nobody should miss."),
        };

    // Keyword scoring over the trend title + top news headline. Highest score wins;
    // ties go to the first category in table order (gaming before finance before
    // politics...); no hits at all falls back to "general". Substring matching on
    // lowercased text, so "GTA 6" hits the "gta" keyword and "interest rates" hits
    // "interest rate".
    public static string Classify(string topicText)
    {
        if (string.IsNullOrWhiteSpace(topicText)) return "general";
        var text = topicText.ToLowerInvariant();
        var best = "general";
        var bestScore = 0;
        foreach (var (id, style) in All)
        {
            if (id == "general") continue;
            var score = 0;
            foreach (var keyword in style.Keywords)
                // Whole-word/phrase match, not substring: Contains() misfires on
                // short keywords - "ai" matched inside "Cairncross" and classified
                // a hippo obituary + a White House story as tech (2026-11-21).
                if (Regex.IsMatch(text, $@"\b{Regex.Escape(keyword)}\b"))
                    score++;
            if (score > bestScore)
            {
                bestScore = score;
                best = id;
            }
        }
        return best;
    }

    public static TopicStyleProfile Get(string category) =>
        All.TryGetValue(category, out var style) ? style : All["general"];
}
