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
const ChartVisual: React.FC<{
  spec: NonNullable<SceneAsset["visualSpec"]>;
  accent: string;
  width: number;
  height: number;
}> = ({ spec, accent, width, height }) => {
  const labels = spec.labels ?? [];
  const values = spec.values ?? [];
  if (labels.length === 0 || values.length === 0) return null;

  if (spec.kind === "bar") {
    const max = Math.max(...values, 1);
    const labelSize = Math.round(width * 0.022);
    const valueSize = Math.round(width * 0.026);
    return (
      <div style={{ display: "flex", alignItems: "flex-end", gap: Math.round(width * 0.02), height, width: "100%" }}>
        {values.map((v, i) => (
          <div key={i} style={{ flex: 1, display: "flex", flexDirection: "column", alignItems: "center", justifyContent: "flex-end", height: "100%" }}>
            <div style={{ fontSize: valueSize, fontWeight: 700, color: "white", marginBottom: 8 }}>
              {v}
              {spec.unit ? ` ${spec.unit}` : ""}
            </div>
            <div style={{ width: "70%", height: `${Math.max(4, (v / max) * 72)}%`, backgroundColor: accent, borderRadius: 6 }} />
            <div style={{ fontSize: labelSize, color: "rgba(255,255,255,0.75)", marginTop: 10, textAlign: "center" }}>
              {labels[i]}
            </div>
          </div>
        ))}
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

  return (
    <div style={{ width: "100%" }}>
      <div style={{ fontSize: valueSize, fontWeight: 700, color: "white", marginBottom: 8 }}>
        {values[values.length - 1]}
        {spec.unit ? ` ${spec.unit}` : ""}
      </div>
      <svg width={chartW} height={chartH} style={{ display: "block", overflow: "visible" }}>
        <polyline points={polyline} fill="none" stroke={accent} strokeWidth={Math.max(3, width * 0.004)} strokeLinejoin="round" strokeLinecap="round" />
        {points.map((p, i) => (
          <circle key={i} cx={p.x} cy={p.y} r={Math.max(5, width * 0.006)} fill={accent} stroke="#0b1526" strokeWidth={2} />
        ))}
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
}> = ({ spec, accent, bodySize }) => (
  <table style={{ width: "100%", borderCollapse: "collapse", fontSize: bodySize, color: "white" }}>
    <thead>
      <tr>
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
      {(spec.rows ?? []).map((row, i) => (
        <tr key={i} style={{ backgroundColor: i % 2 === 0 ? "rgba(255,255,255,0.05)" : "transparent" }}>
          {row.map((cell, j) => (
            <td key={j} style={{ padding: "14px 16px", borderBottom: "1px solid rgba(255,255,255,0.12)" }}>
              {cell}
            </td>
          ))}
        </tr>
      ))}
    </tbody>
  </table>
);

const CalloutVisual: React.FC<{
  spec: NonNullable<SceneAsset["visualSpec"]>;
  accent: string;
  width: number;
}> = ({ spec, accent, width }) => (
  <div style={{ display: "flex", flexDirection: "column", justifyContent: "center", flex: 1 }}>
    <div style={{ width: 12, height: Math.round(width * 0.09), backgroundColor: accent, marginBottom: 28 }} />
    <div style={{ fontSize: Math.round(width * 0.062), fontWeight: 800, color: "white", lineHeight: 1.2 }}>
      {spec.text}
    </div>
    {spec.subtext ? (
      <div style={{ fontSize: Math.round(width * 0.028), color: "rgba(255,255,255,0.75)", marginTop: 20, lineHeight: 1.4 }}>
        {spec.subtext}
      </div>
    ) : null}
  </div>
);

// Animated intro sting (Trendline Now, 2026-11-21) - the channel's brand
// moment, played before scene 1 on every video. Art-forward by design: the
// navy + red identity IS the frame, not a logo pasted on black. A red trend
// line draws itself diagonally upward across the frame, the TN logo badge
// springs in on top of it, "TRENDLINE NOW" slams in below, and the tagline
// fades in last. All sizes scale with the frame width, so the same component
// works for the 16:9 master and the 9:16 short. No narration - the sting is
// purely visual, ~3.5s.
const Intro: React.FC<{ logoPath: string }> = ({ logoPath }) => {
  const frame = useCurrentFrame();
  const { fps, width, height } = useVideoConfig();

  // The trend line draws itself across the first ~1.6s (frames 5-50).
  const lineProgress = interpolate(frame, [5, 50], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const arrowOpacity = interpolate(frame, [46, 52], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  // Logo badge springs in over frames 10-40, with a slight overshoot settle.
  const logoSpring = spring({ frame: frame - 10, fps, config: { damping: 11, stiffness: 110 } });
  const logoScale = 0.45 + 0.55 * logoSpring;
  const logoOpacity = interpolate(frame, [10, 22], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  // "TRENDLINE NOW" slams in over frames 45-60.
  const titleSpring = spring({ frame: frame - 45, fps, config: { damping: 13, stiffness: 160 } });
  const titleScale = 1.5 - 0.5 * titleSpring;
  const titleOpacity = interpolate(frame, [45, 53], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  // Tagline fades/slides up last, frames 60-80.
  const tagOpacity = interpolate(frame, [60, 78], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const tagTranslateY = interpolate(frame, [60, 78], [16, 0], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  // Disclaimer line fades in last, frames 80-96, so it is legible before the
  // ~3.5s sting ends. Intro only renders for Trendline Now.
  const disclaimerOpacity = interpolate(frame, [80, 96], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });

  const logoSize = Math.round(Math.min(width, height) * 0.42);
  const titleSize = Math.round(width * 0.072);
  const tagSize = Math.round(width * 0.026);
  const lineW = Math.max(6, Math.round(width * 0.009));

  // Zigzag rising trend line, bottom-left to top-right, as a fraction of the
  // frame - the same rising-trend motif as the logo's red arrow. The logo
  // badge covers the middle of the line when it springs in, so the line's
  // ends stay visible on either side.
  const pts: Array<[number, number]> = [
    [0.06, 0.82],
    [0.26, 0.66],
    [0.38, 0.74],
    [0.56, 0.52],
    [0.68, 0.6],
    [0.94, 0.3],
  ];
  const pathD = pts
    .map(([x, y], i) => `${i === 0 ? "M" : "L"}${(x * width).toFixed(1)},${(y * height).toFixed(1)}`)
    .join(" ");
  // Arrowhead triangle at the end of the line, pointing along the last segment.
  const ex = 0.94 * width;
  const ey = 0.3 * height;
  const px = 0.68 * width;
  const py = 0.6 * height;
  const dx = ex - px;
  const dy = ey - py;
  const len = Math.hypot(dx, dy) || 1;
  const ux = dx / len;
  const uy = dy / len;
  const ah = Math.round(width * 0.045);
  const aw = Math.round(width * 0.026);
  const bx = ex - ux * ah;
  const by = ey - uy * ah;
  const arrowPoints = `${ex.toFixed(1)},${ey.toFixed(1)} ${(bx - uy * aw).toFixed(1)},${(by + ux * aw).toFixed(1)} ${(bx + uy * aw).toFixed(1)},${(by - ux * aw).toFixed(1)}`;

  return (
    <AbsoluteFill
      style={{
        backgroundColor: "#0b1526",
        backgroundImage: "radial-gradient(ellipse 90% 70% at 50% 42%, #16294d 0%, #0b1526 70%)",
        justifyContent: "center",
        alignItems: "center",
        fontFamily: "sans-serif",
        overflow: "hidden",
      }}
    >
      <svg width={width} height={height} style={{ position: "absolute", inset: 0 }}>
        <path
          d={pathD}
          fill="none"
          stroke="#ef4444"
          strokeWidth={lineW}
          strokeLinejoin="round"
          strokeLinecap="round"
          pathLength={1}
          strokeDasharray={1}
          strokeDashoffset={1 - lineProgress}
        />
        <polygon points={arrowPoints} fill="#ef4444" opacity={arrowOpacity} />
      </svg>
      <div
        style={{
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          transform: `scale(${logoScale})`,
          opacity: logoOpacity,
        }}
      >
        <Img src={staticFile(logoPath)} style={{ width: logoSize, height: logoSize }} />
      </div>
      <div
        style={{
          fontSize: titleSize,
          fontWeight: 800,
          color: "white",
          letterSpacing: Math.round(width * 0.008),
          marginTop: Math.round(height * 0.035),
          transform: `scale(${titleScale})`,
          opacity: titleOpacity,
          textAlign: "center",
        }}
      >
        TRENDLINE NOW
      </div>
      <div
        style={{
          fontSize: tagSize,
          fontWeight: 700,
          color: "#ef4444",
          letterSpacing: Math.round(width * 0.006),
          marginTop: Math.round(height * 0.018),
          opacity: tagOpacity,
          transform: `translateY(${tagTranslateY}px)`,
          textAlign: "center",
        }}
      >
        THE BIGGEST STORY OF THE DAY, EXPLAINED
      </div>
      <div
        style={{
          fontSize: Math.round(width * 0.016),
          fontWeight: 500,
          color: "rgba(255,255,255,0.6)",
          letterSpacing: Math.round(width * 0.002),
          marginTop: Math.round(height * 0.02),
          opacity: disclaimerOpacity,
          textAlign: "center",
          maxWidth: "80%",
        }}
      >
        Includes commentary and clearly-labelled speculation. Verify key facts from primary sources.
      </div>
    </AbsoluteFill>
  );
};

// Branded end card (Trendline Now, 2026-11-21): the navy + red house look,
// the CTA lines, the big @handle, a SUBSCRIBE pill, and the platform chips.
// Profiles without outroHandles get the same card with just their lines (the
// legacy "LIKE, FOLLOW & SUBSCRIBE" card, restyled) - no handle, no chips,
// so the destination is never shown twice. A third line (the vertical
// short's "Watch the full video on YouTube") renders in the accent colour.
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

const Scene: React.FC<{ asset: SceneAsset; durationInFrames: number }> = ({
  asset,
  durationInFrames,
}) => {
  const { width } = useVideoConfig();
  const badgeSize = Math.round(width * 0.02);
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
      {asset.captionText ? <Caption text={asset.captionText} /> : null}
      {asset.isSpeculative ? (
        <div
          style={{
            position: "absolute",
            top: "4%",
            left: "50%",
            transform: "translateX(-50%)",
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
  introSeconds,
  introLogoPath,
  isCalm,
  briefScenes = [],
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

  // The intro sting leads (when the profile has one) and the outro card
  // closes (when outroSeconds > 0); each gets a fade transition into the
  // next sequence, exactly as Root.tsx's calculateMetadata counts them.
  const hasIntro = introSeconds > 0 && introLogoPath !== undefined && introLogoPath !== "";
  // Curiosity brief (Rishi 2026-11-21): teaser scenes play FIRST, before
  // the intro sting, so the viewer is hooked before the brand moment.
  const hasBrief = briefScenes.length > 0;

  return (
    <AbsoluteFill>
      {backgroundMusicPath ? (
        <Audio src={staticFile(backgroundMusicPath)} volume={backgroundMusicVolume} loop />
      ) : null}
      <TransitionSeries>
        {hasBrief
          ? briefScenes.flatMap((asset, i) => {
              const durationInFrames = Math.round(asset.durationInSeconds * fps);
              const elements = [
                <TransitionSeries.Sequence key={`brief-${i}`} durationInFrames={durationInFrames}>
                  <Scene asset={asset} durationInFrames={durationInFrames} />
                </TransitionSeries.Sequence>,
              ];
              // A transition follows every brief scene (into the next brief
              // scene, or into the intro / first scene).
              elements.push(
                <TransitionSeries.Transition
                  key={`transition-brief-${i}`}
                  presentation={fade()}
                  timing={transitionTiming}
                />
              );
              return elements;
            })
          : null}
        {hasIntro ? (
          <>
            <TransitionSeries.Sequence durationInFrames={Math.round(introSeconds * fps)}>
              <Intro logoPath={introLogoPath} />
            </TransitionSeries.Sequence>
            <TransitionSeries.Transition
              key="transition-intro"
              presentation={fade()}
              timing={transitionTiming}
            />
          </>
        ) : null}
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
            <Outro lines={outroLines} audioPath={outroAudioPath} handles={outroHandles} handle={outroHandle} />
          </TransitionSeries.Sequence>
        ) : null}
      </TransitionSeries>
    </AbsoluteFill>
  );
};
