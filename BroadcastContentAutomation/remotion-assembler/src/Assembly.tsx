import React from "react";
import {
  AbsoluteFill,
  Audio,
  Img,
  Video,
  interpolate,
  spring,
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

const Caption: React.FC<{ text: string; durationInFrames: number; hook?: boolean }> = ({
  text,
  durationInFrames,
  hook,
}) => {
  const frame = useCurrentFrame();
  const { fps, width } = useVideoConfig();
  // Hook title (first scene only): the script's hook-first rule puts the
  // single most shocking true fact in the first sentence, so the big title
  // shows exactly that sentence - never a truncation mid-clause. The full
  // narration still plays in audio; the on-screen title is the hook line.
  const displayText = hook ? text.match(/^[^.!?]+[.!?]/)?.[0] ?? text : text;
  const words = displayText.split(/\s+/).filter(Boolean);
  // Word-by-word reveal (2026-11-25, eye-catching overhaul): the caption is
  // the narration, so it reveals word by word at the narration's average
  // pace (~170 wpm) instead of appearing as a static block - the eye
  // follows the words and the frame never sits still. True per-word timings
  // don't exist in the props (CaptionText is the plain narration string),
  // so words are spread evenly across the narration window: the voice ends
  // 0.75s before the scene does (0.5s tail + 0.25s hold, see
  // VideoAssembler), so the last word lands as the voice stops. Drift vs
  // the real TTS is bounded - steady-pace TTS averages out - and each word
  // pops in over 3 frames, so a small offset reads as karaoke, not lag.
  // The hook title reveals faster (first ~2s) since it is the opening punch.
  const revealStart = hook ? 6 : 4;
  const revealEnd = hook
    ? Math.min(66, durationInFrames - 6)
    : Math.max(revealStart + 1, durationInFrames - Math.round(0.75 * fps));
  const revealSpan = Math.max(1, revealEnd - revealStart);

  // Whole-caption entrance (slide up + fade) and exit fade into the dissolve.
  const enter = interpolate(frame, [0, 8], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const enterY = interpolate(frame, [0, 8], [14, 0], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const exit = interpolate(frame, [durationInFrames - 10, durationInFrames], [1, 0], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });

  const wordStyle = (i: number): React.CSSProperties => {
    const at = revealStart + (i / Math.max(1, words.length)) * revealSpan;
    const o = interpolate(frame, [at, at + 3], [0, 1], {
      extrapolateLeft: "clamp",
      extrapolateRight: "clamp",
    });
    const y = interpolate(frame, [at, at + 3], [6, 0], {
      extrapolateLeft: "clamp",
      extrapolateRight: "clamp",
    });
    return { display: "inline-block", opacity: o, transform: `translateY(${y}px)` };
  };

  const renderedWords = words.map((w, i) => (
    <React.Fragment key={i}>
      <span style={wordStyle(i)}>{w}</span>
      {i < words.length - 1 ? " " : null}
    </React.Fragment>
  ));

  if (hook) {
    // Hook title card: large centred type with a red accent bar growing
    // above it - the first 5 seconds read as a title, not a caption.
    const barScaleX = interpolate(frame, [0, 25], [0, 1], {
      extrapolateLeft: "clamp",
      extrapolateRight: "clamp",
    });
    return (
      <AbsoluteFill
        style={{
          justifyContent: "center",
          alignItems: "center",
          paddingBottom: "6%",
          opacity: enter * exit,
          transform: `translateY(${enterY}px)`,
        }}
      >
        <div
          style={{
            width: Math.round(width * 0.18),
            height: Math.max(6, Math.round(width * 0.008)),
            backgroundColor: "#ef4444",
            marginBottom: Math.round(width * 0.03),
            transform: `scaleX(${barScaleX})`,
          }}
        />
        <div
          style={{
            fontFamily: "sans-serif",
            fontSize: Math.round(width * 0.042),
            fontWeight: 800,
            color: "white",
            textAlign: "center",
            textShadow: "0 2px 16px rgba(0,0,0,0.9)",
            maxWidth: "88%",
            lineHeight: 1.3,
          }}
        >
          {renderedWords}
        </div>
      </AbsoluteFill>
    );
  }

  return (
    <AbsoluteFill
      style={{
        justifyContent: "flex-end",
        alignItems: "center",
        paddingBottom: "8%",
        opacity: enter * exit,
        transform: `translateY(${enterY}px)`,
      }}
    >
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
        {renderedWords}
      </div>
    </AbsoluteFill>
  );
};

// Data visual for a "visual" scene (Trendline Now only): a graph, a table, or a
// big on-screen text callout, drawn natively from asset.visualSpec - never an
// image, so the numbers stay sharp. The channel's house look (dark navy, red
// accent, white type) is the base; the accent colour comes from the video's
// topic category (finance green, gaming cyan, politics red, etc.). Content is
// kept in the top ~75% of the frame: the narration caption still renders at
// the bottom, so the visual must not run into it. All sizes scale with the
// frame width so the same component works for both the 9:16 short and the
// 16:9 landscape master.
const VisualScene: React.FC<{ asset: SceneAsset }> = ({ asset }) => {
  const { width, height } = useVideoConfig();
  const frame = useCurrentFrame();
  const spec = asset.visualSpec!;
  const accent = spec.accent ?? "#ef4444";
  const padX = Math.round(width * 0.07);
  const titleSize = Math.round(width * 0.04);
  const bodySize = Math.round(width * 0.024);
  const smallSize = Math.round(width * 0.019);
  const opacity = interpolate(frame, [0, 12], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });

  return (
    <AbsoluteFill
      style={{
        backgroundColor: "#0b1526",
        padding: `${Math.round(height * 0.07)}px ${padX}px ${Math.round(height * 0.22)}px`,
        opacity,
        fontFamily: "sans-serif",
      }}
    >
      {spec.title ? (
        <div style={{ fontSize: titleSize, fontWeight: 800, color: "white", marginBottom: 28, lineHeight: 1.15 }}>
          {spec.title}
        </div>
      ) : null}
      {spec.kind === "line" || spec.kind === "bar" ? (
        <ChartVisual spec={spec} accent={accent} width={width - padX * 2} height={Math.round(height * 0.42)} />
      ) : spec.kind === "table" ? (
        <TableVisual spec={spec} accent={accent} bodySize={bodySize} />
      ) : (
        <CalloutVisual spec={spec} accent={accent} width={width} />
      )}
      {spec.source ? (
        <div style={{ fontSize: smallSize, color: "rgba(255,255,255,0.6)", marginTop: 24 }}>
          Source: {spec.source}
        </div>
      ) : null}
    </AbsoluteFill>
  );
};

// Line chart (SVG polyline with dots + labels) or bar chart (flex bars). Values
// are normalised against the series min/max (line) or max (bar) so any unit
// renders; the unit string is shown next to the top value when given.
// ANIMATED (2026-11-25, eye-catching overhaul): a static chart sitting
// untouched for ~8s is dead air, so the visual builds itself in the first
// ~1.5s - bars grow one by one with their values counting up, the line
// draws itself left to right with its dots popping in along it, the headline
// number counts up. All from the existing visualSpec props; nothing new is
// passed per video.
const ChartVisual: React.FC<{
  spec: NonNullable<SceneAsset["visualSpec"]>;
  accent: string;
  width: number;
  height: number;
}> = ({ spec, accent, width, height }) => {
  const frame = useCurrentFrame();
  const labels = spec.labels ?? [];
  const values = spec.values ?? [];
  if (labels.length === 0 || values.length === 0) return null;

  // Count-up formatting: integers stay integers, decimals keep one place.
  const countUp = (v: number, p: number) =>
    Number.isInteger(v) ? `${Math.round(v * p)}` : `${(v * p).toFixed(1)}`;

  if (spec.kind === "bar") {
    const max = Math.max(...values, 1);
    const labelSize = Math.round(width * 0.022);
    const valueSize = Math.round(width * 0.026);
    return (
      <div style={{ display: "flex", alignItems: "flex-end", gap: Math.round(width * 0.02), height, width: "100%" }}>
        {values.map((v, i) => {
          // Each bar grows over 30 frames, staggered 6 frames apart, so the
          // chart builds left to right; its value counts up with the growth.
          const p = interpolate(frame, [i * 6, i * 6 + 30], [0, 1], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
          });
          const labelOpacity = interpolate(frame, [i * 6 + 22, i * 6 + 32], [0, 1], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
          });
          return (
            <div key={i} style={{ flex: 1, display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "flex-end", height: "100%" }}>
              <div style={{ fontSize: valueSize, fontWeight: 700, color: "white", marginBottom: 8, opacity: labelOpacity }}>
                {countUp(v, p)}
                {spec.unit ? ` ${spec.unit}` : ""}
              </div>
              <div style={{ width: "70%", height: `${Math.max(0, (v / max) * 72 * p)}%`, backgroundColor: accent, borderRadius: 6 }} />
              <div style={{ fontSize: labelSize, color: "rgba(255,255,255,0.75)", marginTop: 10, textAlign: "center", opacity: labelOpacity }}>
                {labels[i]}
              </div>
            </div>
          );
        })}
      </div>
    );
  }

  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min || 1;
  const chartW = width;
  const chartH = height - 40;
  const points = values.map((v, i) => {
    const x = labels.length > 1 ? (i / (labels.length - 1)) * chartW : chartW / 2;
    const y = chartH - ((v - min) / span) * chartH * 0.85 - chartH * 0.05;
    return { x, y };
  });
  const polyline = points.map((p) => `${p.x},${p.y}`).join(" ");
  const labelSize = Math.round(width * 0.022);
  const valueSize = Math.round(width * 0.026);
  // The line draws itself over the first 42 frames (pathLength trick);
  // each dot pops in as the line reaches
  // it, and the headline number counts up to the latest value with it.
  const lineProgress = interpolate(frame, [0, 42], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const headOpacity = interpolate(frame, [0, 10], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });

  return (
    <div style={{ width: "100%" }}>
      <div style={{ fontSize: valueSize, fontWeight: 700, color: "white", marginBottom: 8, opacity: headOpacity }}>
        {countUp(values[values.length - 1], lineProgress)}
        {spec.unit ? ` ${spec.unit}` : ""}
      </div>
      <svg width={chartW} height={chartH} style={{ display: "block", overflow: "visible" }}>
        <polyline
          points={polyline}
          fill="none"
          stroke={accent}
          strokeWidth={Math.max(3, width * 0.004)}
          strokeLinejoin="round"
          strokeLinecap="round"
          pathLength={1}
          strokeDasharray={1}
          strokeDashoffset={1 - lineProgress}
        />
        {points.map((p, i) => {
          const dotAt = points.length > 1 ? (i / (points.length - 1)) * 42 : 21;
          const dotOpacity = interpolate(frame, [dotAt, dotAt + 6], [0, 1], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
          });
          return (
            <circle key={i} cx={p.x} cy={p.y} r={Math.max(5, width * 0.006)} fill={accent} stroke="#0b1526" strokeWidth={2} opacity={dotOpacity} />
          );
        })}
      </svg>
      <div style={{ display: "flex", justifyContent: "space-between", marginTop: 10 }}>
        {labels.map((l, i) => (
          <div key={i} style={{ fontSize: labelSize, color: "rgba(255,255,255,0.75)", textAlign: "center", flex: 1 }}>
            {l}
          </div>
        ))}
      </div>
    </div>
  );
};

