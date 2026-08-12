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
