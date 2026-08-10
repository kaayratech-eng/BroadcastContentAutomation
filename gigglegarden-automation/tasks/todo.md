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