const TableVisual: React.FC<{
  spec: NonNullable<SceneAsset["visualSpec"]>;
  accent: string;
  bodySize: number;
}> = ({ spec, accent, bodySize }) => {
  // ANIMATED (2026-11-25): rows fade in one by one, top to bottom, so the
  // table reads as a reveal rather than a static block. Opacity only -
  // transforms on <tr> are unreliable in Chromium, so no slide here.
  const frame = useCurrentFrame();
  const headOpacity = interpolate(frame, [0, 10], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  return (
    <table style={{ width: "100%", borderCollapse: "collapse", fontSize: bodySize, color: "white" }}>
      <thead>
        <tr style={{ opacity: headOpacity }}>
          {(spec.headers ?? []).map((h, i) => (
            <th
              key={i}
              style={{
                textAlign: "left",
                padding: "14px 16px",
                borderBottom: `3px solid ${accent}`,
                fontWeight: 800,
              }}
            >
              {h}
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {(spec.rows ?? []).map((row, i) => {
          const rowOpacity = interpolate(frame, [10 + i * 7, 22 + i * 7], [0, 1], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
          });
          return (
            <tr key={i} style={{ backgroundColor: i % 2 === 0 ? "rgba(255,255,255,0.05)" : "transparent", opacity: rowOpacity }}>
              {row.map((cell, j) => (
                <td key={j} style={{ padding: "14px 16px", borderBottom: "1px solid rgba(255,255,255,0.12)" }}>
                  {cell}
                </td>
              ))}
            </tr>
          );
        })}
      </tbody>
    </table>
  );
};

const CalloutVisual: React.FC<{
  spec: NonNullable<SceneAsset["visualSpec"]>;
  accent: string;
  width: number;
}> = ({ spec, accent, width }) => {
  // ANIMATED (2026-11-25): the accent bar grows down, the big number/text
  // slides up into place, the subtext fades in last - the callout lands
  // like a punchline instead of sitting there from frame 0.
  const frame = useCurrentFrame();
  const barScaleY = interpolate(frame, [0, 18], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const textOpacity = interpolate(frame, [6, 20], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const textY = interpolate(frame, [6, 20], [18, 0], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const subOpacity = interpolate(frame, [28, 42], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  return (
    <div style={{ display: "flex", flexDirection: "column", justifyContent: "center", flex: 1 }}>
      <div
        style={{
          width: 12,
          height: Math.round(width * 0.09),
          backgroundColor: accent,
          marginBottom: 28,
          transform: `scaleY(${barScaleY})`,
          transformOrigin: "top",
        }}
      />
      <div
        style={{
          fontSize: Math.round(width * 0.062),
          fontWeight: 800,
          color: "white",
          lineHeight: 1.2,
          opacity: textOpacity,
          transform: `translateY(${textY}px)`,
        }}
      >
        {spec.text}
      </div>
      {spec.subtext ? (
        <div style={{ fontSize: Math.round(width * 0.028), color: "rgba(255,255,255,0.75)", marginTop: 20, lineHeight: 1.4, opacity: subOpacity }}>
          {spec.subtext}
        </div>
      ) : null}
    </div>
  );
};

// Branded end card (Trendline Now, 2026-11-21): the navy + red house look,
// the CTA lines, the big @handle, a SUBSCRIBE pill, and the platform chips.
// Profiles without outroHandles get the same card with just their lines (the
// legacy "LIKE, FOLLOW & SUBSCRIBE" card, restyled) - no handle, no chips,
// so the destination is never shown twice. A third line (the vertical
// short's end-card line, e.g. "Want the full story? Watch it on YouTube —
// @spotlightnowofficial") renders in the accent colour.
// First line renders as the header; every line after it as a smaller sub-line.
const Outro: React.FC<{
  lines: string[];
  audioPath?: string;
  handles: string[];
  handle?: string;
}> = ({ lines, audioPath, handles, handle }) => {
  const frame = useCurrentFrame();
  const { width } = useVideoConfig();
  const branded = handles.length > 0 && handle !== undefined && handle !== "";
  const headerSize = Math.round(width * 0.052);
  const subSize = Math.round(width * 0.03);
  const handleSize = Math.round(width * 0.058);
  const chipSize = Math.round(width * 0.024);
  const pillSize = Math.round(width * 0.028);
  const fade = (start: number, end: number) =>
    interpolate(frame, [start, end], [0, 1], {
      extrapolateLeft: "clamp",
      extrapolateRight: "clamp",
    });

  return (
    <AbsoluteFill
      style={{
        backgroundColor: "#0b1526",
        backgroundImage: "radial-gradient(ellipse 90% 70% at 50% 42%, #16294d 0%, #0b1526 70%)",
        justifyContent: "center",
        alignItems: "center",
        fontFamily: "sans-serif",
        textAlign: "center",
      }}
    >
      {audioPath ? <Audio src={staticFile(audioPath)} /> : null}
      <div
        style={{
          width: Math.round(width * 0.12),
          height: Math.max(6, Math.round(width * 0.006)),
          backgroundColor: "#ef4444",
          marginBottom: Math.round(width * 0.03),
          opacity: fade(0, 10),
        }}
      />
      {lines.map((line, i) => (
        <div
          key={i}
          style={{
            fontSize: i === 0 ? headerSize : subSize,
            fontWeight: i === 0 ? 800 : 500,
            color: i === 0 ? "white" : i === 1 ? "rgba(255,255,255,0.88)" : "#ef4444",
            lineHeight: 1.3,
            maxWidth: "86%",
            marginTop: i === 0 ? 0 : Math.round(width * 0.014),
            opacity: fade(i * 8, i * 8 + 12),
          }}
        >
          {line}
        </div>
      ))}
      {branded ? (
        <>
          <div
            style={{
              fontSize: handleSize,
              fontWeight: 800,
              color: "#ef4444",
              marginTop: Math.round(width * 0.03),
              letterSpacing: 1,
              opacity: fade(20, 32),
            }}
          >
            {handle}
          </div>
          <div
            style={{
              marginTop: Math.round(width * 0.025),
              backgroundColor: "#ef4444",
              color: "white",
              fontWeight: 800,
              fontSize: pillSize,
              letterSpacing: 2,
              padding: `${Math.round(width * 0.012)}px ${Math.round(width * 0.035)}px`,
              borderRadius: 9999,
              opacity: fade(28, 40),
            }}
          >
            SUBSCRIBE
          </div>
          <div
            style={{
              marginTop: Math.round(width * 0.025),
              fontSize: chipSize,
              color: "rgba(255,255,255,0.65)",
              letterSpacing: 2,
              opacity: fade(36, 48),
            }}
          >
            {handles.join("  •  ")}
          </div>
        </>
      ) : null}
    </AbsoluteFill>
  );
};

const Scene: React.FC<{ asset: SceneAsset; durationInFrames: number; isHook?: boolean }> = ({
  asset,
  durationInFrames,
  isHook,
}) => {
  const { fps, width } = useVideoConfig();
  const frame = useCurrentFrame();
  const badgeSize = Math.round(width * 0.02);
  // SPECULATION badge pops in with a spring instead of appearing flat.
  const badgeSpring = spring({ frame: frame - 4, fps, config: { damping: 12, stiffness: 200 } });
  const badgeOpacity = interpolate(frame, [4, 12], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  return (
    <AbsoluteFill style={{ backgroundColor: "black" }}>
      {asset.kind === "visual" ? (
        <VisualScene asset={asset} />
      ) : asset.kind === "video" ? (
        <MotionVideo asset={asset} durationInFrames={durationInFrames} />
      ) : (
        <KenBurnsImage asset={asset} durationInFrames={durationInFrames} />
      )}
      {asset.narrationAudioPath ? <Audio src={staticFile(asset.narrationAudioPath)} /> : null}
      {asset.captionText ? (
        <Caption text={asset.captionText} durationInFrames={durationInFrames} hook={isHook} />
      ) : null}
      {asset.isSpeculative ? (
        <div
          style={{
            position: "absolute",
            top: "4%",
            left: "50%",
            transform: `translateX(-50%) scale(${badgeSpring})`,
            opacity: badgeOpacity,
            backgroundColor: "#ef4444",
            color: "white",
            fontFamily: "sans-serif",
            fontSize: badgeSize,
            fontWeight: 800,
            letterSpacing: Math.round(width * 0.002),
            padding: `${Math.round(width * 0.008)}px ${Math.round(width * 0.02)}px`,
            borderRadius: 999,
          }}
        >
          SPECULATION
        </div>
      ) : null}
    </AbsoluteFill>
  );
};

export const Assembly: React.FC<AssemblyProps> = ({
  scenes,
  backgroundMusicPath,
  backgroundMusicVolume,
  outroSeconds,
  outroLines,
  outroAudioPath,
  outroHandles,
  outroHandle,
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

  // The outro card closes (when outroSeconds > 0) with a fade transition
  // into it, exactly as Root.tsx's calculateMetadata counts it. No intro
  // sting, no brief (removed 2026-11-25, Rishi): the video opens directly
  // with scene 0, the hook.

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
              {/* Scene 0 is the hook: its first sentence renders as the big animated title card. */}
              <Scene asset={asset} durationInFrames={durationInFrames} isHook={i === 0} />
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
            <Outro lines={outroLines} audioPath={outroAudioPath} handles={outroHandles} handle={outroHandle} />
          </TransitionSeries.Sequence>
        ) : null}
      </TransitionSeries>
    </AbsoluteFill>
  );
};
