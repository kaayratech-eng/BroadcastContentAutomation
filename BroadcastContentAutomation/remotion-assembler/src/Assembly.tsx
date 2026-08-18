import React from "react";
import {
  AbsoluteFill,
  Audio,
  Img,
  Sequence,
  Video,
  interpolate,
  staticFile,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";
import type { AssemblyProps, SceneAsset } from "./schema";

// Ken-Burns style slow pan/zoom for stills - the motion Vidu used to provide for every
// scene, now needed for character portraits and stock photos, neither of which has any
// motion of their own. Real Pexels video clips already move, so MotionVideo below only
// applies a gentle zoom, not a translate, to avoid fighting the source camera motion.
const KenBurnsImage: React.FC<{ asset: SceneAsset; durationInFrames: number }> = ({
  asset,
  durationInFrames,
}) => {
  const frame = useCurrentFrame();
  const zoomRange: [number, number] = asset.motionIntensity === "subtle" ? [1, 1.06] : [1, 1.12];
  const scale = interpolate(frame, [0, durationInFrames], zoomRange, {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const translateRange = asset.motionIntensity === "subtle" ? 0 : 15;
  const translateX = interpolate(frame, [0, durationInFrames], [0, -translateRange], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });

  return (
    <AbsoluteFill style={{ overflow: "hidden" }}>
      <Img
        src={staticFile(asset.path)}
        style={{
          width: "100%",
          height: "100%",
          objectFit: "cover",
          transform: `scale(${scale}) translateX(${translateX}px)`,
        }}
      />
    </AbsoluteFill>
  );
};

const MotionVideo: React.FC<{ asset: SceneAsset; durationInFrames: number }> = ({
  asset,
  durationInFrames,
}) => {
  const frame = useCurrentFrame();
  const scale = interpolate(frame, [0, durationInFrames], [1, 1.04], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });

  return (
    <AbsoluteFill style={{ overflow: "hidden" }}>
      <Video
        src={staticFile(asset.path)}
        muted
        style={{ width: "100%", height: "100%", objectFit: "cover", transform: `scale(${scale})` }}
      />
    </AbsoluteFill>
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
}) => {
  const { fps } = useVideoConfig();
  let startFrame = 0;

  return (
    <AbsoluteFill>
      {backgroundMusicPath ? (
        <Audio src={staticFile(backgroundMusicPath)} volume={backgroundMusicVolume} loop />
      ) : null}
      {scenes.map((asset, i) => {
        const durationInFrames = Math.round(asset.durationInSeconds * fps);
        const from = startFrame;
        startFrame += durationInFrames;
        return (
          <Sequence key={i} from={from} durationInFrames={durationInFrames}>
            <Scene asset={asset} durationInFrames={durationInFrames} />
          </Sequence>
        );
      })}
    </AbsoluteFill>
  );
};
