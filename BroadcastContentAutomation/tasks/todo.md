# Plan — new character per video + "Welcome to Giggle Garden!"

Status: **implemented and verified**, except the OpenAI character-drawing step,
which has not been run end to end. See Review at the bottom for what was actually
built, what was verified and how, and what is still untested.

Signed off in conversation: character-folder design approved; OpenAI image
generation is the primary character source; parallel submission (no chaining);
flat 8 scenes.

---

## Part A — channel research — **done**

`tasks/research-kids-channels.md`. 45 videos with full metadata, 90 titles, 28
transcripts, 12 thumbnails inspected directly. WebFetch on YouTube returned
nothing as expected; the agent got there via RSS feeds, `ytInitialPlayerResponse`
scraping, `youtube-transcript-api`, and downloading the thumbnail JPEGs.

Gaps it declared rather than filled in: **no CoComelon transcripts** (subtitles
disabled on 14/15, so all CoComelon pacing/hook-timing claims are simply absent);
**no all-time top videos** (the `sort=p` parameter is silently ignored, so every
"highest-view" figure is scoped to a recent window); no subscriber counts, no CTR
data.

---

## Part B — character model

### What exists today (verified by grep, `bin/` excluded)

| Site | Current behaviour |
|---|---|
| `GenConfig.cs:26` | `CharacterReferencePath` → `assets/gigi-reference.png` (1080x1920, verified) |
| `GenConfig.cs:65` | `CharacterName = "Gigi the Duckling"` |
| `GenConfig.cs:128` | `Validate()` **throws** if the reference image is missing |
| `ScriptGenerator.cs:88` | prompt pins the script to `cfg.CharacterName`, "recurring mascot across every video" |
| `ScriptGenerator.cs:154` | `introText` = translated "Welcome to Giggle **World**!" + skill preview, <110 chars |
| `ScriptGenerator.cs:217` | fallback `IntroText = "Welcome to Giggle World!"` |
| `Program.cs:116` | intro bumper motion prompt — `$"{cfg.CharacterName} waves hello … with one **wing**"` (duck-specific) |
| `Program.cs:148` | **every** scene: `StartFramePath = cfg.CharacterReferencePath` |
| `Program.cs:243` | Vidu resubmit also uses `cfg.CharacterReferencePath` |

Two things worth knowing before signing off:

- `CharacterStyle` and `UseCharacterReference` are **referenced by no code at
  all** — only declared in `GenConfig.cs` and set in `appsettings.json`. They
  belong to the retired static-image path. `ImageClient.cs` is likewise entirely
  unreferenced.
