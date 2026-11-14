import React from "react";
import {
  AbsoluteFill,
  Audio,
  Img,
  Video,
  interpolate,
  staticFile,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";
import { TransitionSeries, linearTiming } from "@remotion/transitions";
import { fade } from "@remotion/transitions/fade";
import type { AssemblyProps, SceneAsset } from "./schema";

// Every scene asset is resolved once (see Program.cs) and reused for both the 9:16 short
// and the 16:9 landscape master - so a portrait character still is inevitably rendered into
// a landscape frame at some point. A plain objectFit:"cover" in that case forces a ~2.5x
// blow-up that crops most of the subject out (e.g. a full-body portrait loses the head and
// most of the lower robe, leaving only a torso-width band). Both layers below fix that:
// a blurred cover-fill behind a contain-fit copy of the same asset, so the frame is always
// full-bleed but the subject itself is never cropped, regardless of which orientation it's
// rendered into. When the asset's aspect ratio already matches the frame (the short's own
// case), the contain layer covers the frame edge-to-edge and the blur layer never shows.
const BlurPadBackground: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <AbsoluteFill style={{ overflow: "hidden" }}>{children}</AbsoluteFill>
);

// Ken-Burns style slow pan/zoom for stills - the motion Vidu used to provide for every
// scene, now needed for character portraits and stock photos, neither of which has any
// motion of their own. Real Pexels video clips already move, so MotionVideo below only
// applies a gentle zoom, not a translate, to avoid fighting the source camera motion.
const KenBurnsImage: React.FC<{ asset: SceneAsset; durationInFrames: number }> = ({
  asset,
  durationInFrames,
}) => {
  const frame = useCurrentFrame();
  // Pacing (2026-11-12, standing rule): slower zoom than before - the videos felt
  // too fast. The zoom still spans the whole scene, so the smaller range is the
  // slowdown (and scenes are longer now, which stretches it further).
  const zoomRange: [number, number] = asset.motionIntensity === "subtle" ? [1, 1.03] : [1, 1.06];
  const scale = interpolate(frame, [0, durationInFrames], zoomRange, {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const translateRange = asset.motionIntensity === "subtle" ? 0 : 8;
  const translateX = interpolate(frame, [0, durationInFrames], [0, -translateRange], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });

  return (
    <BlurPadBackground>
      <Img
        src={staticFile(asset.path)}
        style={{
          width: "100%",
          height: "100%",
          objectFit: "cover",
          filter: "blur(60px) brightness(0.55)",
          transform: "scale(1.15)",
        }}
      />
      <Img
        src={staticFile(asset.path)}
        style={{
          position: "absolute",
          inset: 0,
          width: "100%",
          height: "100%",
          objectFit: "contain",
          transform: `scale(${scale}) translateX(${translateX}px)`,
          // Zoom anchors to the centre: the sharp layer is contain-fit
          // (see above), so the whole figure is visible with headroom
          // and a centre zoom trims top and bottom symmetrically by ~3%
          // instead of pushing the bottom out. (The old top anchor made
          // sense for the pre-cropped cover path, where a head tight to
          // the top of the frame got pushed out by a centre zoom — that
          // path is gone since 2026-11-13, when the landscape asset
          // became the square itself.)
          transformOrigin: "50% 50%",
        }}
      />
    </BlurPadBackground>
  );
};

const MotionVideo: React.FC<{ asset: SceneAsset; durationInFrames: number }> = ({
  asset,
  durationInFrames,
}) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  // Pacing (2026-11-12): gentler zoom for stock clips too (was 1.04).
  const scale = interpolate(frame, [0, durationInFrames], [1, 1.02], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });

  // A stock clip can be shorter than its scene (scene = narration + 0.5s), and a
  // <Video> asked for a frame at/past the clip's exact end never becomes ready -
  // the 2026-11-11 test render died this way at scene 2 (16.56s scene, 16.23s
  // clip: the element was asked for time 16.2333s and hung past Remotion's 28s
  // delayRender window). trimAfter (Remotion 4's name for endAt) wraps the video
  // in an inner Sequence that ends at that frame, so the element is never asked
  // for a time past it; the narration and caption keep playing over the last
  // frame for the remaining tail. Only applied when the clip would otherwise
  // run out mid-scene - a clip longer than its scene renders untouched, exactly
  // as before. Two frames of margin keep float rounding from landing on the end.
  const clipFrames =
    asset.videoDurationInSeconds !== undefined
      ? Math.floor(asset.videoDurationInSeconds * fps)
      : undefined;
  const trimAfter =
    clipFrames !== undefined && clipFrames - 2 < durationInFrames
      ? Math.max(1, clipFrames - 2)
      : undefined;

  // The sharp layer is a cover crop, not contain: since 2026-11-11 each
  // context scene downloads ONE clip (landscape-first), and the same clip
  // must fill both renders. Cover keeps the clip's full height in the
  // portrait render (cutting only the sides) and uses it as-is in
  // landscape. objectPosition anchors the crop window to the top, so if
  // the single source ever is a portrait clip, the landscape render crops
  // its bottom, never its top — heads stay in frame. The zoom is anchored
  // to the top for the same reason. The blurred cover layer behind is
  // fully hidden by the sharp cover layer; it stays as a safety net for
  // any frame the sharp layer does not cover.
  return (
    <BlurPadBackground>
      <Video
        src={staticFile(asset.path)}
        muted
        trimAfter={trimAfter}
        style={{
          width: "100%",
          height: "100%",
          objectFit: "cover",
          filter: "blur(60px) brightness(0.55)",
          transform: "scale(1.15)",
        }}
      />
      <Video
        src={staticFile(asset.path)}
        muted
        trimAfter={trimAfter}
        style={{
          position: "absolute",
          inset: 0,
          width: "100%",
          height: "100%",
          objectFit: "cover",
          objectPosition: "50% 0",
          transform: `scale(${scale})`,
          transformOrigin: "50% 0",
        }}
      />
    </BlurPadBackground>
  );
};

