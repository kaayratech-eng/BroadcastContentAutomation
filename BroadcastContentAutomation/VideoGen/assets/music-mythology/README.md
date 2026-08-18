# Background music pool - Chronicle & Chaos

Every track in this folder (or in a mood subfolder below it) is a candidate bed. Each video
picks one, by a stable hash of its job folder, so the 16:9 and 9:16 cuts of the same video
always agree and re-running `--assemble` reproduces the earlier render. Roughly 10-15
tracks per mood gives a couple of months before a repeat at 2 videos/day.

An empty folder is fine — videos render narration-only and the run says so. (The folder
itself must still exist on disk, even empty, or the run throws when it tries to enumerate it.)

## Mood subfolders

`VideoAssembler.ResolveBackgroundMusic` first looks for a subfolder here named after the
script's chosen `musicMood`, slugified (spaces to dashes, lowercase) - e.g. a script that
picks `"somber orchestral"` looks in `somber-orchestral/`. If that subfolder doesn't exist
or is empty, it falls back to whatever is directly in this top-level folder. Mythology's
formats currently draw from this closed set of moods, so these are the subfolders worth
populating first:

- `somber-orchestral/`
- `ancient-drone/`
- `tense-atmospheric/`
- `driving-epic/`
- `curious-ambient/`
- `mysterious-tense/`

## What belongs here

- **Instrumental only.** No vocals, no lyrics in any language. Anything sung competes with
  the narrator for the same attention, which is the one thing a bed must not do.
- **Cinematic, not upbeat.** Orchestral, ambient, or tension-building - think documentary
  or trailer score, not the soft-and-cheerful register GiggleGarden's pool uses. Match each
  subfolder's mood: `somber-orchestral` and `ancient-drone` should sit low and atmospheric;
  `driving-epic` can carry real momentum; `tense-atmospheric` and `mysterious-tense` should
  stay unresolved rather than build to a hook.
- **At least ~90 seconds**, so it covers a full video without looping back on itself.
- Any of `.mp3 .m4a .wav .ogg .flac .aac .opus`.

Mastering level does not matter. Each track is measured and normalised to
`BackgroundMusicLufs` (`MythologyProfile.cs`, currently a `-30.0` placeholder pending a real
measurement pass against `en-US-AriaNeural` narration) at mix time, so a quiet track and a
loud one end up at the same bed level.

## Licensing

This is a monetised channel. A Content ID claim on the audio can demonetise or block the
video, and "free to download" is routinely not "free to use commercially" - check the
licence per track, and keep a note of where each one came from.

Worth checking, in rough order of safety:

- **YouTube Audio Library** (studio.youtube.com → Audio Library), filtered to Instrumental +
  Cinematic/Dramatic/Ambient. YouTube's own library, so it will not self-claim.
- **Pixabay Music** and **Free Music Archive** (filter to CC0) - no attribution required,
  which matters for TikTok/Instagram/Facebook where a description credit is easy to lose.
- **Incompetech** (Kevin MacLeod) - large instrumental catalogue with a strong
  cinematic/dramatic/fantasy section, but most tracks require attribution in the description.
