import React from "react";
import { Composition } from "remotion";
import { Assembly } from "./Assembly";
import { assemblyPropsSchema, type AssemblyProps } from "./schema";

const defaultProps: AssemblyProps = {
  scenes: [],
  fps: 30,
  widthPx: 1080,
  heightPx: 1920,
  backgroundMusicVolume: 0.15,
  outroSeconds: 0,
  outroLines: [],
  isCalm: false,
};

// durationInFrames/width/height/fps must be known before render starts, but they're a
// function of the scenes VideoGen passes in via --props, not fixed constants -
// calculateMetadata resolves them from the real props so there's one source of truth
// for total video length instead of VideoGen and this project each computing it.
export const RemotionRoot: React.FC = () => {
  return (
    <>
      <Composition
        id="Assembly"
        component={Assembly}
        schema={assemblyPropsSchema}
        durationInFrames={30 * 10}
        fps={30}
        width={1080}
        height={1920}
        defaultProps={defaultProps}
        calculateMetadata={async ({ props }) => {
          const totalSeconds =
            props.scenes.reduce((sum, s) => sum + s.durationInSeconds, 0) + props.outroSeconds;
          return {
            durationInFrames: Math.max(1, Math.round(totalSeconds * props.fps)),
            fps: props.fps,
            width: props.widthPx,
            height: props.heightPx,
          };
        }}
      />
    </>
  );
};
