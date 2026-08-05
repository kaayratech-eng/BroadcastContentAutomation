# GiggleGarden Automation — Architecture Overview (Phase 1)

Generated read-only, before any changes. Source: `gigglegarden-automation/README.md`,
root `README.md`, and full read of every `.cs` file in `gigglegarden-automation/`.

## What the system does

A YouTube-first (with half-built Instagram/Facebook/TikTok support) automation pipeline
that generates short animated nursery-rhyme/learning videos for kids (ages 2–6) in
English, Hindi, and Punjabi, and uploads them on a schedule — gated by a mandatory
human-approval step before anything goes live.

Business: **GiggleGarden**, a kids' YouTube channel. Recurring character: "Gigi", a
yellow duckling.

## Tech stack

- **.NET 8**, four projects under `gigglegarden-automation/` (no `.sln` file — each
  project is built/run independently via `dotnet run`/`dotnet publish` in its own dir):
  - `VideoGen` — content generation
  - `Uploader` — publishing (currently: YouTube only, see Bugs below)
  - `TokenCapture` — one-time OAuth setup utility
  - `Shared` — common library (Sidecar model, retry handler, text utilities)
- **FFmpeg** (external binary, on PATH or configured path) — video assembly, subtitles, Ken Burns effect, audio mixing.
- No database, no web server, no queue — the pipeline is entirely file-system driven (watch a folder, drop a file, move a file).

## External / paid services (where cost is incurred)

| Service | Used for | Cost per README |
|---|---|---|
| Anthropic API (Claude) | Script writing (`VideoGen`) + fallback metadata generation (`Uploader`) | ~$0.02/video (script) |
| Azure Speech (TTS) | Narration audio, 3 voices (en/hi/pa neural) | Free tier (0.5M chars/mo), then ~$0.05/video |
| OpenAI Images API (`gpt-image-1`) | Scene illustrations, 7 scenes × 2 orientations | ~$0.30/video |
| Stability AI | Alternate image provider (`ImageProvider: "stability"`), **currently unused** — OpenAI is default | Not itemized |
| YouTube Data API v3 | Upload + trend search | Quota-based: upload ≈1600 units, trend search ≈100 units, daily cap 10,000 |
| Meta Graph API (Instagram/Facebook) | Reels publishing | Free (API), but **code path is dead — see Bugs** |

Total claimed cost: **<$0.50/video** (script + TTS + images). Upload/quota costs are non-monetary (API quota, not billed).

## Pipeline, end to end

**Stage 1 — VideoGen** (`gigglegarden-automation/VideoGen/Program.cs`, manually invoked or Task-Scheduler job 1, e.g. 6am):
1. `ScriptGenerator.GenerateAsync` — calls Claude (`claude-opus-5` by default) for a 6–8 scene script: narration, image prompts, title, description, tags, short-form caption. JSON-only response, original content required (no copying existing rhymes).
2. `TtsClient.SynthesizeAsync` — Azure Speech REST API per scene, per-language neural voice, slowed rate + raised pitch for kids.
3. `ImageClient.GenerateAsync` — OpenAI (default) or Stability, one landscape image per scene + optionally one portrait image per scene (`GenerateVerticalImages`, on by default) so the vertical cut isn't a center-crop of a landscape frame.
4. `VideoAssembler.AssembleAsync` — FFmpeg: per-scene clip (Ken Burns zoom, burned-in wrapped subtitle via `drawtext` + `textfile=`), concat, background-music mix. Produces **two renders**: a 1920×1080 landscape master and a 1080×1920 short (trimmed to fit `VerticalMaxSeconds` = 85s).
5. Writes a **Sidecar JSON** (`Shared/Sidecar.cs`) next to each render: `Title, Description, Tags, Approved:false, Language, Aspect (landscape|vertical), Targets[], Publications{}, CaptionOverrides{}`. `Targets` defaults to `["youtube"]` for landscape and `["youtube","instagram","facebook","tiktok"]` for vertical.
6. Human reviews both videos, flips `"approved": true` in each sidecar.

