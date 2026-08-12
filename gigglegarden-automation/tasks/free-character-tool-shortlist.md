# Free character-creation tool shortlist

Report only — nothing here has been generated, downloaded, or committed to
`assets/character-pool/`. This is for your review; art only gets added to the pool
after you sign off on a specific tool/output.

## Why this exists

`assets/character-pool/` (`CharacterPoolPath`) is populated offline by a person,
not by an API call in the pipeline — each entry is one character image plus a
same-named `.json` sidecar (name + appearance description). That description text
gets injected into every scene's image prompt (`ScriptGenerator.cs:610-611,636`),
so a bad source image doesn't just look wrong once — it corrupts every scene
generated from it. This is exactly what happened with the 4 Kenney.nl "face badge"
assets removed from the pool this pass (Part 5): small head-only crops whose
sidecar literally said "framing the face," which would have driven headshot-only
art on every scene of any video that picked them.

## Hard criteria

1. **Full-body, standing, whole-character-visible** — the bar set by
   `gigi-the-duckling.png` (1080×1920, full body) already in the pool — not a
   portrait/bust/face-only crop. A tool that can't be steered to full-body output
   reliably is marked unsuitable regardless of how good its style is otherwise.

2. **No background — character on transparent, not a scene or flat backdrop.**
   Pool art gets animated by Vidu, and a flat/plain backdrop baked into the
   source image reads as flat in the finished video too. Vidu should be the one
   generating the scene behind the character, not the character source image.
   None of the shortlisted tools below produce transparent output directly (all
   return a flat-color or gradient backdrop), so this is handled as a fixed
   post-step, not a tool-selection criterion: run every candidate through
   `VideoGen/tools/remove-background.py <in.png> <out.png>` before it goes in
   the pool. That script is local/offline (Pillow + NumPy + SciPy, no API, no
   cost) — border-color sampling + connected-component analysis so it only
   clears the background region touching the image edge, not same-colored
   pixels inside the character (teeth, eye highlights, etc). Only the
   transparent output ever gets added to `assets/character-pool/`; the flat-
   background original is a discardable intermediate.
   This applies to pool art only — the on-the-fly `CharacterSource.cs` path
   (used when `CharacterPoolPath` is empty) is a separate mechanism and is not
   part of this rule: `VideoAssembler.FitToPortraitAsync` fills the frame by
   blurring a copy of the source image itself as its own backdrop, so it has
   nothing to composite a transparent character against without further work.

## Shortlist

### Canva (Magic Media / Dream Lab) — usable, needs explicit prompting
Free tier includes a limited number of AI image generations per month (Magic Media);
Pro tier raises the cap. General-purpose text-to-image, not a dedicated "character
generator" — style presets (e.g. "flat illustration," "cartoon") help match
`CharacterStyle`'s "flat colors, thick outlines" look. **Full-body**: achievable by
prompting explicitly for "full body, standing pose, feet visible" — same as this
pipeline's own `CharacterSource.cs:104-108` prompt already does for invented
characters — but not guaranteed by default; a lazy prompt will often return a
bust/portrait crop. Consistency across multiple generations of the "same" character
is not guaranteed (no reference-image conditioning on the free tier) — usable for a
single hero pose, not for a multi-pose set of one character without manual curation.

### ToonyTool (toonytool.com) — mismatch, not recommended
On closer look this is a comic/scene **composer** — drag-and-drop stock characters,
backgrounds, and speech bubbles into panels — not a from-scratch AI character
generator. It doesn't generate new original character art at all, so the full-body
criterion doesn't even apply: there's nothing to generate. Not a fit for producing
an original, style-consistent mascot; dropping this from further consideration.

### OpenArt — usable, best free-generation-volume option
Offers a meaningful number of free generations per day on base Stable-Diffusion-
family models (no hard monthly cap like Canva's). **Full-body**: same as Canva —
achievable with explicit "full body, standing" prompting, not default behavior.
Supports style-reference images on some free-tier models, which helps keep multiple
generated characters visually consistent with `CharacterStyle` and with each other,
closer to what the pool actually needs (many different characters that all still
look like they belong to the same channel).

### Adobe Firefly — usable, free daily cap
Free tier grants a limited number of "generative credits" per month, refreshing
monthly; a "cartoonize"/illustration style preset exists. **Full-body**: same
prompting caveat as above — Firefly's default framing tends toward centered
portraits unless full-body is explicitly requested. Firefly's commercial-use
licensing terms are notably clear/generous (Adobe indemnifies commercial use of
Firefly-generated content on paid plans; free-tier terms are more restrictive —
this would need a direct terms check before assuming it's safe for a monetized
YouTube channel, not confirmed here).

### Picsart — usable, but most restrictive free tier
Free AI character/avatar generation exists but with the tightest free-generation
limits of this shortlist and heavier upsell pressure toward paid credits.
**Full-body**: possible with explicit prompting, same as the others; no meaningful
advantage over Canva or OpenArt found to justify its tighter limits.

## Recommendation

**OpenArt first, Canva second.** Neither is a slam dunk — none of these tools
default to full-body output, all require the operator to explicitly prompt for it
every time (a discipline the invented-character path already has to follow, so this
is a known, manageable step, not a new problem). OpenArt's daily-refreshing free
quota and style-reference support make it the better fit for building out a pool of
several distinct, consistent-feeling characters over time; Canva is the fallback if
OpenArt's output style doesn't match `CharacterStyle` well enough in practice.
ToonyTool is dropped — wrong category of tool entirely.

**Next step, pending your approval:** pick one tool, generate 2-3 candidate
full-body characters, and review them against `gigi-the-duckling.png` side-by-side
before anything is added to `assets/character-pool/`.

**Update:** done via OpenArt's MCP connector (`gpt-image-2`, low-quality/1k/9:16,
5 credits each) — three candidates generated and approved: Ruby the Bunny, Milo
the Fox, Splash the Otter. All three were run through
`VideoGen/tools/remove-background.py` per the no-background criterion above and
added to `assets/character-pool/` with `.json` sidecars.
