import { z } from "zod";

// One data visual for a "visual" scene (Trendline Now): a graph, a table, or a
// big on-screen text callout, drawn natively by Assembly.tsx's VisualScene
// component from this data - never generated as an image, so the numbers stay
// sharp and exactly as the script states them. Mirrors the C# VisualSpec
// record (VideoGen/ScriptGenerator.cs); the props JSON is camelCased by
// VideoAssembler, so the field names here match that casing exactly. The
// numbers must come from the news/trend facts the script was built from -
// never invented (see the data-honesty rule in TrendlineNowProfile).
export const visualSpecSchema = z.object({
  kind: z.enum(["line", "bar", "table", "callout"]),
  title: z.string().optional(),
  labels: z.array(z.string()).optional(),
  values: z.array(z.number()).optional(),
  unit: z.string().optional(),
  headers: z.array(z.string()).optional(),
  rows: z.array(z.array(z.string())).optional(),
  text: z.string().optional(),
  subtext: z.string().optional(),
  source: z.string().optional(),
  accent: z.string().optional(),
});

// One scene's visual asset - either a manually-generated character portrait (the
// Gemini prompt-and-pickup loop) or an auto-fetched Pexels clip/photo (StockFootageClient,
// ../VideoGen/StockFootageClient.cs). Remotion treats both the same way once they arrive
// here; the character-moment/context-moment distinction only matters upstream, for
// picking which source produced `path`.
export const sceneAssetSchema = z.object({
  kind: z.enum(["image", "video", "visual"]),
  path: z.string(),
  durationInSeconds: z.number().positive(),
  narrationAudioPath: z.string().optional(),
  captionText: z.string().optional(),
  // Context/b-roll scenes read better with a slower pan than the wider push used for a
  // character moment showing off a fresh portrait.
  motionIntensity: z.enum(["subtle", "normal"]).default("normal"),
  // A video clip's real length in seconds, when kind is "video". A Pexels clip can be
  // shorter than its scene (scene = narration + 0.5s), and asking the <Video> element
  // for a frame at/past the clip's end never becomes ready - that hung the 2026-11-11
  // test render on a delayRender timeout. Assembly.tsx trims the video a couple of
  // frames short of this length instead. Absent for images and for old props files.
  videoDurationInSeconds: z.number().positive().optional(),
  // The data visual for kind === "visual" (Trendline Now only). Absent for
  // image/video scenes; path is an empty string for visual scenes since there
  // is no file to load.
  visualSpec: visualSpecSchema.optional(),
  // True when the scene's narration is commentary/speculation rather than
  // reported fact (Trendline Now only; set by ScriptGenerator's safety
  // framing + NormalizeNewsScenes safety net). Assembly.tsx renders a
  // SPECULATION badge for these scenes. Optional so old props files still
  // validate; absent means factual.
  isSpeculative: z.boolean().optional(),
});

export const assemblyPropsSchema = z.object({
  scenes: z.array(sceneAssetSchema).min(1),
  fps: z.number().int().positive().default(30),
  widthPx: z.number().int().positive(),
  heightPx: z.number().int().positive(),
  backgroundMusicPath: z.string().optional(),
  backgroundMusicVolume: z.number().min(0).max(1).default(0.15),
  // Extra card appended after the last scene - e.g. a "LIKE, FOLLOW & SUBSCRIBE" card, with
  // the vertical short additionally folding in a "watch the full video" line. 0 (default)
  // renders nothing extra.
  outroSeconds: z.number().min(0).default(0),
  outroLines: z.array(z.string()).default([]),
  outroAudioPath: z.string().optional(),
  // Platform chips shown on the branded outro card (e.g. YouTube, Instagram,
  // TikTok, Facebook) - all pointing at the channel handle. Empty (default)
  // renders no chips, so the legacy text-only card is unchanged.
  outroHandles: z.array(z.string()).default([]),
  // The channel handle shown big on the branded outro card (e.g.
  // "@trendlinenow"). Only rendered when outroHandles is non-empty, so the
  // legacy card never shows the destination twice.
  outroHandle: z.string().optional(),
  // Animated intro sting played before the first scene (Trendline Now): a
  // brand moment built from the channel logo - see Assembly.tsx's Intro.
  // 0 (default) renders no intro. introLogoPath is relative to the
  // --public-dir root, like every other asset path in these props.
  introSeconds: z.number().min(0).default(0),
  introLogoPath: z.string().optional(),
  // Curiosity brief (Rishi 2026-11-21, video #3 onward): teaser scenes that
  // play BEFORE the intro sting, over the video's most dramatic images, to
  // make viewers hold and watch the full video. Empty (default) renders no
  // brief, so old props files still validate.
  briefScenes: z.array(sceneAssetSchema).default([]),
  // Drives scene-transition style/duration - calm formats (retellingArc) get a slower
  // dissolve, brisk formats (topFive) get a quick one. See ContentFormatDef.IsCalm.
  isCalm: z.boolean().default(false),
});

export type SceneAsset = z.infer<typeof sceneAssetSchema>;
export type AssemblyProps = z.infer<typeof assemblyPropsSchema>;
