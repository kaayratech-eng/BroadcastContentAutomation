# GiggleGarden Automation — Cleanup & Overhaul Plan (Phase 2)

Based on `tasks/understanding.md`. Nothing in this plan has been implemented yet —
this is for your review. See the "Review" section at the bottom for a summary of
what needs your decision.

Your decisions folded in so far:
- **Multi-platform publishing (Instagram/Facebook/TikTok):** finish wiring it up, not delete it.
- **Model IDs (`claude-opus-5`, `claude-sonnet-4-6`):** both real, not bugs — moved to Phase 4 cost doc.
- **Framing:** this is treated as a new, not-yet-battle-tested project. Old/unused code can be deleted freely; no obligation to preserve anything just because it's already there.

---

## A. Bugs / broken automation

### A1. Uploader silently ignores every non-YouTube target VideoGen prepares — [HIGH IMPACT / HIGH EFFORT]
- [ ] Root cause: `Uploader/Program.cs` is a self-contained, YouTube-only pipeline with its own private `record AppConfig` / `class VideoMetadata` / `class Logger`. It never touches `Uploader/AppConfig.cs` (multi-platform config), `Publishing/IPublisher.cs`, or `Publishing/InstagramPublisher.cs` — those files compile but are never instantiated.
- [ ] Consequence: `VideoGen` already writes a `Sidecar` JSON declaring `Targets: ["youtube","instagram","facebook","tiktok"]` for every vertical short, but the Uploader deserializes that JSON into its own unrelated flat `VideoMetadata` type (extra fields silently dropped) and uploads **both** the landscape master and the vertical short to plain YouTube — no Instagram/Facebook/TikTok publishing ever happens, and there's no distinction between the two renders on upload.
- [ ] This is the core rewrite for this pass (see C1 below — same underlying issue, fix is one piece of work).

