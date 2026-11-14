import { Config } from "@remotion/cli/config";

// VideoGen's ffmpeg step already handles final mux/loudness/subtitle-burn, so this
// renderer's own output is an intermediate: keep it a straightforward H.264 mp4 rather
// than tuning for final-delivery quality here.
Config.setVideoImageFormat("jpeg");
Config.setCodec("h264");
// Delivery format (2026-11-13): Meta's transcoder rejects full-range yuvj420p, and
// Remotion renders it by default from the JPEG-sourced frames. Force limited-range
// yuv420p here; NormalizeLoudnessAsync re-checks the finished file and re-encodes
// if this ever stops taking effect, so the contract doesn't depend on one layer.
Config.setPixelFormat("yuv420p");
Config.setChromiumOpenGlRenderer("angle");

// 2026-11-12: the long-form teaser died TWICE at the same frame (2156/2443)
// with a 28s delayRender timeout on a stock-video seek (scene07-stock.mp4 at
// 8.7s) that renders fine when fresh - the seek just runs slow under sustained
// render load (35+ min in, concurrency 2, 1.5-CPU VM). The clip itself is valid
// (ffmpeg decodes all 416 frames cleanly). Give slow seeks 120s instead of 28s
// so a loaded frame can finish rather than failing the whole render. A truly
// wedged frame still fails, just after 120s instead of 28s.
Config.setDelayRenderTimeoutInMilliseconds(120000);
