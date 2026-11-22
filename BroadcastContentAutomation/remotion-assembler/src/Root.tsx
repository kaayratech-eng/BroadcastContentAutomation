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
  outroHandles: [],
  introSeconds: 0,
  isCalm: false,
  briefScenes: [],
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
        calculateMetadata={async ({ props }: { props: AssemblyProps }) => {
          const briefSeconds = (props.briefScenes ?? []).reduce(
            (sum, s) => sum + s.durationInSeconds, 0);
          const totalSeconds =
            briefSeconds +
            (props.introSeconds ?? 0) +
            props.scenes.reduce((sum, s) => sum + s.durationInSeconds, 0) + props.outroSeconds;
          // In a <TransitionSeries> each transition's duration is shared time: the
          // following sequence starts that many frames early, so the real content
          // ends transitionCount * transitionFrames before the nominal end. Without
          // subtracting it the composition runs that long past the outro into black
          // (9 calm transitions x 14 frames = 126 frames / 4.2s on a 9-scene video).
          // Must mirror Assembly.tsx exactly: a transition follows every brief
          // scene, the intro (when one exists) and every scene when an outro
          // exists, otherwise only between scenes; calm formats (isCalm)
          // dissolve for 0.45s, brisk ones for 0.2s.
          const transitionFrames = Math.round(props.fps * (props.isCalm ? 0.45 : 0.2));
          const transitionCount =
            (props.briefScenes ?? []).length +
            (props.introSeconds > 0 ? 1 : 0) +
            (props.outroSeconds > 0 ? props.scenes.length : Math.max(0, props.scenes.length - 1));
          return {
            durationInFrames: Math.max(
              1,
              Math.round(totalSeconds * props.fps) - transitionCount * transitionFrames,
            ),
            fps: props.fps,
            width: props.widthPx,
            height: props.heightPx,
          };
        }}
      />
    </>
  );
};