### A2. `ClientSecretPath` location doesn't match README's stated security convention — [LOW IMPACT / LOW EFFORT]
- [ ] Root cause: root `README.md` instructs storing the OAuth client secret at `C:\Secure\GiggleGarden\client_secret.json`, but `Uploader/appsettings.json` actually points at `D:\Business\Youtube\client_secret_desktop.json` — inside the repo tree (correctly git-ignored, but not the secured location the README describes).
- [ ] Fix: either move the secret file to `C:\Secure\GiggleGarden\` and update `appsettings.json`, or update the README to match reality. Recommend the former — matches the existing stated intent and gets a real secret out of a folder that also holds project data.

---

## B. Dead or unnecessary files

| Item | Evidence | Recommendation |
|---|---|---|
| `UploaderApp/*.dll` (Google.Apis.*, Newtonsoft.Json.dll, System.Management.dll, etc., dated Apr–Jul 2024) | This is the README's designated `dotnet publish` output directory, but the DLLs currently sitting there — including `Newtonsoft.Json.dll` and `System.Management.dll` — aren't referenced by the current `Uploader.csproj` at all. They're leftover from a prior/different build, not from the current source tree. Already git-ignored (never committed). | Delete the contents; they'll be regenerated correctly next time you `dotnet publish`. Low risk — it's local build output, not source. |
| `Claude-API/API Key.txt` | Plaintext API key sitting in the repo directory. Correctly git-ignored (never committed), but the README's own convention (`C:\Secure\GiggleGarden\...`) implies secrets shouldn't live in the project tree at all. | Move to `C:\Secure\GiggleGarden\` or equivalent, outside any git-tracked directory. Security hygiene, not a functional bug. |

Nothing else in `gigglegarden-automation/` looks unused — `ImageClient.StabilityAsync` is a legitimate config-switchable alternate provider (reachable via `ImageProvider: "stability"`), not dead code.

The root-level `.claude-flow/`, `.swarm/`, `node_modules/`, `ruvector.db`, `package.json` — these are the Claude-Flow/Ruflo agent-tooling scaffold (per your global CLAUDE.md), unrelated to the GiggleGarden product. Not a deletion candidate; noted only so it's clear they're a separate concern living in the same repo root.

---

## C. Refactors / rewrites

### C1. Build real multi-platform publishing on top of the existing `Sidecar`/`IPublisher` scaffolding — [HIGH IMPACT / HIGH EFFORT]

This is the big item. Breaking it into the smallest safe steps:

1. [ ] **Wrap the existing YouTube upload logic in an `IPublisher` implementation** (`YouTubePublisher`), so YouTube is handled the same way as every other platform instead of being special-cased inline in `Program.cs`. This is a refactor of working code — no new external dependency, lowest risk step.
2. [ ] **Build `FacebookPublisher`**, following the same shape as the existing `InstagramPublisher` (both ride the Meta Graph API via the shared `MetaGraph` helper already in `Shared`/`Publishing`) — `FacebookConfig` already exists in `AppConfig.cs`, just unused.
3. [ ] **Build `TikTokPublisher`** — no existing code to build on here (only `TikTokConfig` exists); this is genuinely new work, and TikTok's Direct Post API requires an *audited* app (per the existing comment in `AppConfig.cs`) — until that audit passes, publishes land in the creator's TikTok inbox as drafts, not a live post. Flagging this now: **this step needs your TikTok developer app credentials and, eventually, TikTok's audit approval — it can't be fully finished by code alone.**
4. [ ] **Rewrite `Uploader/Program.cs`'s orchestration** to: read the `Sidecar` (not the flat `VideoMetadata`), iterate `sidecar.OutstandingTargets()`, dispatch each to the matching `IPublisher`, and write `Publications` back via `sidecar.SaveAsync()` — moving a video to `\done` only once `sidecar.AllTargetsTerminal()` (this logic already exists in `Sidecar.cs`, just unused).
5. [ ] **Delete the now-redundant duplicate types** in `Program.cs` (`record AppConfig`, `class VideoMetadata`, `class Logger`) once the namespaced versions in `AppConfig.cs`/`Logger.cs` are wired in and doing the real work.
6. [ ] **Wire config**: you'll need to obtain and add to `Uploader/appsettings.json`:
   - Facebook: Page ID + Page access token (Meta app with `pages_manage_posts` / Reels permissions)
   - Instagram: already has config fields (`IgUserId`, `AccessToken`) — needs real values if you want it live
   - TikTok: Client key/secret + a completed OAuth consent flow (similar to `TokenCapture` for YouTube) to get the initial refresh token

**Risk note:** this touches the only file that currently uploads anything, and does so live to YouTube. Recommend building and testing each new publisher in isolation (unit-style, hitting the real APIs with `PrivacyStatus`/equivalent set to draft/private) before wiring the orchestration rewrite (step 4) into the path that already works for YouTube.

### C2. (Optional, low priority) Add a `.sln` file
- [ ] The four projects (`VideoGen`, `Uploader`, `TokenCapture`, `Shared`) currently have no solution file tying them together — each is built/run independently via `dotnet run` in its own directory. Not broken, just a minor convenience gap (no single `dotnet build` for the whole thing, no unified IDE view). Low effort if you want it; skip if you don't care.

---

## D. Cost-reduction opportunities

Written up separately in `tasks/cost-optimization.md` per your instructions — not detailed here. Includes the `claude-opus-5` → smaller-model right-sizing question noted above.

---

## Ranked priority (impact vs. risk)

1. **C1** (multi-platform publishing rewrite) — highest impact (this is the actual point of the multi-platform Sidecar design), highest effort/risk (touches live upload code, needs external credentials you'll have to obtain).
2. **A2** (secret file location) — low effort, no functional risk, closes a hygiene gap.
3. **B** (dead build output / stray key file) — trivial, no risk, do alongside A2.
4. **C2** (`.sln` file) — optional, do last or skip.

---

## Review (Phase 3 complete)

**What changed:**
- **A2** — OAuth client secret moved to `C:\Secure\GiggleGarden\client_secret.json`; `appsettings.json` updated to match.
- **B** — stale `UploaderApp\*.dll` build output deleted; `Claude-API\API Key.txt` relocated to `C:\Secure\GiggleGarden\anthropic-api-key.txt`.
- **C1** — built `YouTubePublisher`, `FacebookPublisher`, `TikTokPublisher` (all `IPublisher`); rewrote `Uploader/Program.cs` to dispatch each video's sidecar `Targets` to the matching publisher, track per-platform `Publications`, respect per-platform run quotas, and only move a video to `\done` once every target is terminal. Removed the old duplicate `AppConfig`/`VideoMetadata`/`Logger` types (superseded by the full rewrite).
- **C2** — added `GiggleGarden.sln`; all five projects (`Shared`, `VideoGen`, `Uploader`, `TokenCapture`, `TikTokTokenCapture`) build clean as one unit.
- **Follow-on work requested mid-implementation:** built `TikTokTokenCapture` (one-time OAuth bootstrap, mirrors `TokenCapture`); added `appsettings.Local.json` support to the Uploader (it didn't have it before — only VideoGen did) so real secrets never need to touch tracked config; wired in the one credential already on hand (Anthropic key); flipped `Platforms.Instagram.Enabled` / `Platforms.Facebook.Enabled` to `true` per your instruction.

**Bugs found and fixed along the way, not in the original plan:**
1. `Uploader.csproj` never had a `<ProjectReference>` to `Shared` — the pre-existing `Publishing/` folder already used `GiggleGarden.Shared` types, so the project would have failed to build the moment anyone tried it, even before this rewrite touched anything.
2. `Uploader/appsettings.json` was still the old flat shape after the `AppConfig` rewrite — the new nested `Platforms.*` config silently wasn't being read (`.NET`'s binder ignores unmatched JSON properties rather than erroring). Rewrote it to match the real `AppConfig.cs` shape; my earlier `ClientSecretPath` fix would otherwise have been quietly inert.

**Verification performed:** full solution builds with 0 warnings/errors. Ran the Uploader against real code paths headlessly — empty-directory handling, the approval-gate hold, and a full approve → dispatch → terminal-state → move-to-`\done` cycle (using a disabled-platform target, so no live network calls) — all behaved correctly. Did **not** attempt a live YouTube/Meta/TikTok publish; that needs your real credentials and live OAuth consent, which the README already designates as a manual step.

**What's still open / needs your action (not something I can do):**
- Run `TokenCapture` once for YouTube (browser sign-in required).
- Fill in Azure Speech + OpenAI (or Stability) keys in `VideoGen/appsettings.Local.json`.
- Fill in Instagram/Facebook Page access token + IDs, and get a background music file at `VideoGen/assets/music.mp3`.
- If you want TikTok live: register a TikTok Developer app, run `TikTokTokenCapture` once, fill in the client key/secret, flip `Platforms.TikTok.Enabled`.
- Run the README's own "Test end-to-end" step (drop one test video, approve it, confirm a real upload) before trusting any of this on a schedule.

**Risks:** the multi-platform rewrite is the one thing that could regress YouTube (the only platform that previously worked). Mitigated by wrapping the existing, working upload logic almost unchanged into `YouTubePublisher`, and by testing the orchestration loop's control flow (approval gate, quota tracking, terminal-state detection) before you run it against a real account. The Facebook/TikTok publishers are implemented against my best understanding of Meta's Video Reels API and TikTok's Content Posting API — neither has been exercised against live credentials yet, so treat their first real run as a test, same as the README already prescribes for YouTube.
