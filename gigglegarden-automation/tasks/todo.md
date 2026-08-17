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
- [ ] User creates the new Google Cloud project + OAuth consent screen
      (test user: chronicleandchaos's own email only) + Desktop OAuth
      client, saves client_secret.json at the path above
- [ ] User re-runs TokenCapture for `gigglegarden` (existing project) to
      replace the dead token
- [ ] User runs TokenCapture for `chronicleandchaos` against the new project
- [ ] User creates a new Meta app for Chronicle & Chaos (Business type),
      adds Instagram Graph API product, generates a long-lived Page token
      for Chronicle & Chaos's Page, fills in
      `Channels.chronicleandchaos.{Facebook,Instagram}` + the token in
      appsettings.Local.json
- [ ] User creates a new TikTok Developer app for Chronicle & Chaos,
      adds Content Posting API product, gets ClientKey/ClientSecret
- [x] Correction: `TikTokTokenCapture` already existed (commit `4c83cb1c`,
      predates this conversation) — a `Glob` false-negative briefly made it
      look missing. It already does the full PKCE + local-redirect OAuth
      flow and already takes an optional per-channel token-store-path arg.
      No code change needed, mirrors YouTube's TokenCapture exactly.
- [ ] User runs TikTokTokenCapture for both `gigglegarden` and
      `chronicleandchaos` once their respective TikTok apps exist (each app
      must register redirect URI `http://localhost:53682/callback` exactly)
- [ ] README updated with the Meta/TikTok separate-app steps and
      TikTokTokenCapture usage
