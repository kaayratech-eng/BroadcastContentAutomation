# remotion-assembler

Composites character art + stock footage + TTS narration + text overlays into a
rendered video, applying pan/zoom motion to every asset regardless of source — a
Gemini-generated character portrait and a Pexels photo get identical Ken-Burns
treatment; real Pexels video clips get a gentle zoom only, so as not to fight the
source footage's own camera motion. Part of Deliverable 6
(`../tasks/todo.md`) — the no-Vidu visual pipeline for the Chronicle & Chaos profile.

## Invocation contract

`VideoGen` will shell out to this project the same way it already shells out to
ffmpeg (`VideoAssembler.cs`'s `RunFfmpegAsync`):

```
npx remotion render src/index.ts Assembly <output.mp4> --props=<scene-props.json>
```

`<scene-props.json>` must match `AssemblyProps` in `src/schema.ts`: an array of
`scenes`, each an image or video asset with its duration, optional narration audio
path, and optional caption text. All paths must be absolute — Remotion resolves
relative `src` paths against this project's `public/` folder, not the caller's
working directory.

## Setup

```
npm install
npm run typecheck   # validates src/ against schema.ts, no render
npm start           # opens Remotion Studio for interactive preview
```

## Status

Scaffold only — no render has been run against real assets yet. `VideoGen`'s call
site (shelling out here instead of/alongside ffmpeg) has not been wired up either;
see "Rewire ImageClient call sites for both profiles" in the project's task list.
