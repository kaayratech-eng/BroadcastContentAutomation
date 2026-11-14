import { z } from "zod";

// One scene's visual asset - either a manually-generated character portrait (the
// Gemini prompt-and-pickup loop) or an auto-fetched Pexels clip/photo (StockFootageClient,
// ../VideoGen/StockFootageClient.cs). Remotion treats both the same way once they arrive
// here; the character-moment/context-moment distinction only matters upstream, for
// picking which source produced `path`.
export const sceneAssetSchema = z.object({
  kind: z.enum(["image", "video"]),
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
  // Drives scene-transition style/duration - calm formats (retellingArc) get a slower
  // dissolve, brisk formats (topFive) get a quick one. See ContentFormatDef.IsCalm.
  isCalm: z.boolean().default(false),
});

export type SceneAsset = z.infer<typeof sceneAssetSchema>;
export type AssemblyProps = z.infer<typeof assemblyPropsSchema>;
