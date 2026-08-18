# Background music pool

Every track in this folder is a candidate bed. Each video picks one, by a stable hash of
its job folder, so the 16:9 and 9:16 cuts of the same video always agree and re-running
`--assemble` reproduces the earlier render. Roughly 10-15 tracks gives a couple of months
before a repeat at 2 videos/day.

An empty folder is fine — videos render narration-only and the run says so.

## What belongs here

- **Instrumental only.** No vocals, no "ooh-ah" pads, no lyrics in any language. Anything
  sung competes with the narrator for the same attention, which is the one thing a bed
  must not do. This is the reason the original `music.mp3` was pulled.
- **Soft and unobtrusive.** Gentle, warm, mid-tempo. Nothing with a hard beat, a busy
  melody line, or a big dynamic swing.
- **At least ~90 seconds**, so it covers a full video without looping back on itself.
- Any of `.mp3 .m4a .wav .ogg .flac .aac .opus`.

Mastering level does not matter. Each track is measured and normalised to
`BackgroundMusicLufs` (appsettings.json) at mix time, so a quiet track and a loud one end
up at the same bed level.

## Licensing

This is a monetised kids' channel. A Content ID claim on the audio can demonetise or block
the video, and "free to download" is routinely not "free to use commercially" — check the
licence per track, and keep a note of where each one came from.

Worth checking, in rough order of safety:

- **YouTube Audio Library** (studio.youtube.com → Audio Library), filtered to Instrumental
  + Calm/Happy. YouTube's own library, so it will not self-claim.
- **Pixabay Music** and **Free Music Archive** (filter to CC0) — no attribution required,
  which matters for TikTok/Instagram/Facebook where a description credit is easy to lose.
- **Incompetech** (Kevin MacLeod) — large instrumental catalogue, but most tracks require
  attribution in the description.