- The comment on `Scene.StartFramePath` ("the previous scene's last frame after
  that") is **stale**. There is no chaining in the code today; `Program.cs:148`
  gives every scene the same fixed frame.

### ⚠️ The one part of the brief I can't implement as written

> "rework the `Scene.StartFramePath` chain so scene 1 establishes the NEW
> character … and its final frame seeds scenes 2-9"

Literal last-frame chaining is **serial**: scene 2 can't be submitted until scene
1's clip has been generated and downloaded. Off-peak Vidu only guarantees
delivery within **48 hours per clip**. Ten chained scenes is therefore up to
**20 days per video** in the worst case, versus ~48 hours today. `Program.cs:137`
records that this is exactly why chaining was abandoned. Chaining also compounds
drift across ten generations.

Vidu's `img2video` endpoint **requires** a start image (`ViduClient.cs:47`), so
"no fixed mascot reference image" cannot mean "no image" — something has to
produce one.

**Proposed instead — per-video generated reference frame:**

1. The script prompt invents the character and returns `characterName` +
   `characterDescription`.
2. `--prep` generates **one** reference image from that description
   (`ImageClient`, currently dead code, re-wired for this), pads it to 1080x1920.
3. Every scene seeds from *that* frame — the existing parallel submission loop,
   unchanged.

This gives a brand-new character each video and perfect within-video consistency,
while keeping submission fully parallel and drift-free. Cost: one image per video
(~$0.04 on `gpt-image-1` medium) and it needs `OpenAiApiKey` set — it is
`PUT-OPENAI-KEY-HERE` in `appsettings.json` right now, so this is a **blocker**
unless you supply a key or want the Stability path instead.

Say the word if you'd rather have real chaining anyway and accept the queue time.

### Sign-off needed: what happens to `CharacterName`

**Option 1 — repurpose to the channel, per-video name comes from the script (recommended)**

- `GenConfig.CharacterName` → deleted; new `GenConfig.ChannelName = "Giggle Garden"`,
  used only for the greeting.
- New `VideoScript.CharacterName` + `VideoScript.CharacterDescription`, generated
  per video, persisted in `script.json`.
- `Program.cs:116` rewritten to use `script.CharacterName` /
  `script.CharacterDescription` — this also removes the hard-coded "one wing",
  which is currently wrong for any non-bird character.
- `GenConfig.CharacterReferencePath` kept as an **optional override**: empty =
  generate a fresh character per video; set = pin every video to that image
  (the escape hatch back to Gigi, and it keeps the existing asset useful).
  `Validate()` only throws when it is non-empty.
- `CharacterStyle` / `UseCharacterReference` left untouched this pass — dead
  either way, and deleting them is a separate cleanup.

**Option 2 — keep `CharacterName` as an optional pin.** Non-empty forces that
character in every video (today's behaviour); empty lets the script invent one.
More config surface, two code paths to keep working, and the "which mode am I in"
question shows up in every future edit.

**Option 3 — deprecate outright.** Delete `CharacterName`, `CharacterStyle`,
`UseCharacterReference`, `CharacterReferencePath` and `ImageClient`'s unused
halves. Smallest surface, but no way back to a fixed mascot without a code change.

### Changes if Option 1 is approved

`ScriptGenerator.cs`
- `VideoScript`: add `CharacterName`, `CharacterDescription` (+ schema entries in
  the JSON block and in `Scene.StartFramePath`'s now-stale comment).
- Replace the `cfg.CharacterName` block (line 88) with an instruction to invent
  one new character — name plus a reusable one-sentence appearance description
  (species, colour, one distinguishing accessory) — for **this video only**.
- Every "same recurring main character across all scenes" phrase (line 124, and
  the motionPrompt rule at 127-134) re-anchored to *that description*, within
  this video. Each `imagePrompt`/`motionPrompt` must name the character.
- `introText` (154-161): first sentence becomes "Welcome to Giggle Garden!"
  naturally translated; second, the character introducing **itself** by name;
  third, today's skill. That is three sentences, not two — see budget below.
- Fallback default (217) → `"Welcome to Giggle Garden!"`.
- Fallbacks for the two new fields so a partial response still renders.

`Program.cs`
- Intro bumper motion prompt (116) → per-video character, no "wing".
- New step between script generation and Vidu submission: render the character
  reference into the job folder, pad to 1080x1920, use it for `StartFramePath`
  (148) and for the resubmit path (243) — the resubmit must read the job's frame,
  not `cfg`, or a resubmitted scene comes back as a different character.
- Reference path persisted in `script.json` so `--assemble` days later still has it.

`GenConfig.cs` / `appsettings.json`
- `CharacterName` → `ChannelName`; `CharacterReferencePath` default emptied;
  validation made conditional.

### Intro budget — this needs a decision too

The prompt claims 110 chars is "a ~4-5 second spoken intro". Measured against
`job-20260809-150322` — real Azure TTS output, 749 characters over 78.6 seconds —
English narration actually runs **9.5 characters per second**. So:

- 110 chars is **~11.6s**, not 4-5s. That comment has been wrong since it was
  written; that job's intro only landed at 5.8s because the model happened to
  write 67 characters, not because anything enforced it.
- 8 content scenes at ~85 chars average ≈ 72s.
- `VerticalMaxSeconds` is 85 in `appsettings.json`, and scenes past it are
  dropped from the vertical cut — so overrunning silently truncates the ending.

**Proposal: exactly 8 content scenes, intro capped at 105 characters (~11s)** →
~83s, inside both the 85s trim and the 90s Reels limit. All three sentences fit
that budget: *"Welcome to Giggle Garden! I'm Milo the fox. Today we're learning
to count to five!"* is 81 characters.

That means dropping "8 or 9 scenes" to a flat 8. If you'd rather keep 9, the
intro has to stay a two-sentence ~45-char greeting and the self-introduction
moves into scene 1's narration instead.

---

## Part C — prompt optimisation from Part A

Applied only where it doesn't break a hard constraint. Constraints that win by
default, with the tradeoff noted in the file if research says otherwise:

- 8 scenes (Reels 90s ceiling, 85s vertical trim — see the intro budget above)
- <115 chars narration/scene (subtitle fit + 10s Vidu clip cap)
- no ALL-CAPS words anywhere spoken (TTS spells them out letter by letter)
- no `#Shorts` in TikTok/Reels captions
- image subject clear of top and bottom thirds (one art set, two crops)

### What to take from Part A

**Take — no conflict with anything:**

1. **Topic selection: public-domain nursery rhyme + novel twist setting.** Every
   one of CoComelon's top 5 in the sampled window is a public-domain rhyme with a
   twist — Humpty Dumpty + Outdoor Chase (16M), Twinkle Twinkle + Shiny Shoes
   (12M), London Bridge + Beach (8.3M). Original concepts cluster at 1-5M. Costs
   nothing, it's pure prompt work. Add to the topic-selection instruction.
2. **Fixed tag block + topical block.** CoComelon appends an identical 12 tags to
   every video; Vlad and Niki do the same with their own 12. Replace the current
   free-form "10-15 tags" with a constant GiggleGarden block plus 5-10 topical
   tags. This is string concatenation, not prompt work — better done in code.
3. **Hashtags near the TOP of the description, line 2**, 7-12 of them, median 10.
   Currently the prompt puts them at the end. Straight swap.
4. **Triple-repetition beats.** `no no no`, `help help help`, `come on come on` —
   28 occurrences of one triple in a single video; immediate word-doubling runs
   1-7.5% of all words. Highly mechanical, ideal for a prompt rule.
5. **Very low vocabulary diversity** — type-token ratio 0.11-0.33. Instruct the
   model to reuse words deliberately rather than reach for variety.
6. **Conflict in the first ~20 seconds**, and it's always possession,
   prohibition, or peril. Sharpens the existing scene-1 hook rule.
7. **Description first line: `Can you...?` / `Let's...` / `It's time to...` /
   `Oh no, ...`** — direct address to the child. 15/15 of CoComelon's.
8. **Thumbnail text: drop it.** All 12 thumbnails inspected carry no caption text
   — only brand logos. One huge open-mouthed face, one oversized object,
   primary-colour saturation. The audience is pre-literate.

**Conflicts — constraint wins, tradeoff noted:**

- **Title length.** CoComelon's median is 86 chars with a fixed brand tail. Our
  cap is 90, so length is fine, *but* a `| Giggle Garden` tail is keyword ballast
  that only pays off when the brand is already a search term. Research says so
  explicitly. **Skip the brand tail**, keep the two-segment `[rhyme] [emoji] |
  [twist setting]` shape.
- **Speech rate.** Vlad and Niki run ~55 wpm — roughly half Ryan's World. We
  can't slow the narrator down without blowing the 85s budget, since Azure TTS
  speed is fixed and scene count is capped. **Keep current pacing.**
- **Video length.** All three run long-form compilations for watch-time (Vlad and
  Niki median ~34 min). Out of reach: we're capped at ~85s by Reels. Noted as a
  possible later move — compilations assembled from already-rendered clips is
  cheap, and it's exactly what Vlad and Niki do.
- **ALL-CAPS emphasis** (`GIANT`, `SQUISHY`) is used by two of the three
  channels. **Cannot adopt in narration** — Azure TTS spells capitalised words
  out letter by letter. Safe in *titles* only, which aren't spoken. Worth using
  there.
- **`#Shorts`** appears in CoComelon's hashtag lines. Still excluded for us on
  TikTok/Reels per the existing rule.

**Not transferable, ignored:** live-action kids, giant physical props (the 18-22M
premises are set-build budgets), franchise licensing, and Ryan's World's empty
metadata — 12/15 empty descriptions and 14/15 untagged only works at 40M subs.

---

## Verification (before anything is marked done)

1. `dotnet build` clean.
2. `script.json` round-trips with the new fields; an old `script.json` without
   them still deserializes (`--assemble` on an existing job must not break).
3. Dry script generation, 2-3 topics × 2 languages, checking: a different
   character each run; intro says "Welcome to Giggle Garden" + self-introduction
   + today's skill; the same character named in every scene's prompts within a
   run; 8 scenes; every narration <115 chars; no ALL-CAPS; no `#Shorts`.
4. Review section appended here — old vs new behaviour.

No render and no publish as part of this. The music pool is still empty and the
last job must not be re-published.

---

## Review

### Decisions taken, against the options above

**Option 1**, with one change: `CharacterReferencePath` was not kept as-is but
generalised into **`CharacterPoolPath`**, which accepts a single image (pin every
video to one character), a folder (pick one per video, the way
`BackgroundMusicPath` already picks a track), or empty (the default — invent and
draw a new character each video). Each pooled image needs a same-named `.json`
beside it giving the character's `name` and `description`, because the script has
to be written about whatever is actually in the picture. `source` and `licence`
fields are read by nothing but exist so a monetised channel can answer where a
character came from.

`CharacterName` was removed from `GenConfig` and replaced by `ChannelName =
"Giggle Garden"`. `CharacterStyle` was kept but rewritten to describe *art style
only* — it no longer says anything about who the character is. `UseCharacterReference`
was left in place: still dead, still out of scope.

Chaining was not implemented, per the ⚠️ section — every scene seeds from one
per-video character frame, submission stays parallel.

### Old vs new

| | Before | After |
|---|---|---|
| Character | one fixed `gigi-reference.png` for every video ever | new one per video, drawn during `--prep` |
| Who decides what it looks like | `appsettings.json` | the script, in `characterDescription` — the picture is drawn *from* the script |
| Words vs artwork | script invented a name/species per video while the art stayed a duckling — they described different creatures | one description drives both |
| Greeting | "Welcome to Giggle World!" | "Welcome to Giggle Garden!", translated per language |
| Intro | greeting + skill | greeting + character introduces **itself** by name + today's skill (3 sentences, ≤105 chars) |
| Intro bumper motion | hard-coded "waves with one **wing**" | built from this video's character; prompt now forbids motions the body can't do |
| Scenes | "8 or 9" | flat 8 (see intro budget) |
| Scene start frames | `cfg.CharacterReferencePath` | `script.CharacterImagePath`, persisted in `script.json` |
| Resubmit after a dropped clip | `cfg.CharacterReferencePath` — would return a *different* creature for a job whose character has since changed | the job's own frame, or a hard error telling you to re-run `--prep` |
| Tags | free-form "10-15" from the model | fixed 12-tag block prepended in code + 5-10 topical from the model, deduped, capped at 30 |
| Hashtags | end of the description | line 2, 7-12 of them |
| Topic selection | open | public-domain rhyme + twist setting preferred |
| Thumbnail | one face + object + caption text | same, minus any instruction to add text (research: 12/12 thumbnails carry none) |
| `ImageClient.cs` | entirely unreferenced dead code | wired up as the character-drawing step |

### Verification — what was actually run

1. **`dotnet build`** — succeeded, 0 warnings, 0 errors.

2. **`script.json` round-trip** — a throwaway harness loaded the built
   `VideoGen.dll` by reflection and exercised the real `VideoScript` type through
   the real `Sidecar.Options`. 15/15 passed:
   - `job-20260809-150322/script.json`, written before these fields existed,
     still deserializes; `Title`, all 9 scenes and every `StartFramePath` survive.
     `CharacterName`/`CharacterDescription` default to `""` and
     `CharacterImagePath` to `null`, so `--assemble` on an in-flight job keeps
     using its per-scene frames and never reaches the new fallback.
   - The three new fields serialize and round-trip.
   - A null `CharacterImagePath` is omitted from the file (`WhenWritingNull`) and
     comes back null rather than the string `"null"` — the resubmit guard depends
     on that.

3. **Dry script generation — 3 topics × 2 languages, 6 real Claude calls.**
   Driven straight into `ScriptGenerator.GenerateAsync`, so no TTS and nothing
   submitted to Vidu — no clip credits spent. Second run, after the fix below,
   passed every hard constraint on all 6:
   - exactly 8 scenes, every time
   - every narration under 115 chars (longest observed 89); intros 75-82 chars,
     inside the 105 budget
   - no ALL-CAPS anywhere spoken; no `#Shorts` anywhere
   - character named in all 8 scene prompts, every run
   - "Welcome to Giggle Garden!" + self-introduction + skill, correctly rendered
     in Hindi and Punjabi
   - 10 hashtags on line 2 every time; 19-21 tags with the base block leading
   - 6 runs → 6 distinct characters (pale-blue tapir, lilac pangolin, grey guinea
     fowl, cream-white yak, pink seahorse, …)

   **One defect found and fixed by this run.** For `hi`/`pa` the model returned
   `characterName` in the native script (`पिपो`, `ਮੀਮੂ`) while writing the English
   scene prompts with a romanization (`Pipo`, `Meemu`) — two names for the same
   creature. `CharacterName` is *only* ever interpolated into English prompts:
   the OpenAI image prompt, and the intro bumper's `MotionPrompt`, which goes
   directly to Vidu. Devanagari in a Vidu prompt gets ignored or garbled. Fixed
   by requiring both fields in Latin script regardless of narration language, and
   documented on the field. Re-ran all 6: all Latin, and the narration still
   spells the name natively (`CharacterName = "Tuku"`, intro says `टुकु`).

### Not verified

- **The character drawing itself has never run.** `CharacterSource.DrawAsync` →
  `ImageClient` → `gpt-image-1` → `VideoAssembler.FitToPortraitAsync` is compiled
  and wired but unexercised, so the blur-pad fit to 1080x1920 and the OpenAI call
  are untested in this pipeline. `appsettings.Local.json` does now hold an OpenAI
  key; a single `--prep` run is what would settle it, and that submits clips and
  spends Vidu credit, so it needs your go-ahead.
- **The pool path** (`CharacterPoolPath` pointing at a folder, sidecar parsing,
  the stable per-job pick) is likewise unexercised — there is no character art to
  point it at yet.
- No render and no publish, as agreed. The music pool is still empty and the last
  job must not be re-published.

### Left for you to decide → both now done

- **Character names can repeat across videos — fixed.** Two of the six runs
  independently produced "Pip" — a pink seahorse and a grey guinea fowl,
  genuinely different characters that happened to share a short name. Fixed with
  `CharacterSource.RecentNames(cfg, workDir)`: scans sibling `job-*` folders'
  `script.json` files (newest first, case-insensitive dedup, skips jobs with no
  `script.json` yet or a malformed one), and `Program.cs` passes that list into
  `ScriptGenerator.GenerateAsync`'s new `avoidNames` parameter — only on the
  invent path (`pooled is null`); pool sidecars already have fixed names, so
  nothing to avoid there. Rendered into the prompt as one line: *"Recent videos
  already used these names - pick a different one: …"*.

  Verified two ways: a standalone reflection harness against 7 synthetic
  `job-*/script.json` files confirmed `RecentNames` excludes the current job,
  dedups "Milo"/"milo" down to the newer spelling, skips the folder with no
  `script.json` and the one with malformed JSON without throwing, respects
  `max`, and returns empty rather than throwing when `WorkDirectory` doesn't
  exist yet — all 6 checks passed. Separately confirmed by reading the built
  prompt that `avoidLine` is interpolated into the invent-path instructions
  exactly where `characterName` is requested.

- **`ThumbnailText` — removed.** Research found 12/12 sampled thumbnails across
  the three channels carry no caption text — just a huge face and an oversized
  object. The property, its prompt instruction, its JSON schema entry, and its
  fallback default are all gone from `ScriptGenerator.cs`.
  `VideoAssembler.BuildThumbnailAsync` no longer takes `text`/`language` — it's
  a straight `scale…crop` to 1280x720 with no `drawtext` overlay, and
  `Program.cs`'s call site was updated to match. Re-ran the `script.json`
  round-trip harness afterward: the pre-existing
  `job-20260809-150322/script.json`, which still has a `"ThumbnailText"` key
  from before this change, still deserializes cleanly (System.Text.Json ignores
  unmapped properties by default) — 15/15 checks still pass.

  In the course of this, found that `ThumbnailPrompt` (the sibling field) is
  itself dead in practice — the model still generates it, but nothing consumes
  it to render a separate image; the real thumbnail source is a frame extracted
  from the finished render. Left untouched — out of scope for this round.

- **`UseCharacterReference`** remains as dead config in `GenConfig.cs` and
  `appsettings.json` — still not touched.

`dotnet build` after both changes: 0 warnings, 0 errors.

---

## Character-pool build-up/mix/reuse — **implemented and verified (code-level)**

Requested: grow a cast of drawn characters over time and reuse them (name +
image) instead of every video being a stranger, once enough exist. You said
"pool of 30-40" and "repeat after 10", which conflicted with "stop once 15-20" —
clarified via three questions before designing: auto-save every drawn character
(no manual curation step); the 15-40 range is a *mixing* zone, not a hard
cutoff at 15 (invent-or-reuse coin flip from 15 up to 40, reuse-only at/above
40); the 10-repeat gap is a rolling cooldown (a character is ineligible until
10 *other* pool characters have been used since its last appearance), not a
one-time rule.

Plan written and approved before coding: `C:\Users\rishi\.claude\plans\gentle-gliding-breeze.md`.

### What changed

All new behaviour only activates when `CharacterPoolPath` is a **directory** —
the two pre-existing modes (empty = always invent; single file = always pin)
are untouched.

- `GenConfig.cs`: three new fields — `CharacterPoolBuildupSize` (15),
  `CharacterPoolMaxSize` (40), `CharacterPoolCooldown` (10) — plus a `Validate()`
  guard that throws if buildup > max (the pool would never reach the mixing
  zone).
- `appsettings.json`: `CharacterPoolPath` set to
  `VideoGen/assets/character-pool` (new, empty, checked-in via `.gitkeep`); the
  three new keys added at their defaults.
- `CharacterSource.cs`:
  - `PoolSize(cfg)` — counts image files directly in `CharacterPoolPath`.
  - `SaveToPool(cfg, script, imagePath)` — copies a just-drawn character into
    the pool under a slug of its name, writes the `.json` sidecar `FromPool`
    already reads; collision-suffixes (`-2`, `-3`, …) rather than overwriting
    if the slug is already taken.
  - `RecentPoolPicks(cfg, currentWorkDir, max)` — same shape as `RecentNames`
    (newest-first, skips missing/malformed `script.json`), but reads
    `CharacterImagePath` and only keeps paths that live under
    `CharacterPoolPath` — the cooldown window, in image-path form.
  - `FromPool` gained an optional `exclude` parameter — filters cooldown
    picks out of the candidate list before the existing stable-hash pick;
    falls back to the unfiltered list if excluding would empty it, rather
    than throwing.
  - `Resolve(cfg, workDir) -> (pooled, addToPool)` — the actual policy:
    below buildup → invent; at/above max → reuse-only (cooldown-filtered);
    in between → 50/50 invent-or-reuse. `Program.cs` doesn't need to know
    the thresholds.
- `Program.cs`: `RunPrepAsync` calls `Resolve` instead of `FromPool` directly,
  and calls `SaveToPool` after drawing when `addToPool` is true.

### Verification — what was actually run

1. `dotnet build` — clean, 0 warnings, 0 errors.
2. Reflection harness against the built `VideoGen.dll` (scratch pool
   folders + synthetic `job-*/script.json` files), **22/22 checks passed**:
   - `PoolSize` counts only image files, ignores non-images, is 0 when
     `CharacterPoolPath` is empty.
   - `Resolve` at pool sizes 0, 5 → always invent; 40, 50 → always reuse,
     never inventing; 15, 25 (mixing zone) → over 200 trials each, both
     invent and reuse outcomes occurred.
   - `RecentPoolPicks` collects only distinct pool-backed `CharacterImagePath`
     values, excludes the current job, excludes a one-off invented image not
     under the pool folder, and respects `max`.
   - `FromPool` with 2 of 3 pool images excluded always picks the remaining
     one (10 trials, different job folders each time, so the stable-hash
     hasher didn't just get lucky once); with the whole pool excluded, falls
     back to picking from the full pool rather than throwing; with no
     `exclude` at all, unchanged existing behaviour.
   - `SaveToPool` round-trips through `FromPool` — name and description
     match what was saved — and a second save under the same character name
     produces a second, distinct file instead of colliding.

### Not verified

- **No live `--prep` run.** `CharacterSource.DrawAsync` (the OpenAI
  `gpt-image-1` call) still hasn't been exercised end to end by anything,
  pool feature or not — this needs your go-ahead separately, since it spends
  Vidu clip credit once submission follows.
- The pool folder is currently empty on disk, so the build-up phase (`size <
  15` → always invent, save each result) hasn't been observed against real
  generated art, only against placeholder files in the scratch harness.
- The mixing-zone coin flip (`Random.Shared.Next(2)`) is deliberately
  non-deterministic run-to-run — confirmed both outcomes occur, not that any
  particular run picks a particular way.

---

## Character pool — first real content — **done**

Plan: `C:\Users\rishi\.claude\plans\gentle-gliding-breeze.md`. The pool folder
held nothing but `.gitkeep` until now; this seeds it with real characters
instead of placeholder test data.

**Gigi the Duckling** — copied from the existing `assets/gigi-reference.png`
(no download, internal asset), with a sidecar description written from direct
visual inspection: yellow duckling, red neckerchief, orange beak, webbed feet.

**Investigation finding, before adding anything else:** you asked for "Gigi
and other characters that were created in first videos." Checked every
character asset in the repo — `gigi-reference.png` and the `character-ref.png`
files in `work/job-20260809-021127` ("Pip the Squirrel") and
`job-20260809-030142` ("Pip the Penguin"). All three are visually the same
duckling. There is no separate squirrel or penguin art — those two job
folders predate the `CharacterName`/`CharacterDescription` schema and are the
exact narration/art-mismatch bug documented elsewhere in this file (script
invents an animal, art stays pinned to Gigi). Did not add "Pip" to the pool
under either name, since a sidecar claiming "squirrel" over duck art would
just re-import that bug into the pool system. Gigi is the only real
first-video asset.

**CC0 downloads — approved and completed.** Three ZIPs, sizes verified via
HEAD request before download, confirmed CC0 on both sites:
- Kenney Animal Pack Remastered — 3,600,298 bytes
- OpenGameArt Cute Teddy Bear Character — 5,127,365 bytes
- OpenGameArt Mascot Bunny Character — 3,403,326 bytes

Extracted to scratch and inspected before anything touched the repo. Kenney's
pack turned out to be round face-only badge icons (checked Round, Square, and
Spritesheet folders — no full-body variant exists), not full-body characters
— a style mismatch against `CharacterImagePath`'s job as a full-body 1080x1920
img2video reference frame. Flagged this; you said the inconsistency is fine
and to pull all of them in. Final 7:

- **Bruno the Teddy Bear**, **Coco the Bunny** — full-body, thick-outline,
  one idle-pose frame each from their animation sets. `source` = the
  OpenGameArt content page, `licence` = "CC0".
- **Baxter the Bear**, **Ellie the Elephant**, **Gerry the Giraffe**,
  **Pandy the Panda** — round face-badge icons from Kenney's Animal Pack
  Remastered (flat colours, thick outline, no body — visually inconsistent
  with the other 5, per above, kept anyway on your call). `source` = the
  Kenney asset page, `licence` = "CC0".

Pool is now 7 characters. Still well under `CharacterPoolBuildupSize` (15),
so `--prep` still always invents+draws today — this only seeds real content
for when the pool starts mixing in reuse. More CC0 characters can be sourced
later ("and more if needed") — nothing further pulled in this pass since no
additional packs beyond the ones already researched/shown were available to
draw from.

### Verification — what was actually run

Scratch harness (`...\scratchpad\jsontest\realpool-check\`, same
reflection-against-`VideoGen.dll` approach as the code-level harness above),
pointed at the **real** `VideoGen/assets/character-pool` folder instead of
synthetic data:
- `PoolSize` reports 7.
- `FromPool` invoked 80 times across distinct job folders (stable-hash pick
  varies by folder name) — every call returned a non-empty name/description
  and an `ImagePath` that exists on disk; all 7 names were reached at least
  once (Gigi the Duckling, Bruno the Teddy Bear, Coco the Bunny, Baxter the
  Bear, Ellie the Elephant, Gerry the Giraffe, Pandy the Panda).

### Not done

- No `--prep` or `--assemble` run — no Vidu/OpenAI credit spent, no video
  generated with the new pool. That's a separate, explicitly-approved step.
- The 4 Kenney face badges are a known style outlier (no body, flat icon
  look) versus the other 3 full-body characters — accepted per your call, not
  a defect, but worth knowing if a rendered video looks visually inconsistent
  when one of those 4 gets picked.

---

## Content formats (educational / rhyme / poem / bedtime / sing-along / counting song) — zero added cost

Plan: `C:\Users\rishi\.claude\plans\gentle-gliding-breeze.md`, approved before
coding. Full brief: expand `ScriptGenerator.cs` beyond the single hardcoded
"educational" prompt into a randomized (weighted, overridable), format-aware
generator — at no added cost (same one Claude call, no music-gen API, no new
TTS provider, no more than 8-9 scenes).

**Research before designing, all read directly:**
- `TtsClient.cs` confirmed Azure SSML supports per-call `rate`/`pitch` (safe,
  voice-agnostic) and `<mstts:express-as style=...>` (voice/language-gated —
  only "cheerful" confirmed live for en/hi, pa has none). Decision: vary
  rate/pitch by format only; leave the existing per-language `style` value
  untouched, since the task's own wording authorizes "voice id, slower rate,
  softer pitch" and an unverified style value risks a failed Azure call.
- `assets/music/` holds only a README today — zero real tracks. This pass adds
  the mood-folder convention + safe fallback, not real sourced audio.
- **Confirmed the delivered YouTube thumbnail is a cropped frame extracted from
  `script.Scenes[0]` (the intro bumper), not from `ThumbnailPrompt`**
  (`Program.cs:339-341`: `ExtractThumbnailFrameAsync(cfg, landscape, grabAt,
  thumbFrame)` where `grabAt` sits inside `Scenes[0]`'s duration).
  `ThumbnailPrompt` re-confirmed dead for image generation, consistent with the
  finding already logged above in this file — it only affects the persisted
  `script.json` text. So the format-aware "calm/cozy bedtime-or-poem thumbnail"
  requirement is actually delivered by making the **intro bumper's** prompt
  format-aware, not by touching `ThumbnailPrompt`.
- `research-kids-channels.md:199` evidences Bedtime/Lullaby directly;
  Sing-Along and Counting Song both independently evidenced in the same doc
  (recurring "Sing-Along" titles; "Ten in the Bed | Count and Sing..." at 10M
  views) and are the task's own suggested extra niches.

### Plan being implemented

- `ContentFormat` enum (`Educational, Rhyme, Poem, Bedtime, SingAlong,
  CountingSong`) in `ScriptGenerator.cs`.
- `VideoScript` gains `Format`, `MusicMood` (model output, closed menu per
  energy class), `NarrationStyle` (set by us from a shared
  `FormatVoiceProfile` table, not model text).
- `GenConfig.ContentFormatWeights` (weighted random pick, overridable via
  `appsettings.json`); `ScriptGenerator.PickFormat` supports a deterministic
  override; `Program.cs` gets `--format <name>`.
- The single hardcoded prompt splits into a base block (universal rules,
  including the Part 5 "always colourful" requirement) plus a per-format
  block with genuinely divergent rules (bedtime explicitly drops hook/joke/
  call-and-response).
- `VideoAssembler.ResolveBackgroundMusic` gets an optional mood-folder lookup
  with fallback to today's flat-folder behaviour, unchanged when no mood
  folder exists.
- `TtsClient.SynthesizeAsync` takes `ContentFormat` and looks up rate/pitch
  from `FormatVoiceProfile`; `style` logic untouched.
- Intro bumper in `Program.cs` becomes format-aware (energetic vs. calm
  phrasing + colour cue) — this is also what fixes the delivered thumbnail's
  mood per format, since it's the source frame.
- Format-aware hashtag/caption examples added to the metadata prompt section;
  `BaseTags` channel-identity block untouched.

Verification section (build, `script.json` round-trip incl. old-shape
compatibility, ~6 dry-run generations spanning formats with explicit
override tests, per-format rule checks, explicit zero-new-external-calls
confirmation) will be appended as a Review here once implemented.

### Review

### Decisions taken, against the plan above

Implemented exactly as planned in all five files (`ScriptGenerator.cs`,
`GenConfig.cs`, `VideoAssembler.cs`, `TtsClient.cs`, `Program.cs`) with one
judgment call not spelled out in the plan:

**Poem and Bedtime got a tighter per-scene character budget than the
115-char default (Poem 70-95, Bedtime 55-80), baked into their prompt
blocks.** Reason: `FormatVoiceProfile` gives Poem `-12%` rate and Bedtime
`-22%` rate versus the baseline `-4%` — slower speech at the same 115-char
budget would push total narration past `VerticalMaxSeconds` (85s) and the
90s Reels ceiling, silently truncating the ending on exactly the two formats
where a soft wind-down line matters most. Fixing pacing/text instead of
adding scenes keeps the change inside the task's own hard constraint
("if a format wants to feel longer, do it with pacing/voice, not more
clips"). Confirmed in the dry run below: Bedtime scenes landed at 57-63
chars, comfortably inside budget.

### Old vs new

| | Before | After |
|---|---|---|
| Format | always "educational", hardcoded in one prompt | `ContentFormat` enum (6 values), weighted-random per run, `--format` override for deterministic runs |
| Prompt | single hardcoded block | base (universal rules) + per-format block with genuinely divergent rules — Bedtime explicitly forbids hook/joke/call-and-response |
| Voice | fixed `-4%`/`+6%` SSML rate/pitch for every video | looked up per-format from one shared `FormatVoiceProfile` table (`ScriptGenerator.cs`, consumed by `TtsClient.cs`) — Bedtime `-22%/-6%`, Poem `-12%/+2%`, energetic formats unchanged at `-4%/+6%` |
| Music | one flat folder, stable-hash pick | model outputs a closed-menu `MusicMood`; `ResolveBackgroundMusic` tries `{BackgroundMusicPath}/{slug(mood)}/` first, falls back to the flat folder unchanged if the mood folder doesn't exist — no real tracks added, folder convention only |
| Intro bumper (also the thumbnail source frame) | one hard-coded "waves hello… bounces happily" phrasing | format-aware: energetic keeps that phrasing, Poem/Bedtime get "waves hello softly… sways gently", both with an explicit colour clause |
| Scene/thumbnail colour | not required by the prompt | every format's base rules require an explicit colourful background phrase in every `imagePrompt`/`motionPrompt`; energetic formats get bright/saturated language, Poem/Bedtime get soft-pastel/warm-dim language — never dull, never monochrome |
| Hashtags | one generic set | per-format examples layered onto the same fixed 12-tag base block (e.g. Bedtime → `#lullaby #bedtimestories`, Rhyme → `#nurseryrhymes #kidssongs`) |
| `script.json` | no format/mood/style fields | `Format`, `MusicMood`, `NarrationStyle` persisted; an old file missing them still deserializes, defaulting to `Format = Educational` (today's exact behaviour) |

### Verification — what was actually run

1. **`dotnet build`** — succeeded, 0 warnings, 0 errors, across all five
   edited files together.

2. **`script.json` round-trip**, reflection harness against the built
   `VideoGen.dll`/`GiggleGarden.Shared.dll` (isolated into its own
   `formattest/` mini-project after the older scratch harness folder turned
   out to have three stale, mutually-conflicting `Program.cs` files left
   over from earlier sessions — fixed by isolating rather than by touching
   that pre-existing scratch structure). All checks pass:
   - `job-20260809-150322/script.json`, written before these fields
     existed, still deserializes; `Format` defaults to `Educational`,
     `MusicMood`/`NarrationStyle` default to `""`.
   - A script with `Format = Bedtime` round-trips `Format`/`MusicMood`/
     `NarrationStyle` intact through `Sidecar.Options`.
   - All 6 `ContentFormat` members round-trip individually.
   - **One assumption in the test itself was wrong, not the code**: I
     expected the enum to serialize under a camelCase *property* name
     (`"format": "bedtime"`), but `Sidecar.Options` only camelCases enum
     *values* (`JsonStringEnumConverter(JsonNamingPolicy.CamelCase)`) —
     there's no `PropertyNamingPolicy`, so property names stay PascalCase
     (`"Format": "bedtime"`), matching every other field in a real
     `script.json` (`"Title"`, `"Description"`, …). Fixed the test's
     expectation, not the production code, once confirmed against a real
     job file.

3. **Dry-run — 6 real `ScriptGenerator.GenerateAsync` calls** (one Claude
   call each, same model/`max_tokens` the pipeline already uses — no new
   cost category), via a reflection harness that also binds the real
   `GenConfig` from `appsettings.json`/`appsettings.Local.json` the same
   way `Program.cs` does. 2 forced via `formatOverride` (Bedtime, Rhyme —
   the two flagged highest-risk), 4 left to the weighted random draw.
   Results: **Bedtime, Rhyme, Rhyme, Educational, Rhyme, Educational** — 3
   distinct formats across 6 runs, both forced overrides landed exactly as
   requested, confirming the override path works independently of the
   weighted draw.

   Per-script checks, all passed:
   - Every one of the 48 scenes across all 6 scripts named an explicit
     colourful background in both `imagePrompt` and `motionPrompt` — bright/
     saturated palettes for Educational and Rhyme runs, soft-pastel/warm-dim
     ("dusty blue", "lavender", "warm amber") for the Bedtime run. No plain,
     dull, or monochrome backgrounds anywhere.
   - **Bedtime** ("a sleepy firefly saying goodnight to the garden"): no
     hook, no joke, no call-and-response anywhere in its 8 scenes; explicit
     wind-down ending ("Lumi closes her eyes. Goodnight, little garden.
     Goodnight, you."); scene narration 57-63 chars, inside the tightened
     55-80 budget; `MusicMood = "soft piano lullaby"`, `NarrationStyle =
     "calm, slow, soft"` — matches `FormatVoiceProfile[Bedtime].Label`
     exactly.
   - **Rhyme** (3 runs, forced + 2 random): every scene is an actual rhyming
     couplet ("waddling by / up in the sky", "shore / one more", "Kip / dip",
     "sea / with me"), repeated chorus line across scenes in the forced run
     ("Quack, quack, quack, five ducks at the beach!"); `NarrationStyle =
     "bright, playful"` every time.
   - **Educational** (2 random runs): both kept the current hook / one funny
     beat / call-and-response shape ("Oh no! Where is my toothbrush?" …
     "Blub, blub, blub! … So silly!" … "Can you brush with me?");
     `NarrationStyle = "bright, lively"`.
   - `MusicMood` stayed inside the intended closed menu for the format's
     energy class in all 6 (calm: `soft piano lullaby`; energetic: `upbeat
     playful`).
   - Scene count was 8 in every run — no format pushed past the existing
     8-9 bound.

4. **Zero new external calls/providers, confirmed explicitly**: the dry run
   made exactly one Claude API call per script (6 calls total for 6 scripts
   — the same ratio production uses), the same model and `max_tokens` as
   before this change. No music-generation API exists in the code path —
   `ResolveBackgroundMusic` only ever reads local files. No new TTS
   provider — `TtsClient.SynthesizeAsync` still calls the same Azure
   endpoint, only the `rate`/`pitch` SSML values it sends now vary by
   format via a lookup table; the `style`/express-as attribute (the one
   piece confirmed live only for "cheerful" on en/hi) is untouched by
   format, exactly per the plan's risk-avoidance decision.

### Not verified

- **TTS and assembly were not exercised in this pass.** The dry run calls
  `GenerateAsync` directly, so `TtsClient.SynthesizeAsync`'s new
  `ContentFormat` parameter and `VideoAssembler`'s new mood-folder lookup
  were verified by build + code inspection, not by a live run — running
  either spends Azure/Vidu credit and needs a separate go-ahead, consistent
  with how every prior phase in this file treated `--prep`/`--assemble`.
- **No real mood-subfolder music tracks exist.** `assets/music/` is still
  empty apart from its README, so `ResolveBackgroundMusic`'s mood-folder
  path has never actually been taken end to end — only its safe fallback
  to the flat folder (unchanged from before) is reachable today.
- ~~SingAlong and CountingSong were not drawn in the 6-run sample~~ — **closed.**
  Added `--dry-script [--topic] [--language] [--format]` (`Program.cs`,
  `RunDryScriptAsync`): one `ScriptGenerator.GenerateAsync` call and nothing
  else — no character draw, no TTS, no Vidu — same zero-spend pattern as
  `--test-tts`, writes to `work/dry-script/` so it can never collide with a
  real `job-*` folder. Ran both forced formats live:
  - **SingAlong** ("five little ducks counting"): repeated chorus line
    ("quack, quack, quack" / "quack, quack, clap, clap"), explicit
    call-and-response ("Can you quack and clap along with me?"), 8 scenes
    60-85 chars, `NarrationStyle = "bright, singable"`.
  - **CountingSong** ("counting balloons at a party"): correct sequential
    count-up 1→8 across the 8 scenes ("that makes one" → … → "hooray for
    eight"), 73-91 chars, `NarrationStyle = "bright, playful"`.
  - Both: 10 hashtags on description line 2, zero ALL-CAPS words, no
    `#Shorts`, character named throughout, `MusicMood` inside the energetic
    menu (`upbeat playful`). All 6 `ContentFormat` members now confirmed
    live end-to-end (script-generation stage).

---

## Part D — Free audio migration + free video/music/character research

Full plan: `C:\Users\rishi\.claude\plans\gentle-gliding-breeze.md` (7 parts,
approved). Goal: stop paying for narration if a free tier/local option covers
it, get an honest sourced answer on free video/music/character-art options,
without adding cost, adding a paid provider, or automating a consumer UI.

### Part 1 — Cost surface map (research, no code)

**Azure TTS** — `TtsClient.SynthesizeAsync` (`VideoGen/TtsClient.cs:33-78`), raw
REST POST to `https://{region}.tts.speech.microsoft.com/cognitiveservices/v1`
with an `Ocp-Apim-Subscription-Key` header and an SSML body. No SDK, no
interface prior to this change — concretely `new`'d at `Program.cs:156`
(`RunPrepAsync`) and `Program.cs:217` (`RunRettsAsync`). 3 languages only
(en/hi/pa, one per run via `--language`), voice/locale table hardcoded at
`TtsClient.cs:12-17`. **9 billable calls/video** (8 script scenes + 1 intro
bumper), every run, on every language.

**Vidu I2V** — `ViduClient` (`VideoGen/ViduClient.cs`), raw REST to
`api.vidu.com/ent/v2/img2video` + `/tasks/{id}/creations` polling + a plain GET
download. No interface prior to this change — `new`'d at `Program.cs:176` and
`Program.cs:247`. Billed per submit-call credits (`ViduClient.cs:93`). **9
billable calls/video**, same cadence as TTS.

**Character consistency** — one fixed reference image per video, reused as
every scene's Vidu start frame (`Program.cs:179-189`), deliberately *not*
frame-chained (the `Program.cs:170-176` comment explains why: parallel-safe
against Vidu's up-to-48h off-peak queue, no compounding drift across scenes).
`VideoAssembler.ExtractLastFrameAsync` exists in the codebase but is dead code
— nothing calls it.

**ffmpeg** — already local-only via `Process.Start` (`VideoAssembler.cs`,
`RunFfmpegAsync` at line 531) for concat/crossfade, audio mux + sidechain
ducking + loudness normalization, portrait-fit, thumbnail extraction. No API
involved, zero marginal cost regardless of how much stitching a future design
needs.

**Net: every video today costs 9 Azure TTS calls + 9 Vidu calls, unconditionally.**
Parts 2/4/6/7 below target the TTS half (fully addressable for free) and survey
the video/music half (not addressable for free without new hardware cost, so
Vidu stays as-is).

### Part 2 — Free audio migration (implemented)

- `ITtsProvider.cs` — new interface, `SynthesizeAsync(text, language, outputPath,
  format)`, matching the old `TtsClient` signature so call sites barely changed.
- `TtsClient.cs` → `AzureTtsProvider.cs` (`git mv`), class renamed to
  `AzureTtsProvider` implementing `ITtsProvider`. **Zero logic changes** — same
  SSML, same endpoint, same voice table. This is what keeps Azure fully
  functional as the mandatory `pa` provider and the forced-fallback path.
- `GoogleTtsProvider.cs` — new. REST POST to
  `https://texttospeech.googleapis.com/v1/text:synthesize`, API-key auth (`?key=`,
  same shape as `GenConfig.YouTubeApiKey`), Standard voices only (`en-US-Standard-C`,
  `hi-IN-Standard-A` — pick a different name in `GoogleTtsProvider.cs:20-21` if
  those don't sound right once you can hear real output; not verified live yet,
  see Part 2 verification note below). SSML `<prosody rate/pitch>` built from the
  same `ScriptGenerator.FormatVoiceProfile` table `AzureTtsProvider` already uses,
  so a format's pacing can't drift between providers. No Google equivalent to
  Azure's `<mstts:express-as style='cheerful'>` — that dimension is simply absent
  on the Google path rather than approximated.
- `TtsProviderFactory.cs` — new. Per-language routing, not a single global switch:
  `en`/`hi` → `GoogleTtsProvider`, `pa` → `AzureTtsProvider` always (no free
  Punjabi voice exists anywhere). `GenConfig.TtsProvider` (`"auto"` default /
  `"azure"` / `"google"`) overrides this — `"azure"` is the zero-risk safety
  switch; `"google"` is testing-only and throws on `pa` rather than silently
  degrading.
- `GenConfig.cs` — added `GoogleCloudTtsApiKey` and `TtsProvider`.
  `Validate()` now requires `GoogleCloudTtsApiKey` whenever routing can reach
  Google (i.e. whenever `TtsProvider` isn't forced to `"azure"`) — **this is a
  real behavior change worth flagging**: since `TtsProvider` defaults to
  `"auto"`, the app will refuse to start on the next run until either a Google
  Cloud TTS API key is added to `appsettings.Local.json`/env, or
  `GIGGLE_TtsProvider=azure` is set to keep running Azure-only exactly like
  before. Azure/Vidu/Anthropic keys already fail this same way today (fail-fast
  at startup with a clear message) — this follows that existing convention
  rather than silently substituting a provider.
- `Program.cs:156` and `Program.cs:217` — swapped `new TtsClient(cfg)` for
  `TtsProviderFactory.Create(cfg, language!)`; the surrounding loops are
  untouched.

### Part 3 — Free video feasibility (report only)

Written to `tasks/free-video-feasibility.md`. Headline: **no-go for now** — Vidu
stays exactly as-is, no code path changed for this part beyond the inert
`IVideoProvider` seam (Part 4). Covers Qwen Chat (disqualified — consumer UI, no
durable free API), Meta AI/Movie Gen/Vibes (disqualified — same reason, see Part 7),
self-hosted Wan 2.2 (the one credible free path, but real GPU-rental cost + ops
burden), LTX-Video, SVD/AnimateDiff, and the bifurcate-to-5s-and-stitch strategy
(worse on consistency and generation count, only wins on being free). Full sourced
recommendation table and explicit go/no-go with revisit triggers in that file.

### Part 4 — `IVideoProvider` seam (implemented, zero behavior change)

`IVideoProvider.cs` — new interface capturing `ViduClient`'s existing
`SubmitAsync`/`PollAsync`/`DownloadAsync` contract verbatim (reuses
`ViduClient.Submission`/`ViduClient.Result` rather than duplicating them).
`ViduClient` now implements it (`ViduClient.cs:17`); `Program.cs:176`/`247`
construct it as `IVideoProvider vidu = new ViduClient(cfg)` instead of the bare
concrete type. No new class, no behavior change — purely a type-level seam so a
future provider (see Part 3's revisit triggers) could be dropped in later without
touching orchestration logic.

### Part 5 — Head-only pool fix + free character-tool research

**Fix (done):** deleted the 4 Kenney.nl "face badge" assets
(`animal-pack-bear/elephant/giraffe/panda`, `.png` + `.json` each) from
`assets/character-pool/` — root cause of the "just heads" bug you flagged. Their
sidecar `.json` descriptions literally said things like *"a round brown bear face
badge... framing the face,"* and that text gets injected into every scene's image
prompt (`ScriptGenerator.cs:610-611,636`), so picking one wouldn't just have reused
a bad image — it would have asked the image model for a face-badge crop on every
subsequent scene too. Not live yet in practice (`CharacterPoolBuildupSize` = 15 >
pool size 7, so `Program.cs:118` was still always inventing new characters) — this
removes the landmine before it would have detonated at video #15. Pool now holds 3
full-body assets: `gigi-the-duckling`, `teddy-bear`, `mascot-bunny` (all verified
full-body/standing by reading their `.json` sidecars).

**Research (report only):** written to `tasks/free-character-tool-shortlist.md`.
Shortlisted Canva, OpenArt, Adobe Firefly, and Picsart (all usable, none default to
full-body output — all require explicit "full body, standing" prompting, same
discipline the invented-character path already follows); ToonyTool ruled out as a
category mismatch (it's a comic-panel composer, not a character generator).
Recommendation: OpenArt first (daily-refreshing free quota, style-reference
support), Canva second. **Nothing generated, downloaded, or added to the pool** —
that shortlist is pending your sign-off on a specific tool before any art is
sourced.

### Part 6 — Google AI (Lyria RealTime) for free background music: research + recommendation

**Findings (sourced, dated Aug 2026):**
- Google DeepMind's **Lyria RealTime** (model id `lyria-realtime-exp`) is a genuine
  programmatic API — a WebSocket session, not a consumer-UI-only tool. Input is
  text "weighted prompts" (mood/genre/instrument descriptors, several blendable at
  once); output is raw 16-bit PCM, 48kHz stereo, **instrumental-only** (no vocals —
  matches the "instrumental-only, very soft" bar already set for this channel's
  music), with up to ~2s control latency for steering prompts live mid-stream.
  ([Model card](https://ai.google.dev/gemini-api/docs/models/lyria-realtime-exp),
  [Realtime music generation guide](https://ai.google.dev/gemini-api/docs/realtime-music-generation).)
- **Free access**: [MusicFX](https://www.geminimusic.org/tools/music-fx), the
  consumer tool built on this same model, is confirmed free with no caps, and the
  [pricing page](https://ai.google.dev/gemini-api/docs/pricing) lists no charge for
  `lyria-realtime-exp` at all — only the separate *batch* "Lyria 3" model is paid
  ($0.04-0.08/song) and explicitly free-tier-inaccessible. `lyria-realtime-exp` is
  labeled "experimental," which in Google's convention means free during preview
  but **not a durable SLA** — could change or be paywalled without notice.
- **Quota is unpublished.** No RPM or session-length ceiling appears on the static
  docs pages; it's only visible in a live AI Studio dashboard once a GCP project
  exists. This can share the same one-time GCP-project-with-billing-enabled setup
  Part 2's Google TTS key already needs — it's a single account-setup step, not two.
- Output carries an inaudible SynthID watermark (Google's standard Responsible AI
  policy) — a non-issue for an ambient background bed.
- **Shape mismatch to solve before this is usable in the pipeline:** Lyria RealTime
  is a *live/interactive* API (steer a continuous stream), not a "POST text, get an
  MP3 back" batch call like the TTS providers. Turning it into a fixed-length
  background track needs a small WebSocket client: open a session, send the mood
  prompt (reusing the same mood vocabulary `VideoAssembler.ResolveBackgroundMusic`
  already derives per format), buffer the PCM stream for the render's duration,
  close the session, hand the buffer to ffmpeg (already in the pipeline) for
  encoding + the existing `-36 LUFS` normalization pass. It would become one more
  input option feeding the existing `BackgroundMusicPath` resolution point, not a
  new pipeline stage.

**Recommendation:** promising enough to prototype, not to commit to blindly — the
free-but-unpublished-quota status needs verifying against a live key before it's
trustworthy, the same way Part 2's Google TTS auth approach needs a first live call
to confirm. Per the standing instruction not to pick/source music unilaterally,
**this stays a recommendation + a proposed prototype step, not shipped code in this
pass.** No `BackgroundMusicPath` behavior has changed. Proposed next step, pending
your go-ahead: a ~15s Lyria RealTime session against a real API key, to confirm
audio quality/mood-steerability and see the actual quota in the AI Studio
dashboard — then decide together whether to build the WebSocket→ffmpeg bridge as a
new source alongside the existing static-track-folder option (additive, not a
replacement — already-licensed/curated tracks stay available).

**Dropped (done).** Built a `--test-lyria` diagnostic (`Program.cs`, `RunTestLyriaAsync`)
that shelled out to a small Node helper (`tools/lyria-test/lyria-fetch.js`, using
Google's official `@google/genai` SDK — no .NET SDK exists for this, and the raw
WebSocket wire format isn't publicly documented) to fetch a short clip and wrap it
into a `.wav`. Ran it against a real Gemini API key: the WebSocket connected fine,
then closed immediately with **code 1011**: *"Your prepayment credits are depleted.
Please go to AI Studio at https://ai.studio/projects to manage your project and
billing."* This directly contradicted the pricing-page research above (no charge
listed for `lyria-realtime-exp`) — in practice the project needs **prepaid billing
credits enabled** before the experimental model will run at all, regardless of
whether per-call usage is later billed. No billing was ever enabled and nothing was
purchased. After review, the call was to **not** enable billing — same disqualified
category as Adobe Firefly's enterprise minimum and Canva's no-API finding, per this
task's "no new paid provider" hard constraint. The entire integration has since been
**fully removed**: `LyriaApiKey` out of `GenConfig.cs`/`appsettings.json`/
`appsettings.Local.json`, `tools/lyria-test/` deleted, and every `--test-lyria`/
`RunTestLyriaAsync` code path pulled out of `Program.cs`. No `BackgroundMusicPath`/
`VideoAssembler` code ever changed — the existing static track-folder remains the
only background-music source. This closes Part 6 with no viable free/agent-safe
option found this pass.

### Part 7 — Meta AI for free 5-10s video clips: research (no code)

Meta's video model, **Movie Gen**, has [no public developer API — Meta has stated
this explicitly](https://ai.meta.com/research/movie-gen/). The only access point is
**Vibes**, a consumer feed inside the Meta AI app/meta.ai (launched 2026). Same
shape as the Qwen Chat finding in Part 3: free to a human, zero programmatic
access — automating it means scripting a consumer UI, which the task's hard
constraint and the standing "no consumer UI automation" rule both forbid. **Verdict:
not usable in an automated pipeline**, same category and reason as Qwen Chat.
Recorded in `tasks/free-video-feasibility.md` alongside the Qwen finding rather
than duplicated in full here; not evaluated further on consistency/hardware axes
since the ToS/automation gate alone is disqualifying.

### Verification (done)

1. **`dotnet build` clean** with `ITtsProvider`/`AzureTtsProvider`/`GoogleTtsProvider`/
   `TtsProviderFactory`/`IVideoProvider` all present — 0 warnings, 0 errors.
2. **Real TTS calls, no Vidu/Claude spend.** Added a small diagnostic command,
   `--test-tts [--language en|hi|pa]` (`Program.cs`, `RunTestTtsAsync`), that
   synthesizes one fixed sample line on every provider reachable for that language
   and writes both outputs to `work/tts-test/` — deliberately skips script
   generation and Vidu submission entirely, so it costs nothing beyond a couple of
   free-tier TTS calls. Ran all three languages against the real APIs:
   - `en`: Azure and Google both succeeded (`azure-en.mp3` 89,856 bytes,
     `google-en.mp3` 51,456 bytes) — sent to you for a side-by-side listen.
   - `hi`: Azure and Google both succeeded (`azure-hi.mp3` 80,352 bytes,
     `google-hi.mp3` 38,208 bytes) — also sent for comparison.
   - `pa`: Azure succeeded (`azure-pa.mp3` 76,320 bytes); Google correctly threw
     `NotSupportedException` ("no free Standard voice for language 'pa'") instead
     of silently producing wrong/degraded audio — confirms the per-language
     routing gap is enforced, not just documented.
3. **Monthly character volume vs. Google's free tier.** Measured real narration
   length from 3 existing `script.json` files (`work/job-20260809-*`): 614, 599,
   and 816 characters per video (7-9 scenes each, intro included) — averaging
   ~677 chars/video, in line with the ~1,000 chars/video estimate in the original
   plan. At that rate, Google's 4M-char/mo Standard-voice free ceiling covers
   roughly **5,900 videos/month** before any charge — several orders of magnitude
   above any realistic publish cadence for this channel. Headroom is not a
   practical constraint.
4. **`pa` routing confirmed** to stay on Azure end-to-end (see #2) — the one
   language that had to keep working exactly as before this change.
5. `tasks/free-video-feasibility.md` and `tasks/free-character-tool-shortlist.md`
   both exist with sourced, dated findings and explicit verdicts (no-go for video;
   OpenArt/Canva shortlisted, nothing sourced without approval, for characters).
6. `IVideoProvider` compiles; `ViduClient` unchanged behaviorally (only the
   interface implementation was added — no method bodies touched).
7. 4 head-only pool assets confirmed gone from `assets/character-pool/`
   (`animal-pack-bear/elephant/giraffe/panda`, 8 files) — pool holds 3 verified
   full-body assets (`gigi-the-duckling`, `teddy-bear`, `mascot-bunny`).

### Not yet done / deferred by design

- **A real `--prep`/`--assemble` run through the full pipeline** (script → TTS →
  Vidu → ffmpeg) was deliberately not run in this pass — that spends real Vidu
  credits, and `--test-tts` already isolates and verifies the actual change (TTS
  routing) without that cost. Worth running once you're ready to greenlight a real
  video and want to see the free-audio path in a finished render.
- **`TtsProvider` set to `"azure"` — your call after listening.** You compared
  `azure-en.mp3` against `google-en.mp3` and preferred Azure. `appsettings.json`
  now has `"TtsProvider": "azure"` explicitly (previously absent, defaulting to
  `"auto"` in code). `TtsProviderFactory.Create` forces `AzureTtsProvider` for
  every language when this is set, regardless of `en`/`hi`/`pa` — same behavior
  as before Part 2 started, just reached through the new interface. Google is
  wired, tested, and free-tier-headroom-verified (~5,900 videos/mo) if you ever
  want to flip `TtsProvider` back to `"auto"` or `"google"` later — no code
  change needed, just the one config value. `dotnet build` clean after the
  change; `TtsProviderFactory`'s `"azure"` branch was already exercised live via
  `--test-tts`'s direct `AzureTtsProvider` calls in the verification above, so
  this is a config flip onto an already-verified path, not new code.
- **Lyria RealTime and the character-tool shortlist stay at the report stage**,
  per the standing instruction not to source music/art unilaterally — no
  `BackgroundMusicPath` or `assets/character-pool/` changes beyond the 4-asset
  deletion.

### Summary — zero new paid providers, no consumer UI automated

- **No new paid provider added.** Google Cloud TTS is used strictly within its
  free tier (Standard voices, ~5,900 videos/month of headroom at current usage);
  the only other new dependency is the inert `IVideoProvider` interface around
  the existing (unchanged) Vidu client.
- **No consumer chat/web UI automated.** Qwen Chat and Meta AI/Vibes were both
  evaluated and explicitly ruled out for exactly this reason.
- **Azure kept fully functional** — `AzureTtsProvider` is a pure rename of the
  old `TtsClient` with zero logic changes, remains the mandatory `pa` provider,
  and is one config flag (`TtsProvider=azure`) away from being the provider for
  every language again if needed.
- **Character pool landmine removed** before it could reach production (4
  head-only assets deleted; the 3 remaining are all verified full-body).
- **Vidu untouched behaviorally** — Parts 3/4 add a report and an interface seam
  only; no video-generation code path changed.

---

## Mythology pivot — Profile #1 (PLAN — not yet implemented, awaiting sign-off)

**Task as given:** commit the codebase to adult-directed (18-34) animated
mythology/history/folklore storytelling as the sole *active* profile, built to
monetize and to stay OUT of YouTube's "Made for Kids" classification; retire
kids content THROUGH the `ContentProfile` seam (not by gutting it); propagate
through every stage; advise separately on accounts. Standing workflow applies:
this is a plan only — **no code changes happen until you review this and say
go.**

**Guardrail restated, so it isn't lost in the size of this section:** don't gut
`ContentProfile`. Make Mythology the sole *active* profile. Keep
`GiggleGardenProfile` registered-but-dormant only if that's genuinely free —
otherwise flag it and propose deleting it outright rather than half-removing
it. No preschool assumptions left scattered in shared code.

### Deliverable 1 — Shared-vs-kids inventory (Step A)

Ran via a research subagent, read-only, against the current (post-refactor)
code. Full method: grepped every pipeline stage for kids/mascot-coded literals
living outside `*Profile.cs` files.

**Bucket 1 — already profile-owned, confirmed isolated.** `AudiencePersona`,
`SafetyFraming`, `TopicGuidance`, `BuildCharacterInventionInstructions`,
`FormatInstructions`/all six per-format blocks, `ContentFormatWeights`,
`BaseTags`, `CharacterStyle`, `CharacterPoolPath/BuildupSize/MaxSize/Cooldown`,
`TrendQueries`, `BackgroundMusicPath/Lufs`, `ChannelName`,
`UsesCharacterMascot` — all in `GiggleGardenProfile.cs`, all cleanly swappable.
`CharacterSource.cs` and `GenConfig.cs` were both re-checked and are clean:
nothing content-identity-shaped remains in either.

**Bucket 2 — shared/hardcoded engine code.** This is what a Mythology profile
would silently inherit today, unwanted, if we only wrote a new `*Profile.cs`
file and nothing else:

| # | Where | What | Why it's a problem for Mythology |
|---|---|---|---|
| 1 | `ScriptGenerator.cs:8-16` | `enum ContentFormat { Educational, Rhyme, Poem, Bedtime, SingAlong, CountingSong }` | The format *names themselves* are kids-shaped. No natural slot for "myth retelling" or "top 5 gods." |
| 2 | `ScriptGenerator.cs:154` + duplicated in `GiggleGardenProfile.cs:94` | `IsCalm(format) => format is Poem or Bedtime` | Duplicated, and the *shared* copy drives a hardcoded fallback (next row) independent of any profile. |
| 3 | `ScriptGenerator.cs:366` | `script.MusicMood = IsCalm(format) ? "soft piano lullaby" : "upbeat playful"` | Hardcoded kids-coded mood literals fire whenever the model omits `musicMood`, for any profile. |
| 4 | `ScriptGenerator.cs:144-152` (`FormatVoiceProfile`) | Rate/pitch/label table keyed by `ContentFormat`, not by profile | Every profile reusing e.g. `Educational` gets identical "bright, lively" prosody. Zero profile seam. |
| 5 | `AzureTtsProvider.cs:15-20,51-53` | Voice table (`en-US-JennyNeural`, etc.) + `express-as style='cheerful'`, hardcoded per language | A mythology narrator in English gets the same young/cheerful voice as GiggleGarden. No profile-level voice override exists anywhere in the TTS stack. |
| 6 | `ScriptGenerator.cs:211,214` | `"Choose ONE fresh topic suitable for ages 2-6. {topicGuidance}"` | Literally injects "ages 2-6" into the prompt independent of `profile.AudiencePersona`. |
| 7 | `ScriptGenerator.cs:270-274` | The base colour rule: *"ALWAYS give it an explicit COLOURFUL background... never plain white, black, grey or empty, and never a single-colour flat void."* | **This is "the colour rule"** you referenced. It structurally forbids the desaturated/shadowy palette a dark-forest-god origin story would legitimately want. `colourGuidance` only ever softens bright→pastel, never colourful→muted. This is why Step D can't just reuse GiggleGarden's per-format colour text. |
| 8 | `ScriptGenerator.cs:282-286` and the fallback at `:401-403` | Every motion prompt forced to end with *"2D cartoon animation, flat colors, thick outlines, character design stays consistent."* | Hardcoded flat-cartoon suffix, unconditional, on every scene and the no-motion-prompt fallback. |
| 9 | `Program.cs:151-165` | Intro bumper: *"waves hello... bounces happily... eyes bright and smiling. Colourful bright background... 2D cartoon..."* | Entirely hardcoded in `Program.cs`, not routed through the profile at all. |
| 10 | `ScriptGenerator.cs:327-336,365` | `"Welcome to {ChannelName}!"` + "energetic greeting" instruction, and the fallback intro text | `ChannelName` is a profile field, but the surrounding "Welcome to X! energetic" phrasing is a literal — a mythology channel may not want a cheerful welcome at all. |
| 11 | `ImageClient.cs:55-56` | Every character reference image prompt force-suffixed *"Children's book illustration, bright cheerful colors, soft rounded shapes..."* | Independent of `profile.CharacterStyle` — would force a children's-book look onto a mythic figure's portrait regardless of what the profile's style text says. |
| 12 | `Uploader/Publishing/YouTubePublisher.cs:47-52` | `SelfDeclaredMadeForKids = true; MadeForKids = true;` — **hardcoded literal, unconditional, for every upload** | **Highest-priority finding.** No `ContentProfile`/sidecar field feeds this at all. Ship Mythology unchanged and every video gets auto-declared Made for Kids: comments/personalized ads/notifications disabled, and it's factually wrong for an 18-34 audience — the exact outcome this task exists to avoid. |
| 13 | `Uploader/Program.cs:232-234` + `Uploader/AppConfig.cs:26-27` | Fallback SEO-metadata prompt ("nursery-rhyme and learning videos for young children") + hardcoded kids `TrendQueries`, used only when a video has no sidecar | Low risk (normal pipeline always writes a sidecar) but would badly mis-describe an orphaned mythology upload. Cheap to generalize alongside #12. |

Lower-severity, no action needed: "no ALL-CAPS in narration" / "no `#Shorts`"
are generic platform-formatting rules, not kids-specific — leave as shared.
Thumbnail *composition* text is already correctly profile-owned via
`thumbnailGuidance`.

### Deliverable 2 — Generalize `ContentFormat` into a profile-owned set (Step B)

Replace the shared `enum ContentFormat` with a profile-owned catalog, so a
profile defines its own formats instead of picking from a fixed kids-shaped
enum:

```csharp
// ContentProfile.cs — replaces the ContentFormat enum + FormatInstructionSet's
// current shape
public sealed record ContentFormatDef(
    string Id,                    // profile-chosen, unique per profile, lowercase
    int Weight,                   // same weighted-random mechanism as today
    bool IsCalm,                  // replaces the duplicated IsCalm() switch
    string Rate, string Pitch,    // SSML prosody — replaces FormatVoiceProfile
    string NarrationStyleLabel,   // replaces FormatVoiceProfile's 3rd tuple element
    string DefaultMusicMood,      // replaces the hardcoded "soft piano lullaby"/"upbeat playful" fallback
    FormatInstructionSet Instructions);

public required IReadOnlyList<ContentFormatDef> Formats { get; init; }
```

- `ScriptGenerator.PickFormat(ContentProfile profile, string? overrideId)` —
  weighted-random over `profile.Formats`, or an exact-Id match on `--format`,
  throwing on an unknown Id (same "throw, don't silently fall back" pattern
  `ContentProfileRegistry.Get` already established for `--profile`).
- `VideoScript.Format` changes from the `ContentFormat` enum to a plain
  `string` (the format Id).
- **Backward compatibility trick, so old `script.json` files keep working with
  zero compatibility code:** `GiggleGardenProfile`'s new format Ids are chosen
  to be byte-identical to the old enum's camelCase serialization
  (`educational`, `rhyme`, `poem`, `bedtime`, `singalong`, `countingsong`). An
  in-flight `script.json` with `"Format": "bedtime"` deserializes into the new
  `string Format` field with the same value and still resolves to the same
  `ContentFormatDef` — no migration shim needed.
- `AzureTtsProvider`/`GoogleTtsProvider` stop doing
  `ScriptGenerator.FormatVoiceProfile[format]` lookups internally; the caller
  (`Program.cs`) resolves the matching `ContentFormatDef` from the profile and
  passes `Rate`/`Pitch` straight through. This deletes the TTS providers' only
  remaining dependency on the format enum.
- `IsCalm()` as a free function is deleted; call sites read
  `formatDef.IsCalm` directly (`Program.cs`'s intro-bumper branch, the mood
  fallback).
- `ScriptGenerator.cs`'s hardcoded `"Choose ONE fresh topic suitable for ages
  2-6."` (Bucket 2 #6) is deleted; `profile.AudiencePersona` /
  `profile.TopicGuidance` already carry the audience framing, so the literal
  age range was always redundant with them, not just kids-specific.

### Deliverable 3 — Mythology profile (Profile #1) + per-stage propagation (Steps C & D)

**New `ContentProfile` fields needed** (additive — `GiggleGardenProfile` gets
these set to today's exact hardcoded text, verbatim, so its behavior doesn't
change):

| New field | Replaces (Bucket 2 #) | GiggleGarden value | Mythology value |
|---|---|---|---|
| `bool MadeForKids` | #12 | `true` | `false` |
| `string ColourRule` | #7 | today's "ALWAYS COLOURFUL... never grey" text, verbatim | permits desaturated/shadowy/moody palettes — see below |
| `string ArtStyleSuffix` | #8 | `"2D cartoon animation, flat colors, thick outlines, character design stays consistent."` | `"painterly illustrated animation, dramatic lighting, semi-realistic proportions, character design stays consistent."` |
| `Func<...> BuildIntroBumperMotionPrompt` | #9 | today's "waves hello... bounces happily" text, verbatim | a cinematic direct-address beat, no bounce/wave-with-a-wing energy |
| `string IntroGreetingInstruction` + fallback | #10 | "Welcome to {ChannelName}!" energetic, verbatim | a one-line cold-open hook naming tonight's story, not a "welcome" |
| `string CharacterPortraitStyleSuffix` | #11 | `"Children's book illustration, bright cheerful colors, soft rounded shapes..."`, verbatim | `"digital painting, dramatic chiaroscuro lighting, semi-realistic anatomy, period-accurate attire, no text, no words, no letters."` |
| `string? TtsExpressAsStyle` (nullable) | #5 (style half) | `"cheerful"` | `null` — no Azure express-as; **needs a live `--test-tts` check for whether the chosen adult voice supports any style tag at all, not asserted here** |
| `Dictionary<string,string> VoiceOverride` (per language) | #5 (voice half) | today's Jenny/etc. table, verbatim | a warmer/older-sounding narrator voice — **candidate only, unverified**: something in Azure's `en-US`/`en-GB` neural catalog documented as documentary/narration-style (e.g. a Guy/Davis/Ryan-class voice). Must be confirmed live against Azure's actual current voice list before it's hardcoded — Azure's catalog changes, and I'm not going to assert a specific voice name exists without checking. |

**`MythologyProfile.cs` — full definition:**

- `Id = "mythology"`; `ChannelName` — **not decided here**, needs your call
  (ties into Deliverable 5's branding question) — placeholder only.
- `UsesCharacterMascot = true`. **This is a load-bearing decision, not a
  default I picked freely:** `UsesCharacterMascot = false` makes
  `ScriptGenerator.GenerateAsync` throw immediately (`ScriptGenerator.cs:193-198`)
  and abort the *entire* pipeline past that point — TTS, Vidu, assembly, all of
  it — because the faceless path was explicitly never built (per the original
  approved plan). So Mythology reuses the character mechanism, repurposed: not
  a cute mascot animal, but a rotating painterly-illustrated figure drawn from
  whichever myth/history story that video tells (a god, a hero, a monarch,
  a spirit) — new per video, same as GiggleGarden's per-video invention, just a
  different `CharacterStyle`/portrait suffix. If you actually want a
  narrator-only, no-character-on-screen format, that requires implementing the
  `UsesCharacterMascot = false` path for real — out of scope for this pass,
  flag it if you want it instead.
- `AudiencePersona` — *"You write original narrated scripts for an animated
  YouTube channel retelling mythology, history, and folklore for an adult
  audience (18-34), US/UK-weighted English."*
- `SafetyFraming` — adult-directed, explicitly not for children: permits
  mature themes (death, war, betrayal, tragedy) but stays non-graphic/non-gory
  for advertiser-friendliness; explicitly avoids child-directed appeal signals
  (sing-song cadence, primary-color mascot cheerfulness) since YouTube's
  Made-for-Kids classifier looks at more than the self-declared flag —
  content/character-design/music signals matter too, not just the API field.
- `TopicGuidance` — Greek/Norse/Egyptian/world mythology retellings,
  historical origin stories, folklore "what really happened" explainers.
- `Formats` (replaces the six kids formats entirely for this profile):
  - **RetellingArc** — dramatic narrative retelling of a myth/legend (calm/moody register available)
  - **WhatIf** — speculative "what if history had gone differently"
  - **TopFive** — listicle ("5 gods who...", "5 myths that predicted...")
  - **Explainer** — the real-world/historical roots behind a myth
  - **MythBust** — myth vs. historical fact
  
  Each needs its own weight, `IsCalm`, rate/pitch, narration-style label, and
  default music mood — mirroring the density of GiggleGarden's per-format
  blocks (content rules, colour guidance, thumbnail guidance, intro third,
  music menu, hashtag examples). Not fully drafted here since it's a lot of
  prompt-writing best done alongside implementation, not upfront in the plan —
  flag if you want the full prose drafted before I start coding.
- `BaseTags` — mythology/history/folklore/ancient-civilizations/storytelling
  block, mirroring GiggleGarden's fixed-12-tag pattern.
- `CharacterStyle` — *"Painterly illustrated adult storytelling style,
  semi-realistic proportions, dramatic lighting, period-appropriate
  clothing/architecture — not cute, not cartoon, not flat-color-thick-outline."*
- `CharacterPoolPath` — a new folder (e.g.
  `assets/character-pool-mythology`), starts empty. `BuildupSize/MaxSize/Cooldown`
  — propose keeping the 15/40/10 defaults unless you'd rather tune them
  smaller for a slower-growing adult cast; not a strong opinion either way.
- `TrendQueries` — e.g. "mythology explained", "greek mythology story",
  "norse mythology animated", "history storytelling shorts".
- `BackgroundMusicPath` — a new mood-folder set (tense / epic / somber /
  mysterious — cinematic/atmospheric, not nursery), still entirely unsourced
  (per [[gigglegarden-background-music]] memory — this doesn't change that,
  it just retargets the eventual mood). `BackgroundMusicLufs` — GiggleGarden's
  -36 was tuned specifically for "a narrator aimed at 2-6 year olds"; an adult
  documentary-style narrator's loudness profile will differ, so this needs a
  real measurement once the new voice is chosen, not a copy-pasted number.
- `MadeForKids = false`.

**Per-stage propagation, standing constraints respected:**

- **Script** — one Claude call per script, unchanged. Only prompt *content*
  changes (persona, safety framing, topic guidance, colour rule, art-style
  suffix, intro instructions, format catalog).
- **Image (character portraits)** — `ImageClient.BuildPrompt` takes
  `profile.CharacterPortraitStyleSuffix` instead of the hardcoded children's-
  book literal.
- **Audio/TTS** — same Azure/Google providers, no new paid provider. Only the
  voice name, express-as style, and per-format rate/pitch vary by profile now
  (all flagged unverified above where they need a live check).
- **Video/Vidu** — character-consistency mechanism (one reference frame per
  video, parallel scene submission) is completely unchanged — only the *style
  text* baked into motion prompts becomes profile-owned (`ArtStyleSuffix`,
  `ColourRule`, `BuildIntroBumperMotionPrompt`).
- **Metadata** — `BaseTags`/hashtag examples already profile-owned via the
  per-format instructions; `MadeForKids` threads from `ContentProfile` → a new
  `Sidecar` field VideoGen writes → `Uploader`'s sidecar model →
  `YouTubePublisher.cs:47-52` reads it instead of the hardcoded `true`,
  **defaulting to `true` when absent** so any already-queued/older sidecar-less
  jobs keep today's safe behavior.
- **Scene count / ~90s bound** — not touched by this plan. If Mythology's
  slower narrative pacing wants more than 8 scenes or a longer runtime, that's
  a cost decision to make explicitly with you, not something to slip in here.

### Deliverable 4 — Migration order (keeps `--prep`/`--assemble`/`script.json` runnable at every step)

1. **`MadeForKids` wiring first**, independent of everything else below —
   add the field, set `GiggleGardenProfile.MadeForKids = true` (behavior
   unchanged), thread it through the sidecar into `YouTubePublisher.cs`. Build
   + verify. This is the safety-critical fix and shouldn't wait on the rest.
2. **Generalize `ContentFormat`** (Deliverable 2) — enum → profile-owned
   `Formats` list, GiggleGarden's Ids kept identical to old spellings. Build +
   re-run the existing `--dry-script` regression across all 6 GiggleGarden
   formats to confirm zero behavior change.
3. **Extract the remaining Bucket 2 items one field at a time**
   (`ColourRule`, `ArtStyleSuffix`, `BuildIntroBumperMotionPrompt`,
   `IntroGreetingInstruction`, `CharacterPortraitStyleSuffix`,
   `TtsExpressAsStyle`, `VoiceOverride`) — each time setting
   `GiggleGardenProfile`'s value to the exact current hardcoded text, verbatim,
   then rebuilding/re-verifying before moving to the next field. By the end of
   this step, GiggleGarden's actual output is provably unchanged and every
   Bucket 2 item is profile-owned.
4. **Write `MythologyProfile.cs`** with real values (Deliverable 3) and
   register it in `ContentProfileRegistry`. Purely additive — nothing
   consumes it yet, so this can't break GiggleGarden.
5. **Decide `GiggleGardenProfile`'s disposition** (see sign-off below) —
   done *after* step 3, once we know from real experience whether "dormant"
   is actually free or whether some field resisted clean dual-purposing.
6. **Flip the active default** — `appsettings.json`'s `"Profile"` key and
   `GenConfig.Profile`'s default move from `"gigglegarden"` to `"mythology"`.
   Smallest, most reversible step, saved for last on purpose.
7. **Verification only, no spend** — `--dry-script` across all 5 Mythology
   formats, `--test-tts` against the new voice, `script.json` round-trip,
   confirm `MadeForKids: false` persists through to a would-be sidecar. A real
   `--prep`/`--assemble` cycle needs your explicit go-ahead separately, same
   as every prior costed step in this file.

### Sign-off needed: `GiggleGardenProfile`'s disposition

Per your guardrail, this is your call, not mine to make silently. My read
after designing the above: **keep it dormant, registered but unused** — once
step 3 fully extracts Bucket 2, GiggleGarden costs nothing extra to keep
around (that's the entire point of doing steps 2-3 before writing Mythology).
No half-removed state, no dead code scattered through shared paths — it just
sits in the registry as a second data file nobody points `--profile` at by
default. If step 3 turns up a field that genuinely resists clean dual-purposing
(hasn't happened in the design above, but implementation sometimes finds
things design doesn't), I'll flag it and bring back a delete recommendation
instead of quietly working around it.

### Deliverable 5 — Accounts recommendation (Step E)

**Recommendation: fresh accounts, leaning strongly, but with real
uncertainty flagged below rather than asserted as fact.**

Reasoning:
- **Classification/algorithm history.** Every video already uploaded from the
  3 existing accounts was published with `MadeForKids: true` (per Bucket 2
  #12 — that's been unconditional for every upload to date). Mixing a
  Made-for-Kids upload history with new 18-34 content on the same channel
  muddies the channel-level audience signal right when the new content needs
  a clean read. I can't verify how strongly YouTube's classifier weights past
  video history vs. new uploads — flagging that as unverified, not asserting
  a specific mechanism.
- **Branding mismatch.** A "Giggle Garden"-branded handle/avatar/banner built
  around a duckling mascot is a flat mismatch for mythology/history
  storytelling. At ~zero views there's nothing valuable in the existing brand
  to preserve.
- **Monetization progress.** At ~zero views, none of the 3 accounts have
  plausibly crossed any platform's monetization threshold, so starting fresh
  sacrifices nothing there. (I'm not going to state YouTube Partner Program's
  exact current numeric threshold here — it's changed before and I'd rather
  you or I verify it live than I assert a figure from memory.)
- **Cross-platform consistency.** `appsettings.json`'s `VerticalTargets`
  (instagram/facebook/tiktok) plus `LandscapeTargets` (youtube) means 4
  platforms — fresh accounts let all 4 launch under one consistent new
  identity at once, instead of 3 mismatched legacy handles plus one new one.

Weaker case for repurposing: account age as a very mild trust signal on some
platforms (not something I can verify or quantify), and avoiding the
operational overhead of new signups/verification. Given ~zero views, this
doesn't outweigh the classification/branding case above.

**Checklist once accounts are decided** (generic items; platform-specific UI
locations flagged where I haven't verified them live):
- [x] Channel/page name + handle, distinct from the GiggleGarden brand —
  **done**, accounts created on all 4 platforms (YouTube, Instagram,
  Facebook, TikTok).
- [x] Avatar/banner/profile art in the new `CharacterStyle` (painterly, not
  cute cartoon) — **done**, `assets/branding/chronicle-and-chaos/`
  (avatar.png/banner.png) uploaded to the new accounts.
- [x] About/bio copy: mythology/history storytelling, general/adult audience
  — **done**, written and applied by you directly (not drafted by me).
- [x] YouTube: channel-level "not made for kids" default in Studio settings
  (separate from the per-video flag fixed in this plan) — **done**.
- [ ] YouTube: content category matched to the genre (Education vs.
  Entertainment) — **not confirmed yet**.
- [ ] AI-generated-content disclosure — YouTube (and likely TikTok/Meta) have
  disclosure requirements for synthetic media that have changed recently —
  **not confirmed yet; needs a live check against current policy before your
  first upload**, not something I'll assert a specific toggle/label for
  without checking.
- [ ] Default upload settings (privacy, comments, category) reset fresh per
  platform, not inherited from GiggleGarden's defaults — **not confirmed
  yet**.
- [ ] A content-rating self-check given mature themes (death, war, betrayal)
  — **not confirmed yet**; aim to stay unrestricted where possible, but
  review each platform's actual current guidelines rather than assuming.

**(2026-08-16) Confirmed by you directly**: accounts exist on all 4
platforms, avatar/banner applied, bio copy written, YouTube's channel-level
"not made for kids" is set. The 4 unchecked items above (content category,
AI-disclosure check, per-platform upload defaults, content-rating
self-check) are the only parts of Deliverable 5 still open — everything else
is done.

**Open questions I need from you before this can be finalized (not
guessable, not something I'll invent):**
1. Current subscriber/view/upload counts on each of the 3 existing accounts,
   and which of the 4 platforms each one is actually on.
2. Whether any of the 3 has a monetization application in progress (YPP,
   TikTok Creator Rewards, etc.) — that's the one thing that would meaningfully
   argue for repurposing instead of starting fresh.
3. One unified handle across all 4 platforms, or can it differ per platform?
4. Final channel/brand name — I can propose candidates once you want them,
   but I'm not picking one unilaterally.
5. Do the 3 existing GiggleGarden accounts get kept dormant for a possible
   future kids relaunch, or wound down entirely? (Affects whether "start
   fresh" has any real cost.)

---

**(Historical, kept for context) Original note before Deliverable 5's account
work started:** "Nothing above has been implemented. Per the standing
workflow, this is where I stop for your review..." — superseded by the
2026-08-16 confirmation above; account setup is done except the 4 unchecked
items.

---

### Progress log

**You approved starting implementation** ("start implementing... keep old
code aside for future and not completely remove kids script and workflow...
as of now I will not generate videos that do not follow money") before every
open question above was answered — so work began on the two migration steps
that are purely mechanical and don't depend on any of your answers, per
Deliverable 4's ordering.

- **Step 1 — `MadeForKids` wiring — done, build-verified.** Added
  `ContentProfile.MadeForKids` (required, no default — every profile must say
  this on purpose), set `GiggleGardenProfile.MadeForKids = true` (zero
  behavior change), added `Sidecar.MadeForKids` (defaults `true` for
  backward-compat with any sidecar written before this field existed),
  threaded `profile.MadeForKids` through both `Sidecar` constructions in
  `VideoGen/Program.cs`, and switched `YouTubePublisher.cs:47-52` from the
  unconditional hardcoded `true` to `request.Sidecar.MadeForKids`. Confirmed
  via `dotnet build` on both `VideoGen.csproj` and `Uploader.csproj` — 0
  warnings, 0 errors, both projects.
- **Step 2 — generalize `ContentFormat` into a profile-owned `Formats` list —
  done, build-verified.** Replaced the shared `enum ContentFormat` plus the
  `FormatVoiceProfile`/`ContentFormatWeights` tables with a new
  `ContentFormatDef` record (`ContentProfile.cs`) and a `Formats:
  IReadOnlyList<ContentFormatDef>` field each profile owns outright.
  `VideoScript.Format` is now a plain string (the format's `Id`).
  `ScriptGenerator.PickFormat` resolves either an explicit `--format <id>`
  (exact match, case-insensitive) or a weighted random draw from
  `profile.Formats`. `ITtsProvider.SynthesizeAsync` now takes `rate`/`pitch`
  strings directly instead of a `ContentFormat`, so `AzureTtsProvider`/
  `GoogleTtsProvider` no longer know anything about formats or profiles —
  `Program.cs` resolves the `ContentFormatDef` and passes `Rate`/`Pitch`
  straight through at every call site (`--prep`, `--retts`, `--test-tts`).
  `GiggleGardenProfile.cs` rewritten with all 6 formats' weights, rate/pitch,
  labels and full per-format prose preserved verbatim; Ids are `educational`,
  `rhyme`, `poem`, `bedtime`, `singAlong`, `countingSong` — matching exactly
  what the old enum already serialized as under `Sidecar.Options`'
  `JsonStringEnumConverter(CamelCase)`, confirmed by reading the converter
  config directly rather than assuming (the reasoning that led here found the
  doc's original claim of all-lowercase `singalong`/`countingsong` was
  wrong — CamelCase only lowercases the first letter). Confirmed via `dotnet
  build` on both `VideoGen.csproj` and `Uploader.csproj` — 0 warnings, 0
  errors — then verified zero behavior change by running `--dry-script`
  against all 6 formats: titles, per-format tone/pacing, `MusicMood` and
  `NarrationStyle` all match what the old hardcoded switch produced.
- **Step 3 — extract remaining Bucket 2 fields into `ContentProfile` — done,
  build-verified.** Added 7 new required fields to `ContentProfile.cs`:
  `ColourRule`, `ArtStyleSuffix`, `BuildIntroBumperPrompts` (builds the intro
  bumper's image+motion prompt pair, given character name/description and
  whether the resolved format is calm), `BuildIntroGreetingInstruction`,
  `BuildFallbackIntroText`, `CharacterPortraitStyleSuffix`, and `VoiceOverride`
  (per-language Azure locale/voice/style table — combines the doc's
  `TtsExpressAsStyle`+`VoiceOverride` into one, since they were always coupled
  per-language in `AzureTtsProvider`'s existing shape anyway). Set all 7 on
  `GiggleGardenProfile.cs` to today's exact hardcoded text/behavior, verbatim.
  Updated every consumer to read from the profile instead of a hardcoded
  literal: `ScriptGenerator.cs` (colour-rule sentence, both `ArtStyleSuffix`
  occurrences, the intro-greeting instruction sentence, the fallback intro
  text), `Program.cs`'s `RunPrepAsync` (intro-bumper `Scene` now built via
  `profile.BuildIntroBumperPrompts(...)` instead of an inline hardcoded
  wave/bounce block), `ImageClient.cs`'s `BuildPrompt`/`GenerateAsync`/
  `GenerateWithReferenceAsync` (new `portraitStyleSuffix` parameter replacing
  the hardcoded "Children's book illustration..." literal — threaded through
  `GenerateWithReferenceAsync` too for consistency even though it's confirmed
  dead code, never called), `CharacterSource.cs`'s `DrawAsync` (passes
  `profile.CharacterPortraitStyleSuffix`), and `AzureTtsProvider.cs` (the
  static `Voices` dictionary is now an injected constructor parameter, sourced
  from `profile.VoiceOverride` at every call site via `TtsProviderFactory.Create`,
  which now takes a `profile` argument). `GoogleTtsProvider`'s own voice table
  and `TtsProviderFactory`'s "auto" en/hi-to-Google routing policy were
  deliberately left untouched — out of Bucket 2 #5's scope; that policy
  question belongs with Step 4 or Step 7. Confirmed via `dotnet build` on both
  `VideoGen.csproj` and `Uploader.csproj` — 0 warnings, 0 errors. Every
  substitution was checked by hand against the original source text and is
  byte-identical (or, for `ColourRule`, identical content with line-wrap
  newlines collapsed to spaces — not a meaningful difference to a text
  prompt). **Live `--dry-script` re-verification could not be completed**:
  the configured Claude API key has zero credit balance ("Your credit balance
  is too low..."), and no `GroqApiKey` is set in `appsettings.Local.json` to
  fall back to the free `GroqScriptProvider` seam instead. Confidence rests on
  the clean build plus the verbatim manual diff, not on live output — rerun
  `--dry-script` across all 6 formats once either the Claude account is
  topped up or a Groq key is added, to close this out the same way Steps 1-2
  were closed out.
- **Step 4 — write `MythologyProfile.cs` for Chronicle & Chaos — done,
  build-verified.** New file, id `"chronicleandchaos"`, `MadeForKids = false`,
  `UsesCharacterMascot = true`. Persona/safety framing pitch the channel as an
  adult (18-34) mythology/history/folklore documentary-storytelling show,
  explicitly not-for-kids, mature themes allowed but non-graphic. 5 formats
  (mirroring `GiggleGardenProfile.cs`'s `BuildFormats`/`BuildFormat` pattern,
  with one deliberate deviation — `BuildFormat` here takes an explicit
  `defaultMusicMood` parameter rather than deriving it from `isCalm`, since
  Mythology wants 5 distinct default moods rather than a 2-way split):
  `retellingArc` (35, calm, grave/cinematic), `whatIf` (15, urgent/speculative),
  `topFive` (20, brisk/punchy), `explainer` (20, clear/documentary), `mythBust`
  (10, wry/contrarian) — each with its own `contentRules`/`introThird`/
  `hashtagExamples`. `BuildCharacterInventionInstructions` has the invented
  figure be the story's actual historical/mythological protagonist (not an
  invented narrator mascot), named in Latin-alphabet form. **`CharacterPoolPath
  = ""`** is deliberate, not a placeholder: GiggleGarden's pool-pick hashes the
  job folder name, independent of story topic, which would be a correctness
  bug here (a Zeus video could draw "Anansi" art from an unrelated pooled
  character) — every Mythology video always invents and draws its own figure
  instead. `VoiceOverride` uses Azure `en-US-AriaNeural` with the
  `narration-professional` express-as style. Registered in
  `ContentProfileRegistry.cs` alongside `GiggleGardenProfile.Value`, which
  stays registered and unmodified — this resolves your earlier answer to keep
  the 3 existing GiggleGarden accounts dormant rather than removed. Created
  `assets/music-mythology/` (with a README adapted from `assets/music/`'s)
  with 6 mood subfolders matching the 5 formats' music menus
  (`somber-orchestral`, `ancient-drone`, `tense-atmospheric`, `driving-epic`,
  `curious-ambient`, `mysterious-tense`); `BackgroundMusicLufs` is a `-30.0`
  placeholder pending a real measurement pass against `en-US-AriaNeural`
  narration. Also fixed a stale comment in `AzureTtsProvider.cs` (referenced
  only "cheerful" as a verified express-as style; now also references
  `narration-professional`/`en-US-AriaNeural`). Confirmed via `dotnet build`
  on both `VideoGen.csproj` and `Uploader.csproj` — 0 warnings, 0 errors.
- **Step 5 — decide `GiggleGardenProfile`'s disposition — resolved, no code
  change needed.** Per your earlier answer ("keep dormant, recommended"),
  `GiggleGardenProfile.Value` stays registered in `ContentProfileRegistry.All`
  alongside Mythology, unmodified and reachable via `--profile gigglegarden`,
  just no longer the default.
- **Step 6 — flip the active default — done.** `appsettings.json`'s
  `"Profile"` changed from `"gigglegarden"` to `"chronicleandchaos"`.
- **Step 7 — verification, no spend — partially done.** Confirmed
  `TtsProviderFactory`'s "auto"-mode en/hi-to-Google routing (flagged earlier
  as a caveat that could silently bypass a profile's Azure `VoiceOverride`) is
  moot in practice: `appsettings.json`'s `"TtsProvider"` is already pinned to
  `"azure"` explicitly, not left at `"auto"`, confirmed by reading the live
  file. Ran `--test-tts --language en` against the new default profile — both
  Azure (`azure-en.mp3`, 93,888 bytes) and Google (`google-en.mp3`, 51,456
  bytes) outputs produced with no errors, confirming the
  `en-US-AriaNeural`/`narration-professional` combination is a live, working
  Azure voice, not just present in a `/voices/list` capability check.
  Confirmed by code inspection (`Program.cs` lines 412 and 447) that
  `profile.MadeForKids` threads through both `Sidecar` constructions, so
  Chronicle & Chaos runs will correctly write `MadeForKids: false`. **Now
  fully closed out**: you added a `GroqApiKey` to `appsettings.Local.json`,
  and `appsettings.json`'s `"ScriptProvider"` was flipped from `"claude"` to
  `"groq"` (Claude's key still has zero credit, so Groq is now the live path
  until that's topped up — flip back to `"claude"` once it is, if you prefer
  Claude's quality for real runs). Ran `--dry-script` live against Groq:
  produced a full `topFive`-format script ("Anubis: 5 Darkest Secrets") with
  a real mythological protagonist as the character (not an invented
  narrator), the cold-open intro style, mature-non-graphic framing, tags
  matching `BaseTags` plus story-specific ones, and `MusicMood: "mysterious
  tense"` — one of the six mood folders now populated with real tracks.
  Confirmed `script.Profile` is only set in `RunPrepAsync` (the real `--prep`
  flow), not `RunDryScriptAsync` — the empty `"Profile": ""` in the dry-run
  JSON is expected, not a bug. Also populated all 6 mood subfolders under
  `assets/music-mythology/` with real instrumental tracks (you'd initially
  dropped them under `assets/music/`, GiggleGarden's folder, by mistake —
  moved them to the correct location; one filename collision between two
  source folders during the merge may have silently dropped one duplicate
  file, flagged to you directly, not silently resolved).
  **Deliverable 4 is now fully done** — every step (1-7) is either complete
  or, for the parts requiring your own action (Deliverable 5's account
  setup), explicitly out of scope for automation.

**Mid-Step-4 finding, resolved:** while reading this file for a place to log
Step 4's completion, found the "Deliverable 6" section below, dated today and
proposing a materially different Chronicle & Chaos pipeline (no Vidu; manual
Gemini character art; auto-fetched Pexels stock; a new Remotion assembly
project; Pixabay Music API) that would partially obsolete Step 4's
Vidu/`VideoAssembler`-targeted fields (`ArtStyleSuffix`,
`BuildIntroBumperPrompts`, the local `BackgroundMusicPath` folder) if adopted.
Flagged this to you directly rather than guessing either way; you chose
**"Finish Deliverable 4 first"** — Deliverable 6 stays exactly as written
below, unimplemented and not started (no `StockFootageClient`, no Remotion
scaffold, no Pexels/Pixabay work) until you revisit and approve it separately.

---

## Deliverable 6: Stock-footage + Remotion assembly path (new, not started)

**Why this exists:** looked at `OpenMontage` (github.com/calesthio/OpenMontage)
as a possible free image/video engine. It's not a library we can call from
the C# pipeline — it's a standalone agentic framework meant to be driven
directly by a coding assistant, AGPLv3, and its "free" local video models
(WAN 2.1, LTX-Video, Hunyuan, CogVideoX) all want 8GB+ VRAM. This laptop's
GPU is a GTX 1650 with 4GB (confirmed via `nvidia-smi`), well under every
one of those floors — local video generation here would OOM or be too slow
to be usable. Decision: skip local video-gen models entirely, and take
OpenMontage's *other* free path instead — real stock footage cut together
with motion graphics — which needs no GPU at all.

**Licensing, checked live before assuming anything:**
- **Pexels** (photos + video) — free API, no attribution required, explicit
  commercial use permitted including monetized content. Only restrictions:
  can't resell the unaltered file itself, can't use it as a trademark/logo.
  This is clean enough to let the pipeline auto-fetch and use without a
  human review step per clip.
- **Wikimedia Commons / Archive.org** — licensing is per-file and often
  jurisdiction-dependent (Commons' own docs say reuse of some "PD-Art"
  reproductions is "at your own risk" and varies by country). Valuable for
  this content (real historic art, artifacts, ruins) but **not safe to
  auto-select in code** — treat as a manual, human-verified supplementary
  source only, same pattern as the by-hand Gemini image generation: I
  surface candidates, you pick and confirm the license tag before it's used.
- **NASA** (images.nasa.gov) — US government work, public domain, safe to
  automate, but only occasionally relevant (space/creation-myth adjacent
  topics).
- **Remotion** — free for an individual or a for-profit org with ≤3
  employees, explicitly permits commercial video monetization. We qualify.
  Paid company license only triggers above that headcount.

**Decided (2026-08-13) — final architecture for both profiles:**

Chronicle & Chaos (adult mythology channel) — hybrid, no Vidu, no paid
image API:
- Script generation tags each scene as a **character moment** (a god,
  hero, historical figure needs to be shown) or a **context/b-roll
  moment** (setting, object, abstract idea).
- Character moments: pipeline prints a Gemini-ready prompt (same pattern
  already used for the avatar/banner), you generate it by hand at
  gemini.google.com, drop the file where the pipeline expects it. As many
  as needed per video — confirmed no meaningful quota pressure at 2
  videos/day on the day this channel is active (see scheduling below).
- Context moments: new `StockFootageClient` (C#, mirrors `ImageClient`'s
  shape) auto-queries the Pexels video/photo API per scene, downloads the
  best match — no manual step, Pexels license is clean for this.
- Wikimedia Commons / Archive.org stay **manual-approval only** (per-file
  licensing risk) — a supplementary source you approve into a local cache,
  never auto-selected by code.
- New Remotion project (Node/TypeScript) in its **own sibling folder**,
  separate from the .NET solution — composes character art + stock
  footage + TTS narration + text overlays, applying pan/zoom/transition
  motion to every asset regardless of source (a Gemini character image
  and a Pexels photo get identical treatment). `VideoGen` shells out to it
  the way it already shells out to ffmpeg.
- No Vidu anywhere in this path — Remotion's pan/zoom is what makes stills
  feel alive, at zero API cost.
- All five formats (`RetellingArc`, `WhatIf`, `TopFive`, `Explainer`,
  `MythBust`) run through this same pipeline — content structure differs,
  visual pipeline doesn't.
- Narrator voice: Azure Neural, documentary/serious style (not "cheerful")
  — exact voice + style tag still needs a live check against
  `/cognitiveservices/voices/list` before it's hardcoded, same
  verification `AzureTtsProvider.cs` already does for the kids voices.
- Background music: Pixabay Music — same automatable, API-searchable,
  royalty-free model as Pexels. License-summary page needs a quick
  confirmation read before wiring in (only skimmed the landing page so
  far), same diligence already applied to Pexels/Remotion.

GiggleGarden (kids channel) — **same manual-image-sourcing strategy,
Vidu kept**:
- Character art generation switches from `ImageClient`'s automated
  OpenAI/Stability calls to the same manual Gemini-prompt-and-supply loop
  as Chronicle & Chaos — removes the paid-API spend entirely.
- Vidu image-to-video animation **stays** for this profile only — young
  children's engagement leans more on visible character motion than an
  adult mythology audience, so the extra cost/complexity is kept
  deliberately for this one channel. Chronicle & Chaos does not use Vidu.
- Visual style is otherwise unchanged: fully illustrated, no stock
  footage mixed in (not asked for, doesn't fit a nursery-rhyme mascot show
  — flagging this assumption so it can be corrected if wrong).
- The existing character-pool reuse mechanism (`BuildupSize`/`MaxSize`/
  `Cooldown`) still applies and helps here: most videos reuse an
  already-generated pool character rather than needing a fresh manual
  generation, which further reduces how often you're asked to sit down
  and generate something by hand.

Shared: `ImageClient`'s OpenAI/Stability calls become **fully dormant**
for both profiles — kept in code (per the standing "don't delete, keep
old code aside" rule), not deleted, but no longer on the live path for
either channel.

**Scheduling (confirmed, informs quota math, not a code architecture
item):** channels alternate by day rather than both running simultaneously
— roughly 4-5 days/week on Chronicle & Chaos, 2 days/week on GiggleGarden,
each capped at 2 videos on its active day. Only one channel's manual-image
workload lands on the Gemini free quota on any given day.

**Still open — the two questions from Deliverable 3 that this doesn't
resolve on its own:**
1. `Formats` full prose (per-format content rules, colour guidance,
   thumbnail guidance, music-mood menu, hashtag examples) — not yet
   drafted, still needed before `ChronicleAndChaosProfile.cs` can be
   written for real.
2. `BaseTags`, `AudiencePersona`/`SafetyFraming`/`TopicGuidance` wording —
   already drafted in Deliverable 3 above, just needs a final read-through
   sign-off.

**Nothing above has been implemented.** Per the standing workflow, this is
for your review — tell me what to change, then I'll start with the
smallest, least ambiguous piece (`StockFootageClient` against the Pexels
API) and work outward: Remotion project scaffold, the manual-character
prompt-and-pickup loop, then rewiring `ImageClient`'s call sites in both
profiles.

(2026-08-17) Update: Deliverable 6 above is now implemented — `StockFootageClient`,
the Remotion assembler, scene tagging, the manual-art loop, and the
`ImageClient`/`VideoAssembler` call-site rewiring in `Program.cs` are all
in place and both projects build clean. No live end-to-end render has
been run yet (no Pexels key configured, no manual art produced).

## Deliverable 7: dual-channel Uploader (2026-08-17)

Supersedes the "channels alternate by day" scheduling note above — you now
want GiggleGarden and Chronicle & Chaos publishing **simultaneously**, each
to its own 4 accounts. `Uploader/AppConfig.cs` is single-tenant today (one
shared `PlatformsConfig` for whatever's in `WatchDirectory`), so this needs
real routing, not just new tokens.

Plan:
- `Shared/Sidecar.cs` — add a `Channel` field, defaulting empty; `LoadAsync`
  falls back to `"gigglegarden"` when absent (same pattern as the existing
  `Targets` backward-compat fallback), so pre-existing sidecars keep working.
- `VideoGen/Program.cs` — stamp `Channel = profile.Id` on both `Sidecar`
  constructions (vertical short + landscape master), so every future render
  self-declares which channel it belongs to.
- `Uploader/AppConfig.cs` — replace the single `PlatformsConfig Platforms`
  with `Dictionary<string, PlatformsConfig> Channels` keyed by profile id
  (`"gigglegarden"`, `"chronicleandchaos"`), plus a `DefaultChannel` for
  sidecar-less manually-dropped videos.
- `Uploader/Program.cs` — build one `IPublisher[]` set per channel instead
  of one global set; resolve each video's channel from its sidecar and
  dispatch through that channel's publishers; per-run quota counters keyed
  by (channel, platform) instead of just platform, so one channel maxing
  out doesn't throttle the other.
- `Uploader/appsettings.json` / `appsettings.Local.json` — restructure
  `Platforms` into `Channels.gigglegarden.*` / `Channels.chronicleandchaos.*`.
  Existing GiggleGarden values move under the `gigglegarden` key unchanged;
  Chronicle & Chaos gets empty placeholders for you to fill in per the
  token-generation steps already given.
- YouTube `TokenStorePath` and TikTok `TokenStorePath` become per-channel
  paths (separate folders/files) so both channels' tokens coexist without
  overwriting each other. The existing YouTube token file gets moved (not
  re-authed) into a `gigglegarden` subfolder as part of this.
- `TokenCapture/Program.cs` — add an optional channel-name argument so
  re-running it for Chronicle & Chaos writes to its own subfolder instead
  of clobbering GiggleGarden's.
- Build both projects, verify no regressions.

Not in scope here: actually obtaining Chronicle & Chaos's real tokens —
that's still the manual steps already given; this only makes the codebase
able to hold and route both channels' credentials once you have them.

**Done (2026-08-17):** `Sidecar.Channel`, `VideoGen` stamping `Channel =
profile.Id` on both renders, `AppConfig.Channels`/`DefaultChannel`,
`Uploader/Program.cs` routing per-channel publisher sets with (channel,
platform) quota keys, `appsettings.json`/`.Local.json` restructured into
`Channels.gigglegarden` / `Channels.chronicleandchaos`, `TokenCapture` now
takes a required `<channel>` arg and writes to `%APPDATA%\GiggleGarden\<channel>\`.
All three projects (`Shared`, `VideoGen`, `Uploader`, `TokenCapture`) build
clean. Moved the existing YouTube token into `...\GiggleGarden\gigglegarden\`
to match the new per-channel path — found a second, differently-hashed
token file still sitting at the old flat path afterward (`...\GiggleGarden\`
root), unexplained but both predate today by over a week so nothing live
rewrote it just now. Left both in place rather than guess which is
authoritative; flagged to you to either re-run TokenCapture for
`gigglegarden` cleanly or just delete the one you don't want.
Chronicle & Chaos's actual Instagram/Facebook/TikTok/YouTube credentials
are still unset (empty placeholders) — needs the manual token-generation
steps run per platform, same as before.

## Deliverable 8: fully segregated per-channel apps + TikTok capture tool (2026-08-17)

Decision (user-confirmed): Chronicle & Chaos gets its own developer app on
every platform — separate Google Cloud project, separate Meta app, separate
TikTok app — not just its own tokens under a shared app. Reasons: YouTube
Data API quota (10k units/day) is per-project, so sharing one project across
two channels halves each channel's effective headroom for no benefit; a
flagged/restricted app also only takes down one channel this way.

Also found along the way: GiggleGarden's own YouTube refresh token was
already dead (`invalid_grant`, confirmed via a live read-only refresh
attempt) — its Google Cloud OAuth app is in Testing status, which caps
refresh tokens at 7 days. Re-running TokenCapture for `gigglegarden` is
needed regardless of the Chronicle & Chaos work.

- [x] `Channels.chronicleandchaos.YouTube.ClientSecretPath` repointed to
      `C:\Secure\ChronicleAndChaos\client_secret.json` (new project, not
      created yet — steps given to user)
- [x] User created the new Google Cloud project + OAuth consent screen
      (test user: chronicleandchaos's own email only) + Desktop OAuth
      client, saved client_secret.json at the path above — confirmed via
      `client_secret.json` on disk at `C:\Secure\ChronicleAndChaos\`
- [ ] User re-runs TokenCapture for `gigglegarden` (existing project) to
      replace the dead token — still open; token at
      `%APPDATA%\GiggleGarden\gigglegarden` is still the stale Aug 9 one
      that returns `invalid_grant` on refresh
- [x] User ran TokenCapture for `chronicleandchaos` against the new
      project — confirmed via a fresh token file at
      `%APPDATA%\GiggleGarden\chronicleandchaos` (2026-08-17 05:46)
- [x] User created a new Meta app for Chronicle & Chaos (Business type),
      added Instagram Graph API product, generated a long-lived Page token
      for Chronicle & Chaos's Page (`PageId 1259686303903826`,
      `IgUserId 17841439599868514`), filled in
      `Channels.chronicleandchaos.{Facebook,Instagram}` + the token in
      appsettings.Local.json. Verified live via `debug_token`: `is_valid
      true`, expires 2026-10-16 (~60 days out, confirms it's the long-lived
      exchange, not the short-lived token) — see renewal note below.
- [x] User created a new TikTok Developer app for Chronicle & Chaos,
      added Content Posting API product, got ClientKey/ClientSecret
- [x] Correction: `TikTokTokenCapture` already existed (commit `4c83cb1c`,
      predates this conversation) — a `Glob` false-negative briefly made it
      look missing. It already does the full PKCE + local-redirect OAuth
      flow and already takes an optional per-channel token-store-path arg.
      No code change needed, mirrors YouTube's TokenCapture exactly.
- [x] User ran TikTokTokenCapture for `chronicleandchaos` — refresh token
      saved to `C:\Secure\GiggleGarden\tiktok-token-chronicleandchaos.json`
      (confirmed on disk). `gigglegarden`'s own TikTok token status not
      re-verified in this pass — [ ] still open if it hasn't been re-run
      since its app was created.
- [ ] README updated with the Meta/TikTok separate-app steps and
      TikTokTokenCapture usage

### ⚠️ Recurring: Facebook/Instagram long-lived Page token renewal

Chronicle & Chaos's Facebook/Instagram token (shared — Instagram publishing
goes through the same Page token) is a **long-lived Page token, not
permanent**. Confirmed via `debug_token` on 2026-08-17: expires
**2026-10-16**. Facebook offers no non-expiring option on this flow — it
needs re-exchange roughly every 60 days or uploads will silently start
failing on that channel.

- [ ] Renew `Channels.chronicleandchaos.{Facebook,Instagram}.AccessToken`
      before **2026-10-16** — re-run the short-lived → long-lived exchange
      (`GET /oauth/access_token?grant_type=fb_exchange_token&...`) from a
      fresh Graph API Explorer token, update `appsettings.Local.json`.
- [ ] GiggleGarden's own Facebook/Instagram token has the same 60-day
      expiry mechanics and has not been checked in this pass — worth an
      equivalent `debug_token` check to know its actual expiry date.
- Consider: a periodic reminder (calendar or a scheduled check) rather than
  relying on someone noticing a failed upload after the fact.

---

## Long-form YouTube path for Chronicle & Chaos — plan

Requested: today every render (`RunAssembleAsync`) is the same ~8-scene,
~75-90s script cut twice — a vertical crop for IG/FB/TikTok and a landscape
crop for YouTube — both tagged `#Shorts`. YouTube gets no long-form content,
unlike every real comp in this niche (The Why Files, The Infographics Show),
which build watch-time on 10-40 min documentaries. Requested: a real
long-form path (20-40+ scenes / several minutes) for YouTube, keeping the
current short as the IG/FB/TikTok/Shorts teaser.

Four decisions locked in via `AskUserQuestion` before designing:
1. Target length: **12-15 minutes**.
2. Art density: **hybrid** — a shared image sustains ~20-40s of narration via
   Remotion pan/zoom/hold; fresh manual art only at key story beats (~12-18
   images/video, not one per scene).
3. Short/long relationship: the Shorts/Reels teaser is **derived from the
   long-form script**, not generated independently.
4. Formats: **all 5** (`retellingArc`, `whatIf`, `topFive`, `explainer`,
   `mythBust`) get long-form treatment, not just `retellingArc`.

**Investigation before designing** (avoiding a duplicate mechanism):
`Scene.ImagePath2`/`VerticalImagePath2` + `VideoAssembler.BuildSplitImageClipAsync`
already do a "second image, internal crossfade" for a long single scene — but
that mechanism lives entirely in `AssembleAsync`/`AssembleFromClipsAsync`
(the ffmpeg Ken-Burns path GiggleGarden uses). Chronicle & Chaos renders
through `AssembleWithRemotionAsync` instead (`AllowsContextScenes = true`),
which never reads `ImagePath2` — confirmed by reading `VideoAssembler.cs` in
full. So that mechanism is dead for this profile and **not** being extended.

Read `Assembly.tsx`/`schema.ts`: each `SceneAsset` already gets its own Ken
Burns pan/zoom computed over its own `durationInSeconds`, driven purely by
`path`/`durationInSeconds`/`narrationAudioPath`/`captionText`. Nothing stops
two consecutive scenes pointing at the same image file — Remotion just
restarts the pan/zoom at each scene boundary, which reads as intentional
b-roll-style re-establishing shots, not a bug. **Conclusion: the hybrid
art-density requirement needs zero Remotion/schema changes.** It's a
script-generation + art-resolution-loop concern only: cluster scenes into
"visual groups," resolve art once per group, point every scene in the group
at that same `ImagePath`.

Also confirmed: `RunAssembleAsync`'s existing vertical-cut logic already
*trims `script.Scenes` to fit `cfg.VerticalMaxSeconds`, from the front*. If
the long-form script's opening ~8-10 scenes are written as a strong
self-contained hook (mirroring what the short's scene 1 already has to be),
that existing trim IS the teaser-derivation decision #3 asks for — no new
"pick a hook segment" algorithm needed. Landscape already renders whatever
`script.Scenes` contains, untrimmed — so pointing it at the full long-form
scene list is also a no-op change to that code path.

### Plan

- [ ] `Scene` (`ScriptGenerator.cs`): add `VisualGroup` (int, default 0) —
      consecutive scenes sharing a group number reuse one resolved image;
      a new group number is a fresh art beat.
- [ ] `VideoScript`: add `IsLongForm` (bool, default false), persisted like
      every other script-level flag.
- [ ] `ScriptGenerator.GenerateAsync`: new `longForm` parameter. When true:
  - scene-count/length instruction changes from "exactly 8 scenes, <115
    chars each" to a range hitting 12-15 min total narration (~140 wpm),
    each scene still a natural <180-char beat (so TTS/subtitle timing stays
    the same shape, just more of them — no new subtitle-wrap logic needed).
  - new instruction: assign each scene a `visualGroup` int; group narration
    into ~12-18 groups spanning the whole video, each covering roughly
    20-40s (a handful of consecutive scenes) — the model clusters by
    story-beat, not a fixed scene-per-group count.
  - explicit instruction that the first ~8-10 scenes must work as a
    self-contained hook, since they double as the teaser once trimmed.
- [ ] `MythologyProfile.cs`'s `BuildFormatInstructions`: each of the 5
      formats' `contentRules` needs a long-form variant (e.g. `topFive`'s
      "1-2 scenes per entry" → "a dedicated multi-scene segment per entry";
      `mythBust`'s "correct it point by point" → "each point gets its own
      segment"). Add via a new `LongFormContentRules` field on
      `FormatInstructionSet` (additive, short-form untouched) rather than a
      parallel instruction-set type.
- [ ] `Program.cs`:
  - new `--long` CLI switch; throws a clear error unless
    `profile.AllowsContextScenes` (long-form on the Vidu/clip path would be
    60-90 paid Vidu submissions/video — out of scope, not what was asked).
  - `RunPrepAsync`'s `AllowsContextScenes` art-resolution loop: track the
    last-resolved `(VisualGroup, ImagePath, ClipPath)`; when the next
    scene's `VisualGroup` matches, copy the path instead of calling
    `StockFootageClient`/`ManualArtClient` again.
  - `RunAssembleAsync`: no change to the render calls themselves — confirm
    by test that the existing `shortScenes` trim (teaser) and full
    `script.Scenes` (landscape/long-form) logic behaves correctly against a
    70-90-scene long-form script.
- [ ] Sidecar/title: landscape long-form render should not carry `#Shorts`
      semantics implied elsewhere (it already doesn't — only the vertical
      cut's title gets `#Shorts` appended, unchanged).

### Cost/scope, called out explicitly per the user's "real scope" framing

- ~70-90 Azure TTS calls/video instead of ~9 (cheap, character-billed).
- ~12-18 manual Gemini art generations/video instead of ~9 — this is the
  accepted human-in-the-loop time cost from decision #2, not a regression.
- Zero new paid APIs, zero Remotion/schema changes, zero ffmpeg changes.

### Verification (before marking done)

1. `dotnet build` clean.
2. `script.json` round-trip: old files without `VisualGroup`/`IsLongForm`
   still deserialize (defaults: group 0, `IsLongForm = false`).
3. `--dry-script --long` (extended to support the flag, zero spend) across
   all 5 formats: confirm total narration lands in the 12-15 min band,
   `visualGroup` values cluster into roughly 12-18 groups, first ~8-10
   scenes read as a coherent hook standalone.
4. Reflection-harness check (matching this file's established pattern) that
   the art-resolution loop's group-reuse logic only calls
   `StockFootageClient`/`ManualArtClient` once per group, not once per scene.
5. Review section appended here once run.

No live `--prep --long` run (spends Gemini art time + real API calls) without
a separate go-ahead, consistent with how every other phase in this file
gates a real run.

---

## Long-form chunked generation — Groq TPM blocker (plan)

Discovered while running verification step 3 above (`--dry-script --long`):
`llama-3.3-70b-versatile` (the configured `GroqModel`) now 404s — deprecated on
Groq's end, unrelated to this feature (repro'd identically without `--long`).
Live model list (`GET /openai/v1/models`) confirmed the two models you named,
`openai/gpt-oss-120b` and `qwen/qwen3.6-27b`, both exist and are active
(131072 ctx). Picked `openai/gpt-oss-120b` — OpenAI's flagship open-weight
release, stronger public track record on strict-JSON output than the newer,
smaller Qwen3.6-27B — and updated `appsettings.json`.

That surfaced the real blocker: a single-shot 70-90-scene request needs
~32-35k tokens (prompt + completion), but Groq's free tier caps **every**
plain chat model at **8,000 tokens/minute** — confirmed via response
rate-limit headers on `gpt-oss-120b`, `gpt-oss-20b`, and `qwen3.6-27b` alike
(only the agentic `compound`/`compound-mini` models get a higher 70k cap, and
those inject tool-use/search behavior that risks breaking strict JSON output
— not a safe swap for this). Live 413 confirmed: `Requested 35482, Limit
8000`. `ClaudeScriptProvider.cs:14`'s hardcoded `max_tokens = 8000` is at the
same real risk for the same reason, untested until now.

Asked you how to proceed (`AskUserQuestion`); you picked **chunk the
long-form request into multiple smaller requests ("acts")** over falling
back to Claude for `--long` or risking `compound-mini`.

### Plan

- [x] `ScriptGenerator.cs`: split long-form generation into 4 acts instead of
      one shot. Act 1 = the existing single-call prompt, reworded so it asks
      for full metadata + only ~18-22 scenes ("Act 1 of 4 - the opening/
      setup"), not the whole 70-90. Acts 2-4 = a new, smaller
      `GenerateContinuationActAsync` prompt: character description, this
      format's `effectiveContentRules`/`sceneKindGuidance`/colour rules
      (unchanged rules, so per-act quality doesn't drift), a short recap (the
      last few narrated lines) for continuity, the next `visualGroup` number
      to continue from, and a `{"scenes": [...]}`-only response (new
      `ScenesOnly` DTO) — no metadata re-asked.
- [x] Merge: `script.Scenes.AddRange(...)` after each continuation act, ahead
      of the existing distinct-`visualGroup` sanity check and all the
      existing post-processing (tags, character validation, `SceneKind`
      normalization, `MotionPrompt` fallback) so those run once, generically,
      over the full merged list exactly as they do today.
- [x] Applies regardless of active `IScriptProvider` — the loop lives in
      `ScriptGenerator`, not `GroqScriptProvider`, so it also protects
      Claude's 8000-token `max_tokens` ceiling for free, at the cost of a few
      more Claude calls if `ScriptProvider` ever switches back.
- [x] `GroqScriptProvider.cs`: drop the flat `max_completion_tokens = 32000`
      (sized for the old one-shot approach) down to a per-request cap sized
      for one act (~6000) — keeps every request's requested-token total
      (prompt + completion) comfortably under Groq's 8000 TPM ceiling.
- [ ] Known limitation, not blocking: continuity across acts relies on a
      short recap (last few lines), not full history — a `topFive`/`mythBust`
      script could in principle repeat an earlier entry in a later act. Worth
      watching in the verification dry-runs below; a stronger fix (structured
      "already covered" breadcrumbs) is future work if it actually shows up.

### Verification

1. `dotnet build` clean.
2. `--dry-script --long` across all 5 formats on Groq: confirm all 4 acts
   complete without a 413/429 exhausting retries, total scenes/duration lands
   near the 70-90/12-15min target, `visualGroup` numbering is contiguous
   across act boundaries (no duplicate/reset numbers), no obvious repeated
   beat across acts.
3. Review appended here once run.

### Review

The 413 (per-minute budget) is fixed. First live run of Act 1 alone still hit
a 413 (`Requested 9471, Limit 8000`) — the fixed `max_completion_tokens: 6000`
plus Act 1's own large rules/schema prompt (~3.5k tokens) was still over the
ceiling, since Act 1's prompt is 3-4x bigger than a continuation act's. Fixed
properly by sizing `max_completion_tokens` off the actual prompt length in
`GroqScriptProvider.cs` (`Math.Max(1500, 7500 - prompt.Length / 4)`) instead of
a flat number, and trimming Act 1's own ask from ~18-22 scenes down to 10-14
(it also carries the full title/description/character-invention response, so
it needed a smaller scene budget than the continuation acts) while bumping
Acts 2-4 to 20-25 each to still land in the 70-90 total range.

That surfaced two more failure modes, both pre-existing weaknesses of
open-weight-model JSON reliability that chunking simply exposed 4x more often
(4 calls/script instead of 1): occasional malformed JSON mid-response, and a
`sceneKind: "context"` scene missing its required `stockQuery`. Both are
one-off sampling glitches, not deterministic prompt problems, so the fix is a
bounded retry: `CompleteAndParseAsync` (new helper in `ScriptGenerator.cs`)
retries the exact same prompt up to 3 times on either a JSON parse failure or
a caller-supplied `validate` callback throwing `InvalidDataException`; both
the main script call and every continuation act now route through it, with
`ValidateContextScenes` reused as the validator for both.

Verified 4 of 5 formats live end-to-end (`--dry-script --long` on
`chronicleandchaos`, Groq/`gpt-oss-120b`):

| Format | Scenes | Visual groups | Duration | Notes |
|---|---|---|---|---|
| retellingArc | 78 | 28 | ~9min | 1 retried JSON glitch on an early run (pre-retry-fix), clean after |
| whatIf | 79 | 27 | ~10.6min | 1 retried JSON glitch, retry succeeded |
| topFive | 71 | 31 | ~8.7min | clean |
| explainer | 81 | 29 | ~11.5min | clean |
| mythBust | — | — | — | blocked, see below |

All four: no unhandled exceptions, no 413s, `visualGroup` numbering
contiguous across every act boundary with no resets/duplicates, no obviously
repeated beat spotted skimming the narration. Scene counts landed inside the
70-90 target; duration landed under the 12-15min target on 3 of 4 (~9-11.5min
vs the ~12-15min goal) — narration is coming in noticeably shorter than the
180-char/scene ceiling allows, not a chunking defect, just the model not
filling its budget. Not fixed here (out of this task's scope — the blocker
was TPM/reliability, not pacing) but worth a follow-up if the shorter runtime
matters: nudge the per-scene guidance to write closer to the cap, or bump the
per-act scene counts further.

`mythBust` (the 5th format) did not get a clean run: it hit Groq's **daily**
token cap (`TPD: Limit 200000, Used 197108, Requested 7375` → 429), not the
per-minute one — the four prior verification runs in this same session each
burned ~30-40k tokens across their 4 acts, plus everything spent earlier
diagnosing the original 413/reasoning-token issues, exhausted the day's
budget. `RetryHandler.cs` retried it 3 times with correct exponential
backoff (17s/56s/27s/120s/120s/120s) before giving up with a clear FATAL
error — that's `RetryHandler` working as designed against a genuinely
exhausted quota, not a bug in the chunking change. Groq's own error reported
a ~32 minute reset. Not re-run here to avoid spending more of a quota a real
job may need; the other 4 formats already demonstrate the chunking/retry
logic holds up across format variety, so this is logged as an open item
rather than re-run immediately.

**Follow-up, not done in this pass:**
- Re-run `mythBust --dry-script --long` once the daily quota resets, purely
  to complete the format matrix (low risk — it exercises the same code path
  as the other 4).
- Consider whether Chronicle & Chaos's real `--prep --long` cadence (however
  often it's meant to run) fits inside Groq's free-tier 200k TPD budget at
  all — 4 formats' worth of verification alone used nearly the whole day's
  budget in ~40 minutes. If the real production cadence is more than
  ~1 long-form video/day on Groq's free tier, this will need either Claude
  for `--long` specifically, or a paid Groq tier.
- The known "continuity across acts relies on a short recap" limitation
  flagged in the Plan above did not visibly surface in any of the 4 runs
  (skimmed for repeated beats), but wasn't exhaustively checked beat-by-beat.

## Gemini script provider (plan)

Motivation: Groq free tier hit two real ceilings this session (8k TPM/model,
200k TPD total) — 4 verification runs alone burned most of a day's budget.
Community-reported Gemini free-tier numbers (unverified against a live
official table — Google now gates exact figures behind an AI-Studio login)
are far higher: `gemini-2.5-flash` ~10 RPM/250k TPM/250 RPD,
`gemini-2.5-flash-lite` ~15 RPM/250k TPM/1000 RPD. Adding Gemini as a second
`IScriptProvider` lets us test those numbers live the same way Groq's were
confirmed, and gives `chronicleandchaos` a provider with real headroom for
`--long` if the numbers hold.

### Plan
- [x] Add `GeminiApiKey` / `GeminiModel` to `GenConfig.cs`, gate the key
      requirement in `Validate()` on `ScriptProvider == "gemini"` only
      (same pattern as the existing Groq gate).
- [x] Add matching placeholder fields to `appsettings.json`.
- [x] New `GeminiScriptProvider.cs` implementing `IScriptProvider` against
      the `generateContent` REST endpoint, reusing `RetryHandler` for
      429/5xx backoff same as Claude/Groq.
- [x] Add a `"gemini"` branch to `ScriptProviderFactory.cs`.
- [x] Build clean, then live-verify with `--dry-script --long` (temporarily
      set `ScriptProvider: "gemini"`) to see Gemini's *actual* rate-limit
      headers/errors, not the unverified community numbers above.

### Review

`gemini-2.5-flash` (the originally planned default) turned out to be
retired for new users - the live API 404s and points to `gemini-3.6-flash`
instead, so that became the default. First live attempt also hit a
billing-related 429 ("prepayment credits are depleted") because the
project (`Giggle Garden`) had a paid billing account linked, even at $0
spend - Gemini's free tier disappears entirely once a project has ever had
billing attached to it, confirmed via Google's own forum. Fixed by the user
disabling billing on the project in Google Cloud Console, which did restore
free-tier behavior here (contrary to some forum reports that this doesn't
reliably work - it did in this case).

Second finding: `gemini-3.6-flash` is a thinking model, and hidden
reasoning tokens count against `maxOutputTokens` (same failure mode
already solved for Groq's `gpt-oss` models) - the first free-tier run
truncated mid-JSON on 2 of 4 acts. Fixed in `GeminiScriptProvider.cs` by
adding `generationConfig.thinkingConfig.thinkingLevel = "low"` and raising
`maxOutputTokens` from 8000 to 12000 as headroom (thinking can only be
minimized on Gemini 3 models, not fully disabled).

After both fixes, `retellingArc` and `whatIf` ran clean end-to-end with
**zero retries and zero 429s** across all 4 acts each (Groq needed retries
on most runs) - `retellingArc`: 76 scenes/19 groups/~12.1min, landing
inside the 12-15min target on the first try; `whatIf`: 66 scenes/19
groups/~11.1min.

Third run (`topFive`, still on `gemini-3.6-flash`) hit a **real, confirmed**
free-tier quota: `GenerateRequestsPerDayPerProjectPerModel-FreeTier`,
`quotaValue: 20`. That's 20 requests/day, per project, per model - roughly
5 long-form scripts/day at 4 requests/script (1 main + 3 continuation acts),
in the same order of magnitude as Groq's effective ~4-5 scripts/day, **not**
the large headroom win the earlier community-sourced numbers (250 RPD, for
the now-retired `gemini-2.5-flash`) suggested. Switching to
`gemini-3.5-flash-lite` (separate quota bucket, per-model) let `topFive` run
clean immediately after - that model's real RPD wasn't pushed to failure
and remains unconfirmed, but at minimum it's a second independent 20+
request/day budget on the same free account.

**Net finding:** Gemini alone doesn't dissolve the daily-cap problem the
way the initial research suggested - `gemini-3.6-flash`'s free tier is
capped at 20 req/day just like Groq's is effectively capped by TPD. What
Gemini *does* provide is a wholly separate quota bucket (or two, counting
flash-lite) from Groq's - so alternating providers per script (not
mid-script act-splitting) could roughly double or triple real daily
capacity without the tone-consistency risk a mid-script provider switch
would carry. Not yet decided or implemented - this is a decision for the
user, not something to build unprompted.

**Follow-up, not done in this pass:**
- Confirm `gemini-3.5-flash-lite`'s real RPD (currently unconfirmed -
  only 1 request spent against it before stopping to avoid burning more
  of a shared daily quota mid-investigation).
- Decide whether to build provider alternation (e.g. Groq on odd days,
  Gemini on even; or round-robin per script) - not authorized/built yet.
- appsettings.json currently has `ScriptProvider: "gemini"` and
  `GeminiModel: "gemini-3.5-flash-lite"` left over from this test run -
  revert to `"groq"` / `"openai/gpt-oss-120b"` before any real `--prep`
  run, unless the user wants Gemini as the active default going forward.

## Hybrid Groq+Gemini provider for a single script (plan)

User explicitly asked for the original idea: one script, first half of the
acts on one provider, second half fed to the other - not provider
alternation across different scripts. Long-form scripts always make exactly
4 sequential `CompleteAsync` calls (main call = act 1, then acts 2-4 via the
continuation loop in `ScriptGenerator.GenerateAsync`), so a call-counting
wrapper is enough - no changes needed inside `ScriptGenerator.cs` itself,
since it already passes each act's recap/context forward regardless of which
provider produced the prior act.

Split: acts 1-2 on Groq, acts 3-4 on Gemini (one handoff, at the narrative
midpoint, not one at every act boundary - fewer tone seams than a full
per-act alternation).

### Plan
- [x] New `HybridScriptProvider.cs`: `IScriptProvider` that counts calls and
      routes the first N to one inner provider, the rest to a second.
- [x] Add a `"hybrid"` branch to `ScriptProviderFactory.cs` wiring
      Groq (first 2 calls) + Gemini (remaining calls).
- [x] `GenConfig.Validate()`: require both `GroqApiKey` and `GeminiApiKey`
      when `ScriptProvider == "hybrid"`.
- [x] Build clean, then live-verify with `--dry-script --long`
      (`ScriptProvider: "hybrid"`) - confirm via the `[groq]`/`[gemini]` log
      prefixes that acts 1-2 hit Groq and acts 3-4 hit Gemini, and skim the
      output for a jarring tone/voice seam at the handoff.

### Review

Added a `[hybrid] call N -> <ProviderType>` log line to `HybridScriptProvider`
itself (same style as the existing `[groq]`/`[gemini]` prefixes) so the
routing is visible in every run, not just this one-off test.

First live run (real `firstProviderCalls: 2` split, `explainer` format):
call 1 correctly routed to `GroqScriptProvider` per the log, but Groq's
account-level TPD was still exhausted from earlier same-session testing
(`196975/200000` used, ~30min to reset) - a real account-state limit, not a
routing bug, so acts 2-4 never ran in that attempt.

Rather than wait 30 minutes, ran a second diagnostic with
`firstProviderCalls: 0` (all calls forced to Gemini) to confirm the handoff
mechanism itself. Result: all 5 raw `CompleteAsync` calls (4 acts + 1
mid-generation JSON-retry on act 2) routed correctly, and `ScriptGenerator`
assembled a coherent 72-scene/4-act script ("Minos: The REAL King Behind the
Labyrinth Monster") with no crash or seam - confirms `ScriptGenerator`'s
continuation/recap logic really is provider-agnostic as designed.

One real limitation surfaced by this test, not fixed (rare edge case, still
produces a coherent script either way): the wrapper counts raw
`CompleteAsync` calls, not acts. If an early act needs a JSON-retry, that
extra call consumes one slot of the `firstProviderCalls` budget, so the
Groq/Gemini boundary can silently land one act earlier than the intended
"acts 1-2 vs 3-4" split. Not worth guarding against for a 2-provider,
4-call script - the failure mode is "the tone seam moves by one act," not
a broken script.

Reverted both temporary test edits after verification:
`ScriptProviderFactory.cs`'s `firstProviderCalls` back to `2`, and
`appsettings.json` back to `ScriptProvider: "claude"` (the zero-risk
default) with `GeminiModel` reset to `gemini-3.6-flash` (matches
`GenConfig.cs`'s default; it had been swapped to `gemini-3.5-flash-lite`
mid-session only because 3.6-flash's 20 RPD was already exhausted from
earlier testing). To actually use hybrid generation, set
`ScriptProvider: "hybrid"` in `appsettings.Local.json` - Groq's TPD resets
daily, so pick a time when it isn't already spent by other testing.

(2026-08-17) Update: default flipped to `"hybrid"` in both
`appsettings.json` and `GenConfig.cs`'s fallback, per explicit user request
("flip default to hybrid") - comments in `GenConfig.cs`/`ScriptProviderFactory.cs`
updated to describe hybrid as the default rather than claude. Build clean.

---

## GiggleGarden proper channel setup (plan)

User: the initial GiggleGarden setup wasn't done properly and wants it
redone right, covering five things, in order: (1) a single consistent
account handle checked for real availability across all 4 platforms before
committing to it, (2) avatar + banner art, (3) description/tags/settings
applied correctly on every platform, (4) a properly vetted kids narration
voice, (5) a real background music pool, sourced via Pixabay Music with
prompts I provide.

Decisions locked in via `AskUserQuestion` before starting:
- Name: I propose fresh kid-friendly candidates (not keeping "GiggleGarden"
  as a given), then check live availability.
- Platforms: same 4 as Chronicle & Chaos - YouTube, Instagram, Facebook,
  TikTok.
- Internal naming: profile id `"gigglegarden"`, file paths, config keys all
  stay unchanged in code regardless of what public handle is chosen - only
  the external account name/handle and branding assets change.
- Avatar/banner: invent ONE fixed channel mascot (name + fixed appearance)
  used only for avatar/banner/intro-bumper purposes - separate from the
  per-video invented characters `BuildCharacterInventionInstructions`
  already generates for video content, which is unchanged.
- Voice: re-verify live against Azure's `/cognitiveservices/voices/list`
  (same diligence already applied to Chronicle & Chaos's Eric pick) rather
  than assume `en-US-JennyNeural`/cheerful is still the best fit - may end
  up keeping Jenny if it checks out.

Found while surveying current assets: unlike Chronicle & Chaos
(`assets/branding/chronicle-and-chaos/`), GiggleGarden has **no**
`assets/branding/` folder at all - no avatar, no banner ever made. Its
`assets/music/` pool is real but currently empty (README only, confirmed
this session and in memory from earlier). So this is filling a real gap,
not redoing something that already existed.

### Plan
- [x] Brainstorm 6-10 kid-friendly channel name/handle candidates. Went
      through 5 batches (generic compound, duck-themed, giggle-prefixed,
      single-word Cocomelon/Blippi-style, and a second single-word round) -
      ~70 candidates checked total.
- [x] Check each candidate's live availability on YouTube, Instagram,
      Facebook, TikTok (read-only browser checks - no accounts created,
      no logins, per the standing rule against creating accounts).
- [x] Report an availability table; user picks the final name. **Chosen:
      `gigglewiggletown`** (giggle-prefixed batch) - verified clear on
      YouTube, Instagram, TikTok, and likely-clear on Facebook (the
      logged-out-signal caveat noted earlier in this file still applies).
      Flagged to the user as long/unwieldy; they picked it anyway.
- [x] Design one fixed channel mascot: **Giggy** - a round, pudgy garden
      creature, coiled-spring body in teal/sunshine-yellow stripes, huge
      round white eyes, giggling open-mouth grin, two wiggly antennae,
      two stubby feet, no arms - matches `CharacterPortraitStyleSuffix`/
      `CharacterStyle`. Presented in-chat for sign-off; user moved
      straight to using the follow-on content, treated as accepted, not
      re-confirmed with an explicit yes.
      Also found and fixed in passing: `GiggleGardenProfile.cs`'s
      `ChannelName` (spoken in every video's intro - "Welcome to
      {ChannelName}!") was still `"Giggle Garden"`, mismatched against
      the new public handle. Changed to `"Giggle Wiggle Town"`; internal
      `Id = "gigglegarden"` and all file paths untouched per the earlier
      locked decision. `dotnet build` clean after the change.
- [x] Generate avatar (square) and banner (2560x1440) - user generated
      both and saved to `assets/branding/giggle-wiggle-town/`. Verified:
      `Giggle_Wiggle_Town_Avatar.jpeg` is 1024x1024, matches Giggy's
      design brief well. `Giggle_Wiggle_Town_Banner.jpeg` is 1584x672 -
      below YouTube's documented banner minimum of 2048x1152 - flagged
      as a likely upload blocker, but **user uploaded it live and it
      went through with no issues**, so that documented minimum either
      doesn't hold in practice or Studio auto-upscales. Corrected here
      rather than left as an open concern.
- [x] Draft channel description, tags, and the MadeForKids/audience
      settings text for each of the 4 platforms - delivered in-chat:
      YouTube About description + channel Keywords + Studio "Upload
      defaults" description/tags (screenshot-driven), Instagram bio,
      Facebook Page About, TikTok bio, plus cross-linking steps for all
      4 platforms and a MadeForKids-consequences explainer. Applying it
      is the user's own manual step - not done via Claude-in-Chrome,
      never asked for. **User confirmed this is applied to the live
      accounts** (YouTube About/Keywords/upload defaults, Instagram,
      Facebook, TikTok) - not independently re-verified on my end.
- [ ] Live-check `en-US-JennyNeural` (current) plus 2-3 kid-oriented
      alternatives (e.g. `en-US-AnaNeural`, marketed by Microsoft as a
      child's voice) against `/cognitiveservices/voices/list` for
      available express-as styles - same process as Chronicle & Chaos's
      Eric pick - report findings, user picks final voice.
- [x] Propose ~10-15 Pixabay Music search prompts split across
      `GiggleGardenProfile`'s existing calm menu ("soft piano lullaby",
      "gentle music box", "warm ambient") and energetic menu ("upbeat
      playful", "bright acoustic", "cheerful ukulele") - user downloads,
      I organize into `assets/music/` per the existing README's licensing/
      format rules (instrumental, ≥90s, CC0/no-attribution preferred).
      User populated all 6 mood folders (29 tracks total); verified via
      `ffprobe` - all `.mp3` (accepted format). 6 tracks under the 90s
      guideline (`cheerful-ukulele` worst hit, only 1 of 5 tracks clears
      90s); not a functional blocker since `MixBackgroundMusicAsync`
      loops with `-stream_loop -1`, but short loops repeat audibly.
      Two tracks flagged for a manual listen before shipping:
      `bright-acoustic/jonasblakewood-extraordinary-custom-vocal-294010.mp3`
      (filename said "vocal" - README is instrumental-only) and
      `gentle-music-box/leberch-horror-music-box-511181.mp3` (filename
      said "horror" - possibly too unsettling for the calm bucket).
      **User listened to both - confirmed fine, no vocals, no mood
      mismatch.** Renamed to drop the misleading words:
      `jonasblakewood-extraordinary-custom-294010.mp3` and
      `leberch-gentle-music-box-511181.mp3`.
- [x] Live-check `en-US-JennyNeural` (current) plus 2-3 kid-oriented
      alternatives (e.g. `en-US-AnaNeural`, marketed by Microsoft as a
      child's voice) against `/cognitiveservices/voices/list` for
      available express-as styles - same process as Chronicle & Chaos's
      Eric pick - report findings, user picks final voice.
      Live-queried all 109 en-US voices; Jenny (current, style
      "cheerful") still valid. Confirmed `en-US-AnaNeural` is Microsoft's
      only true "Female, Child" tagged en-US voice, but has zero
      express-as styles - generated side-by-side samples (Jenny, Ana,
      Aria, Jane, Sara, all same line) via the Azure TTS REST API and
      sent them to the user. **User picked Ana.** Updated
      `GiggleGardenProfile.cs`'s `VoiceOverride["en"]` to
      `("en-US", "en-US-AnaNeural", null)` - `Style` is `null` (not
      "cheerful") since Ana has no supported express-as styles; a
      non-null style on a voice that doesn't support it fails the Azure
      call outright (see `AzureTtsProvider.cs`'s SynthesizeAsync
      comment). `dotnet build` clean after the change.
- [x] Review section appended here once run.

## Review - GiggleGarden proper channel setup

All 5 phases complete:
- **Name**: `gigglewiggletown`, chosen by the user over my "long/
  unwieldy" flag; verified clear on YouTube/Instagram/TikTok, likely
  clear on Facebook (logged-out-signal caveat).
- **Mascot/branding**: "Giggy" designed and accepted; `ChannelName`
  fixed to "Giggle Wiggle Town" in `GiggleGardenProfile.cs` (internal
  `Id`/paths stay `gigglegarden`). Avatar and banner generated by the
  user, saved to `assets/branding/giggle-wiggle-town/`. Avatar verified
  good (1024x1024). Banner's scene is good but its resolution
  (1584x672) is below YouTube's documented banner minimum
  (2048x1152) - **needs a higher-res regenerate/upscale before
  uploading**.
- **Descriptions/tags/links**: full copy delivered for YouTube (About +
  Keywords + Studio upload defaults), Instagram, Facebook, TikTok, plus
  cross-linking checklist and MadeForKids explainer. User confirmed
  it's applied to all 4 live accounts (not independently re-verified).
- **Voice**: switched from `en-US-JennyNeural` to `en-US-AnaNeural`
  (Style: null) after live-verifying styles and the user picking Ana
  from generated audio samples. Since Ana has no express-as styles to
  supply the "cheerful" boost Jenny had, compensated via prosody:
  bumped `pitch` from `+6%` to `+9%` on the 4 non-calm formats
  (educational/rhyme/singAlong/countingSong) in `BuildFormats()` after
  the user A/B'd +6/+9/+12 samples and picked +9. This table is shared
  across languages, so it also nudges `hi-IN-SwaraNeural` (stacks with
  her existing "cheerful" style - worth an ear-check next Hindi render)
  and `pa-IN-VaaniNeural` (style-less like Ana, same benefit). `dotnet
  build` clean after the change.
- **Background music**: all 6 mood folders populated (29 tracks total),
  verified via `ffprobe` for format/duration against `assets/music/
  README.md`. Two tracks flagged for a manual listen over misleading
  filenames ("vocal", "horror") - user listened, confirmed both are
  fine (instrumental, calm), renamed to drop the misleading words.
  `cheerful-ukulele` is thin on tracks that clear the README's ~90s
  guideline (1 of 5) - not a functional blocker since music loops,
  just a quality note.

All 5 phases are now fully done, including manual follow-through
(images generated, copy applied to live accounts, banner uploaded
clean, flagged music tracks cleared). Nothing left open on this plan.

**Still open, not part of this plan's scope but worth tracking:**
regenerating the banner at ≥2048x1152, and the two flagged music
tracks' manual listen (vocal/mood check).

---

## Plan — recurring character cast for retention

Every video currently invents a brand-new character (`ScriptGenerator.cs`
lines 50-54 say this explicitly: "the channel, not a mascot, is what
viewers are meant to recognise"). Research (both `research-kids-channels.md`
and fresh search this session) says the opposite is true for this genre:
a recognizable recurring character is the strongest loyalty/retention lever
CoComelon, Vlad and Niki, and Ryan's World all share, and Made-for-Kids CPM
ceilings mean retention matters more here than per-view yield. The pool
build-up/mix/reuse machinery in `CharacterSource.Resolve` already exists but
is tuned for variety (buildup 15, max 40, cooldown 10) rather than
recognizability. Goal: shrink to a small fixed cast that gets reused
consistently, and give each cast member a stable identity trait so it reads
as the "same" character across videos, without touching the per-scene
parallel-generation architecture (`todo.md` lines 59-84's rationale for one
reference frame per video stands untouched).

- [x] `GiggleGardenProfile.cs`: `CharacterPoolBuildupSize` 15→6,
      `CharacterPoolMaxSize` 40→6 (removes the 50/50 mixing zone - once 6
      are built, every video reuses from the cast of 6 rather than still
      inventing new ones), `CharacterPoolCooldown` 10→2 (meaningful rotation
      among only 6, vs. 10 which always fell through to full-list fallback).
- [x] `CharacterSource.cs`: add optional `Catchphrase` to `CharacterBrief`
      and the pool sidecar schema (`PoolEntry`); `FromPool` reads it,
      `SaveToPool` writes it from `script.CharacterCatchphrase`. Missing on
      existing sidecars (gigi-the-duckling, milo-the-fox, etc.) just
      deserializes to null - no migration needed.
- [x] `ScriptGenerator.cs`: add `VideoScript.CharacterCatchphrase` (nullable,
      not required - no throw if empty), add it to the JSON schema block,
      have the pooled-character prompt path tell the model this is a
      *returning* character and work its catchphrase in naturally, have the
      invention-path prompt (via `BuildCharacterInventionInstructions`) ask
      for one for brand-new characters, and override
      `script.CharacterCatchphrase` from the pool sidecar the same way
      Name/Description are already overridden (pool wins over model output).
      Update the stale "different character each video is the point" comment.
- [x] `GiggleGardenProfile.cs`: reword `BuildCharacterInventionInstructions`
      - it currently tells the model "the next video gets a different
        character entirely," which is no longer true once the cast is
        built; ask for `characterCatchphrase` too.
- [x] `dotnet build` clean.
- [x] Review section appended here once run.

## Review - recurring character cast for retention

`dotnet build` clean, 0 warnings/errors. Changes:
- `GiggleGardenProfile.cs`: pool knobs `CharacterPoolBuildupSize`/
  `CharacterPoolMaxSize` 15/40 → 6/6 (removes the mixing zone; once 6 exist
  every video reuses one instead of still inventing), `CharacterPoolCooldown`
  10 → 2. `BuildCharacterInventionInstructions` reworded off "the next video
  gets a different character entirely" and now also asks for
  `characterCatchphrase`.
- `CharacterSource.cs`: `CharacterBrief` and the pool sidecar (`PoolEntry`)
  both gained an optional `Catchphrase`; `FromPool` reads it, `SaveToPool`
  writes it from `script.CharacterCatchphrase`. Backward compatible - the 6
  existing sidecars (gigi-the-duckling, mascot-bunny, milo-the-fox,
  ruby-the-bunny, splash-the-otter, teddy-bear) have no `catchphrase` key and
  deserialize it as null with no error.
- `ScriptGenerator.cs`: added `VideoScript.CharacterCatchphrase` (nullable,
  not required - no throw on empty, unlike Name/Description), added to the
  JSON schema block, pooled-character prompt path now says "a returning
  character the audience already knows" and works the catchphrase in when
  one exists, the pool sidecar's catchphrase overrides whatever the model
  wrote (same as Name/Description already did). Updated the two comments
  that documented the old "different character every video, channel not
  mascot is what's recognised" design.

**Not done, and deliberately not part of this change:** no code touches how
the reference frame is generated or how scenes are seeded - the per-video
parallel `img2video` submission this was built around (`todo.md` 59-84)
still holds; reuse just means *picking* an existing pool portrait instead of
drawing a new one, same as it already did during the old "mix" zone.

**Still open before this pays off:**
- Untested end-to-end: no `--prep` run exercised the new pooled-character
  prompt path or the invention-path catchphrase field yet.

### Update - pool finalized (all 10 new characters + catchphrases)

All 10 user-generated characters (Pip, Nari, Quill, Kiwi née Coco, Bandit,
Sunny, Pebbles, Stretch, Willow, Hazel) came back from the "plain uncluttered
background" regeneration clean - ran `remove-background.py` on all 10
(71-84% of pixels cleared, in line with Pip's earlier-verified 80.7%),
visually spot-checked all 10 composited on a checkerboard (only the same
minor unclearable grass-shadow ellipse under the feet already accepted for
Pip). Finalized into the pool: kebab-case `.png` + `.json` sidecar
(`name`/`description`/`catchphrase`/`source`/`licence`) for each, raw source
JPEGs moved to `character-pool/_raw-source/` (non-recursive
`Directory.EnumerateFiles` in `CharacterSource.cs` doesn't see subfolders,
so this keeps them out of the enumerated pool without deleting anything).
Found and fixed a name collision along the way - the koala was independently
named "Coco" while the existing `mascot-bunny.json` is already "Coco the
Bunny" - renamed to "Kiwi the Koala" (file and JSON both) before it could
confuse two cast members with the same name.

Pool is now **16 characters**, all sidecar'd, no orphaned image or JSON on
either side (verified by directory diff). This is larger than the
`CharacterPoolMaxSize = 6` set earlier in this plan - that config only gates
when the *build-up* phase stops inventing new ones, it does not cap how many
images `FromPool` will pick from once reuse mode kicks in (pool size 16 ≥
max 6, so every video is now in "always reuse" mode, drawing from all 16).
Effectively the recurring cast is 16, not 6 - still a large reduction from
the old 40-character ceiling, but worth knowing rather than assuming 6.
Left as-is rather than trimmed, since deleting finalized character art
without being asked is not this task's call.

---

## Plan — full-codebase audit fixes (runtime bugs + dead code removal)

Source: two parallel background audits (VideoGen+Shared, Uploader+TokenCapture+
remotion-assembler), reviewed with the user, decisions taken per item below.

### Runtime bugs
- [ ] `Program.cs` `RunVisualLoopAsync` — `canReuse` ignores `SceneKind`; add a
      `lastSceneKind` check so a context (stock) scene can never reuse a
      character-art path or vice versa.
- [ ] `Uploader/Program.cs` — sidecar is saved only once after the whole
      per-video target loop; move to saving after every target's result so a
      mid-loop exception can't discard already-recorded successes.
- [ ] `Uploader/Publishing/TikTokPublisher.cs` — `ReadTikTokResponseAsync`'s
      `GetProperty("data")`/`GetProperty("publish_id")` etc. can throw
      `KeyNotFoundException`/`JsonException` on a malformed response, uncaught
      by `PublishAsync`'s catch clauses; add a catch that turns it into
      `PublishResult.Retry` like the transport-error case.
- [ ] `Uploader/Publishing/YouTubePublisher.cs` `GetServiceAsync` — no
      `IsNullOrWhiteSpace` guard on `ClientSecretPath`/`TokenStorePath` unlike
      the other three publishers; add one, mirroring `InstagramPublisher`'s
      pattern (`PublishResult.Fatal` before any I/O).
- [ ] `VideoGen/GenConfig.cs` `Validate()` — never checks `PexelsApiKey`
      despite `StockFootageClient` being live for every `AllowsContextScenes`
      profile; add the check, conditioned on `profile.AllowsContextScenes`
      (mirrors the existing `GroqApiKey`/`GeminiApiKey` conditional-reachability
      pattern). Requires passing `profile` into `Validate()`.
- [ ] `Uploader/Program.cs` — add bounded automatic retry for videos that hit
      the outer per-video catch (uncaught exceptions), instead of moving
      straight to `\failed`. Design: a small `.retry.json` marker file next to
      the video tracking `attempts`/`lastAttemptUtc`; on catch, increment and
      leave the video in place (do not move it) until `MaxCrashRetries` is
      exceeded, with a `CrashRetryBackoffMinutes` cooldown between attempts
      enforced by the watch-directory scan filter. Marker is deleted on a
      subsequent clean pass and added to `MoveWithSiblings`'s sibling list so
      it travels with the video if it's ever finally moved to `\failed`.
      (This is independent from the existing per-platform retry, which already
      works via `PublishStatus.Failed` staying non-terminal.)
- [x] TikTok inbox-draft vs `Published` gating — **user chose to keep current
      behavior**, no change.

### Video-quality decision
- [ ] Standardize frame rate across the whole pipeline at **30fps** (matches
      the native/expected rate for YouTube Shorts, TikTok and IG Reels — the
      25fps in the ffmpeg paths has no documented rationale, just an
      unexamined default): change the four `-r 25` occurrences in
      `VideoAssembler.cs` (`BuildVideoClipAsync`, `BuildImageClipAsync`,
      `BuildSplitImageClipAsync`, `BuildCrossfadedConcatAsync`) to `-r 30`, and
      add an explicit `Fps = 30` field to `RemotionAssemblyProps` (currently
      relies on `schema.ts`'s implicit `.default(30)` — makes the already-in-
      use value deliberate instead of accidental, no `schema.ts` change
      needed).

### Dead code removal (user approved removing now)
- [ ] `VideoGen/ImageClient.cs` — remove `GenerateAsync`,
      `GenerateWithReferenceAsync`, `OpenAiAsync`, `OpenAiEditAsync`,
      `StabilityAsync`, `StabilityImageToImageAsync`. Keep the static
      `BuildPrompt` and the `Orientation` enum (both still used).
- [ ] `VideoGen/VideoAssembler.cs` — remove `AssembleAsync`,
      `BuildImageClipAsync`, `BuildSplitImageClipAsync`, `ZoomPanFilter`,
      `ExtractLastFrameAsync` (all confirmed unreachable from `Program.cs` for
      both channels).
- [ ] `VideoGen/ScriptGenerator.cs` — remove `Scene.ImagePath2` and
      `Scene.VerticalImagePath2` (only ever read by the `AssembleAsync` being
      removed above; zero writers anywhere).
- [ ] `VideoGen/GenConfig.cs` — remove `SplitLongSceneAfterSeconds` (only
      appears in a comment) and `GenerateVerticalImages` (zero references
      outside its own declaration); remove the matching
      `"GenerateVerticalImages"` key from `appsettings.json`.
- [ ] `VideoGen/ViduClient.cs` — fix the stale header comment (lines 6-10)
      describing the old last-frame-chaining design; replace with the current
      shared-start-frame design already documented in `Program.cs`'s
      `RunViduLoopAsync` and `CharacterSource.cs`.
- [ ] `Uploader/Logger.cs` — remove unused `Warn` method (zero callers
      repo-wide).

### Verification
- [ ] `dotnet build` on both `VideoGen` and `Uploader` — 0 warnings, 0 errors.
- [ ] Diff review before reporting done.
- [ ] Ask before commit/push (standing rule — prior approvals don't carry
      forward to a new batch of changes).

---

## Per-channel path segregation (video output, work/script folders, done/failed)

Requested: `gigglegarden` and `chronicleandchaos` currently share every flat
folder (`VideoGen/work/job-*`, `D:\Business\Videos\*.mp4`,
`Videos\done`, `Videos\failed`), so there is no way to tell which video/script
belongs to which channel just by where it sits on disk. Deferred until the
in-flight chronicle generation finished; that run is done now (`8 maritime
disasters...` rendered into `D:\Business\Videos` on 2026-08-18), so this
starts.

### Current flat state (confirmed by listing, not guessed)

- `VideoGen/work/`: 6 job folders. 4 pre-date the `script.Profile` field
  (all gigglegarden content — Pip/Gigi mascots); one has `Profile:
  gigglegarden` explicitly; one has `Profile: chronicleandchaos` (today's
  maritime-disasters job). One empty job folder (`job-20260812-142302`, no
  script.json — a dead/abandoned run). Plus `dry-script/` and `tts-test/`,
  two flat diagnostic-only folders that never get read by `--resume`/
  `--assemble`/`--status` (they don't match the `job-*` glob).
- `D:\Business\Videos\`: 2 files at the root right now, both
  chronicleandchaos (`8-maritime-disasters...`, `Channel` confirmed in the
  sidecar JSON). `done\` holds 7 folders, all gigglegarden content (pre-date
  the `Channel` field, so it's blank in those sidecars — identity confirmed
  from filenames/titles instead). `failed\` is empty. `logs\` holds 4 daily
  log files mixing both channels' lines (already channel-prefixed per line,
  e.g. `[gigglegarden/youtube]`).

### Design

```
VideoGen\work\<channel>\job-YYYYMMDD-HHMMSS\...     (was: work\job-*)
VideoGen\work\dry-script\, work\tts-test\           (unchanged — diagnostic only, out of scope)
Videos\<channel>\*.mp4 / .json / .srt / .thumb.jpg  (was: Videos\*.mp4 flat)
Videos\<channel>\done\<video-name>\...              (was: Videos\done\*)
Videos\<channel>\failed\<video-name>\...            (was: Videos\failed\*)
Videos\logs\                                        (unchanged — shared, see note below)
```

`<channel>` is `profile.Id` (`"gigglegarden"` / `"chronicleandchaos"`).

**Logs stay flat/shared — deliberate, not an oversight.** Every log line is
already prefixed `[channel/platform]`, so one combined operational log stays
searchable; splitting it would only fragment "what happened this run" across
files with no benefit. Will split on request if that turns out wrong.

**`dry-script`/`tts-test` stay flat — deliberate, not an oversight.** They're
throwaway diagnostic output (`--dry-script`, `--test-tts`), never read by
`--resume`, `--assemble`, or `--status`, and don't match the `job-*` glob
those scan for. Not what "scripts when scripts are generated" refers to —
that's the real per-job `script.json` files under `work\<channel>\job-*\`.

### Code changes

**`VideoGen/Program.cs`**
- [ ] `RunPrepAsync` (job dir creation, ~line 136) and `RunManualAsync`
      (~line 491): job folder becomes
      `Path.Combine(cfg.WorkDirectory, profile.Id, $"job-{...}")`.
- [ ] `ResolveJobDirectory()`: explicit `--job` still resolves without
      needing `--profile` set correctly — tries the CLI-resolved profile's
      subfolder first, then falls back to searching every channel
      subfolder for a directory with that name. No-arg "newest job"
      default unions job folders across every channel subfolder before
      picking the newest, so `--resume`/`--assemble` with no `--job` keeps
      working regardless of which channel actually generated the latest job
      (today: if you don't pass `--profile`, it defaults to gigglegarden,
      but the newest real job is chronicleandchaos — this fixes that).
- [ ] Render section (~line 726): output goes to
      `Path.Combine(cfg.OutputDirectory, profile.Id, ...)` instead of
      `cfg.OutputDirectory` directly, for both the vertical and landscape
      renders.
- [ ] `RunStatusAsync` (~line 825): job-folder scan unioned across channel
      subfolders (same helper as `ResolveJobDirectory`); per-job
      done/failed/live-video lookup keyed off `script.Profile` (falls back
      to `"gigglegarden"` for the pre-migration jobs missing that field,
      matching where they're being moved to below).

**`Uploader/AppConfig.cs`**
- [ ] Drop `DoneDirectory`/`FailedDirectory` as fixed config properties —
      they were always a trivial `WatchDirectory\done` /
      `WatchDirectory\failed` convention, now channel-scoped instead:
      `DoneDirFor(channel)` / `FailedDirFor(channel)` →
      `WatchDirectory\<channel>\done` / `\failed`.

**`Uploader/Program.cs`**
- [ ] Startup directory creation: loop known channels
      (`cfg.Channels.Keys` ∪ `cfg.DefaultChannel`) instead of one
      done/failed pair.
- [ ] Video discovery (~line 74): scan `WatchDirectory\<channel>\*.mp4`
      per known channel (top-directory-only, so it never re-descends into
      that channel's own `done`/`failed`) instead of one flat
      `Directory.GetFiles(cfg.WatchDirectory, "*.mp4")`. Each discovered
      video now carries its origin channel from the folder it was found
      in.
- [ ] **Behavior change, called out explicitly:** the per-video `channel`
      used for publisher routing and the done/failed destination becomes
      the *origin folder's* channel instead of `sidecar.Channel ??
      cfg.DefaultChannel`. In every real case these already agree (VideoGen
      always writes `Channel = profile.Id` into the same folder it renders
      to), so this is a no-op for generated content. It only changes the
      one edge case — a manually dropped video with no sidecar — which now
      publishes through whichever channel folder you dropped it into
      instead of always `cfg.DefaultChannel`; `ResolveSidecarAsync`'s
      fallback-metadata path takes that channel as a parameter instead of
      hardcoding `cfg.DefaultChannel`.
- [ ] `MoveWithSiblings` call sites (done, failed, crash-path failed):
      destination becomes `cfg.DoneDirFor(channel)` /
      `cfg.FailedDirFor(channel)`. `channel` is resolved once at the top of
      the per-video try block (from the origin folder) so it's available
      even if the crash happens before the sidecar is resolved.

**`appsettings.json` (both projects)** — no changes needed; `WorkDirectory`/
`OutputDirectory`/`WatchDirectory` stay as roots, channel subfolders are
appended in code, not configured per-channel.

### Migration of existing files (one-time, done alongside the code change)

- [ ] `VideoGen/work/job-20260809-021127`, `-030142`, `-150322`,
      `-20260817-234145`, and the empty `job-20260812-142302` →
      `VideoGen/work/gigglegarden/`.
- [ ] `VideoGen/work/job-20260818-125747` (maritime disasters) →
      `VideoGen/work/chronicleandchaos/`.
- [ ] `Videos\8-maritime-disasters...*` (mp4/json/srt/thumb) →
      `Videos\chronicleandchaos\`.
- [ ] `Videos\done\*` (7 folders, all gigglegarden) →
      `Videos\gigglegarden\done\`.
- [ ] `Videos\failed\` — empty, nothing to move; recreated under each
      channel by the code above on next run.
- [ ] `Videos\logs\` — left in place (see design note above).

### Verification

- [ ] `dotnet build` on both `VideoGen` and `Uploader` — 0 warnings, 0
      errors.
- [ ] `VideoGen --status` (no Vidu/Claude spend) after migration reports the
      same outstanding items as before the move — proves the channel-unioned
      job scan and the done/failed lookup still find everything.
- [ ] `VideoGen --job job-20260818-125747` (no `--profile` passed) still
      resolves to the chronicleandchaos job under its new nested path —
      proves the "search other channels" fallback in `ResolveJobDirectory`.
- [ ] Confirm no orphaned files left behind at the old flat locations after
      migration (`Videos\*.mp4` at root, `VideoGen\work\job-*` at root).
- [ ] Diff review before reporting done.
- [ ] Ask before commit/push.

### Review

Implemented as designed. Code changes:
- `ContentProfileRegistry.cs`: exposed `Ids` (was private `All.Keys`) so
  Program.cs can walk every channel subfolder without hardcoding the two
  profiles.
- `VideoGen/Program.cs`: `RunPrepAsync`/`RunManualAsync` create
  `work\<channel>\job-*`; `ResolveJobDirectory()` tries the CLI-hinted
  channel first, then searches every channel for an explicit `--job`, and
  unions all channels (sorted by job-folder name, not full path) for the
  no-arg "newest" default; render output goes to
  `Videos\<channel>\...`; `RunStatusAsync` scans all channel subfolders and
  keys each job's done/failed/live lookup off `script.Profile` (defaulting
  to gigglegarden for the pre-migration jobs that predate that field).
- `Uploader/AppConfig.cs`: `DoneDirectory`/`FailedDirectory` replaced with
  `DoneDirFor(channel)`/`FailedDirFor(channel)` → `WatchDirectory\<channel>\
  done|failed`. `LogDirectory` untouched (flat/shared, as designed).
- `Uploader/Program.cs`: discovers videos per channel subfolder instead of
  one flat scan; the per-video channel is now the folder it was found in
  (checked, and `--failed`-routed, *before* `ResolveSidecarAsync` runs) —
  the intended behavior change from sidecar-derived to location-derived
  channel resolution. `ResolveSidecarAsync` takes the resolved channel
  instead of always falling back to `cfg.DefaultChannel`. All three
  `MoveWithSiblings` call sites and the `TopicHistory.AppendAsync` call
  switched to the folder-derived channel.

Migration: 5 pre-migration gigglegarden job folders + the empty
`job-20260812-142302` → `work\gigglegarden\`; the chronicleandchaos maritime
job → `work\chronicleandchaos\`; the live maritime-disasters render files →
`Videos\chronicleandchaos\`; the 7 `done\` folders → `Videos\gigglegarden\
done\`. `dry-script\`, `tts-test\`, `Videos\logs\`, and the stray
`job-20260818-125747-recovered-script.md` file were deliberately left in
place (all out of scope per the design notes above).

### Verification

- [x] `dotnet build` on `VideoGen` — 0 warnings, 0 errors.
- [x] `dotnet build` on `Uploader` — 0 warnings, 0 errors.
- [x] `VideoGen --status` after migration: 4 items flagged (2 for the
      never-rendered gigglegarden balloons job, 2 for the awaiting-approval
      chronicleandchaos maritime job) — the same set that was true before
      migration; none of the 7 already-published gigglegarden `done\`
      videos are (correctly) flagged.
- [x] `VideoGen --resume --job job-20260818-125747 --profile gigglegarden`
      (deliberately mismatched channel hint) resolved to
      `work\chronicleandchaos\job-20260818-125747` via the fallback search,
      confirmed by the printed Workspace line — proves `ResolveJobDirectory`
      no longer depends on the CLI-supplied `--profile` being correct. Zero
      spend: every scene was already on disk.
- [x] No orphaned files at the old flat locations: `VideoGen\work\` root has
      no `job-*` folders left, `Videos\` root has no `.mp4` files left.
- [x] Diff reviewed.
- [ ] Commit/push — not yet asked.