const Caption: React.FC<{ text: string }> = ({ text }) => (
  <AbsoluteFill style={{ justifyContent: "flex-end", alignItems: "center", paddingBottom: "8%" }}>
    <div
      style={{
        fontFamily: "sans-serif",
        fontSize: 44,
        fontWeight: 700,
        color: "white",
        textAlign: "center",
        textShadow: "0 2px 12px rgba(0,0,0,0.85)",
        maxWidth: "85%",
        lineHeight: 1.25,
      }}
    >
      {text}
    </div>
  </AbsoluteFill>
);

// "LIKE, FOLLOW & SUBSCRIBE" card appended after the last scene (see Program.cs's
// baseOutroLines/shortOutroLines) - the vertical short additionally folds in a "watch the
// full video" line, since that render is a trimmed teaser rather than the full video
// itself. First line renders as the header; every line after it as a smaller sub-line.
const Outro: React.FC<{ lines: string[]; audioPath?: string }> = ({ lines, audioPath }) => {
  const frame = useCurrentFrame();
  const opacity = interpolate(frame, [0, 15], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });

  return (
    <AbsoluteFill
      style={{
        backgroundColor: "black",
        justifyContent: "center",
        alignItems: "center",
        opacity,
      }}
    >
      {audioPath ? <Audio src={staticFile(audioPath)} /> : null}
      <div
        style={{
          fontFamily: "sans-serif",
          textAlign: "center",
          color: "white",
        }}
      >
        {lines.map((line, i) => (
          <div
            key={i}
            style={{
              fontSize: i === 0 ? 52 : 38,
              fontWeight: i === 0 ? 700 : 500,
              marginTop: i === 0 ? 0 : 16,
              opacity: i === 0 ? 1 : 0.85,
            }}
          >
            {line}
          </div>
        ))}
      </div>
    </AbsoluteFill>
  );
};

const Scene: React.FC<{ asset: SceneAsset; durationInFrames: number }> = ({
  asset,
  durationInFrames,
}) => (
  <AbsoluteFill style={{ backgroundColor: "black" }}>
    {asset.kind === "video" ? (
      <MotionVideo asset={asset} durationInFrames={durationInFrames} />
    ) : (
      <KenBurnsImage asset={asset} durationInFrames={durationInFrames} />
    )}
    {asset.narrationAudioPath ? <Audio src={staticFile(asset.narrationAudioPath)} /> : null}
    {asset.captionText ? <Caption text={asset.captionText} /> : null}
  </AbsoluteFill>
);

export const Assembly: React.FC<AssemblyProps> = ({
  scenes,
  backgroundMusicPath,
  backgroundMusicVolume,
  outroSeconds,
  outroLines,
  outroAudioPath,
  isCalm,
}) => {
  const { fps } = useVideoConfig();

  // Every scene carries a 0.5s silent tail after its narration ends (see RemotionSceneAsset
  // in VideoAssembler.cs: DurationInSeconds is the audio length plus a fixed 0.5s buffer), so
  // keeping the transition under that avoids two scenes' narration overlapping mid-dissolve.
  // Calm formats (retellingArc) get a slow dissolve; brisk ones (topFive) get a quick blend
  // that still smooths the cut without dulling the pace.
  const transitionTiming = linearTiming({
    durationInFrames: Math.round(fps * (isCalm ? 0.45 : 0.2)),
  });

  return (
    <AbsoluteFill>
      {backgroundMusicPath ? (
        <Audio src={staticFile(backgroundMusicPath)} volume={backgroundMusicVolume} loop />
      ) : null}
      <TransitionSeries>
        {scenes.flatMap((asset, i) => {
          const durationInFrames = Math.round(asset.durationInSeconds * fps);
          const elements = [
            <TransitionSeries.Sequence key={`scene-${i}`} durationInFrames={durationInFrames}>
              <Scene asset={asset} durationInFrames={durationInFrames} />
            </TransitionSeries.Sequence>,
          ];
          if (i < scenes.length - 1 || outroSeconds > 0) {
            elements.push(
              <TransitionSeries.Transition
                key={`transition-${i}`}
                presentation={fade()}
                timing={transitionTiming}
              />
            );
          }
          return elements;
        })}
        {outroSeconds > 0 ? (
          <TransitionSeries.Sequence durationInFrames={Math.round(outroSeconds * fps)}>
            <Outro lines={outroLines} audioPath={outroAudioPath} />
          </TransitionSeries.Sequence>
        ) : null}
      </TransitionSeries>
    </AbsoluteFill>
  );
};
