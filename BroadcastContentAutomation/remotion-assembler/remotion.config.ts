import { Config } from "@remotion/cli/config";

// VideoGen's ffmpeg step already handles final mux/loudness/subtitle-burn, so this
// renderer's own output is an intermediate: keep it a straightforward H.264 mp4 rather
// than tuning for final-delivery quality here.
Config.setVideoImageFormat("jpeg");
Config.setCodec("h264");
Config.setChromiumOpenGlRenderer("angle");