**Stage 2 — Uploader** (`gigglegarden-automation/Uploader/Program.cs`, Task-Scheduler job 2, e.g. 4pm):
1. Scans `D:\Business\Videos\*.mp4`, oldest first, capped at `MaxUploadsPerRun` (default 3) for quota safety.
2. Per video: reads the sidecar if present (else calls YouTube Search API for trending kids' content + Claude, `claude-sonnet-4-6`, to auto-generate multilingual metadata).
3. If not approved: writes/leaves the sidecar and skips (approval gate).
4. If approved: uploads to YouTube via `Google.Apis.YouTube.v3`, `selfDeclaredMadeForKids: true`, `MadeForKids: true`, category "Entertainment", privacy from config (start `"private"`).
5. Moves the `.mp4` (+ sidecar) to `\done` on success, or to `\failed` on any exception.

**Stage 0 — TokenCapture** (one-time, manual): opens a browser OAuth consent flow for the channel-owner Google account, persists a refresh token to `%APPDATA%\GiggleGarden` via `Google.Apis` `FileDataStore`. The Uploader reuses this token store silently (no browser at runtime).

## Kids-safety / compliance mechanisms already in place

- Every video requires an explicit human `"approved": true` before upload — no fully-autonomous publish path exists today.
- `SelfDeclaredMadeForKids` / `MadeForKids` are hard-set `true` on every upload (not configurable per-video).
- Uploads default to `PrivacyStatus: "private"` until manually tested and switched to `"public"`.
- Script prompt explicitly requires original content ("do not copy existing rhymes' lyrics"), ages 2–6 appropriate topics only.
- Image prompts explicitly forbid on-image text.

## Data/state flow

Entirely file-system based, no DB:
```
VideoGen writes  →  D:\Business\Videos\*.mp4 + *.json (sidecar)
Uploader reads   →  same folder, moves processed files to \done or \failed
```
`Videos/`, `Videos/done`, `Videos/failed`, `Videos/logs` are git-ignored (correctly — generated media, not source).

## Assumptions made while reading (not verified by running code)

- Assumed `ClaudeModel` values (`claude-opus-5` in VideoGen, `claude-sonnet-4-6` in Uploader) are what's actually intended — **flagged below as an open question**, since `claude-sonnet-4-6` doesn't match any current Anthropic model-naming pattern I'm aware of.
- Assumed the `Publishing/` folder (Instagram support) represents in-progress/abandoned work rather than something wired up elsewhere I haven't found — confirmed via grep: nothing references `IPublisher`, `InstagramPublisher`, or `MetaGraph` except themselves.
- Assumed `UploaderApp/` (published `.dll`s dated Apr–Jul 2024) is a stale build output from a prior/simpler version of the uploader, since the current `gigglegarden-automation/` source tree is dated Aug 2026 and structurally different (namespace, config shape) from what those DLLs would produce. Not 100% certain without decompiling — worth confirming before deleting.

## Open questions — README vs. code disagreements, or unclear intent

1. **The multi-platform publisher is entirely dead code.** `Uploader/Program.cs` (the actual entry point) defines its *own* private, YouTube-only `record AppConfig` and `class VideoMetadata`/`Logger` inline, and never references `Uploader/AppConfig.cs` (the multi-platform config with `PlatformsConfig.Instagram/Facebook/TikTok`), `Publishing/IPublisher.cs`, `Publishing/InstagramPublisher.cs`, or `Publishing/MetaGraph.cs`. Those files compile (nothing excludes them from the `.csproj`) but are never instantiated or called. **Consequence:** VideoGen already writes rich `Sidecar` JSON with `Targets`/`Aspect`/`Publications` for multi-platform fan-out, but the Uploader ignores all of that — it deserializes the sidecar into a flat, unrelated `VideoMetadata` type (extra JSON fields are silently dropped) and uploads **both the landscape master and the vertical short to plain YouTube**, with no distinction and no Instagram/Facebook/TikTok publishing at all, despite the sidecar declaring those targets.
   **Question: is Instagram/Facebook/TikTok support intended to be finished and wired up (Phase 3 candidate), or should the half-built `Publishing/` folder + `AppConfig.cs` + namespaced `Logger.cs` be deleted as abandoned scaffolding, with the Uploader staying YouTube-only?**
2. Only `InstagramPublisher` was ever written — no `FacebookPublisher` or `TikTokPublisher` exist despite `AppConfig.cs` having full `FacebookConfig`/`TikTokConfig` records. This supports "abandoned mid-build" over "wired up elsewhere."
3. ~~`claude-sonnet-4-6` (Uploader's `ClaudeModel` default)~~ — **Resolved.** Checked against the authoritative model catalog: `claude-sonnet-4-6` (Claude Sonnet 4.6) is a real, currently-active model — not a typo or bug. `claude-opus-5` (VideoGen's `ClaudeModel` default) is also real — it's the current Opus-tier flagship, the *most expensive* model available ($5/$25 per MTok). Using it for a short structured script-writing call (6-8 sentences, JSON output) isn't broken, but it's the priciest choice on the table for a task README prices at ~$0.02/video. This is a **cost right-sizing opportunity, not a bug** — moved to `tasks/cost-optimization.md` (Phase 4) rather than the bug list.
4. Root `README.md` says the OAuth client secret should live at `C:\Secure\GiggleGarden\client_secret.json`, but `Uploader/appsettings.json` actually points `ClientSecretPath` at `D:\Business\Youtube\client_secret_desktop.json` — inside the repo tree (git-ignored, so not committed, but not the secured location the README describes either). **Question: intentional relocation, or should this move to match the README/`C:\Secure\...` convention?**
5. `ImageProvider: "stability"` code path (`ImageClient.StabilityAsync`) exists but `StabilityApiKey` is empty in the template and OpenAI is the default — dead-ish/unused path, low priority, listed for completeness.
6. Root-level `package.json`/`node_modules`/`.claude-flow/`/`.swarm/`/`ruvector.db` are **Claude-Flow/Ruflo agent-tooling scaffolding** (auto-installed per your global `~/.claude/CLAUDE.md` "Ruflo Integration" instructions), unrelated to the GiggleGarden product itself. Confirmed via `git rm --cached` in Phase 0 that these were accidentally swept into the first commit. Not a bug in the product, just noting it's a second, unrelated concern living in the same repo root as the actual .NET solution.
7. `Claude-API/API Key.txt` — a plaintext API key file sits in the repo directory (correctly git-ignored via the `Claude-API/` rule, never committed). Given you have a security convention elsewhere (`C:\Secure\GiggleGarden\...`), this is worth moving out of the repo tree entirely rather than relying solely on `.gitignore`.
8. `UploaderApp/` — published binaries dated Apr–Jul 2024, structurally inconsistent with the current Aug-2026 `gigglegarden-automation` source (see Assumptions above). Likely stale/dead; will confirm and list with evidence in the Phase 2 plan rather than assume here.

I'll fold your answers to these into `tasks/todo.md` in Phase 2. Given your note that this is treated as a new, not-yet-tested project, my default lean (pending your answer on #1) is: **YouTube-only is the real, working product; the multi-platform scaffolding is unfinished work — safe to either finish or delete, your call.**
