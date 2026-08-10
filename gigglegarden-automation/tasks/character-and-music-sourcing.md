# Sourcing free, monetization-safe character art + background music

Status: **research complete, nothing downloaded, nothing approved yet.** No OpenAI
credits or Vidu credits spent as part of this. This is the input to a decision, not
a decision.

Requested: instead of paying OpenAI to draw a new character per video, use free
CC0/public-domain cartoon character art pulled from the internet — but only if the
license is genuinely safe for a monetized, cross-posted (YouTube/Instagram/
TikTok/Facebook) kids' channel. Same question for background music, where nothing
was ever finalized (the music pool is still empty).

Research was done by two independent agents, each verifying license terms by
reading the actual license/terms page on the source itself rather than trusting a
search snippet or the site's reputation. Full detail from both is below; short
version first.

---

## Short version

**Characters — best bet: [Kenney.nl](https://kenney.nl/assets/animal-pack-redux).**
Site-wide CC0, verified directly across multiple asset pages, no attribution, no
caps, vector source files (so it scales cleanly to whatever resolution the pipeline
needs instead of looking blown-up). Animal Pack Redux gives 10 animals in 8 style
variants each — likely enough to seed the whole pool without repeating a look.
Style is flat/simplified rather than the painterly look OpenAI draws today — worth
you looking at actual samples before committing (see links below) since that's the
one thing research can't settle for you.

Backup/supplement: hand-picked **CC0 items on OpenGameArt.org** (verify the license
badge on every single item, it's a mixed-license site) — the Cute Teddy Bear and
Mascot Bunny character pages are good genre fits with a proper idle-pose reference
image. `freesvg.org` and `OpenClipart.org` are usable supplementary CC0 vector
sources once you accept the quality varies per contributor.

**Avoid:** Vecteezy free tier (hard $1,000 video-budget cap, plus mandatory
attribution — a bad fit for an ongoing channel), Freepik free tier (mandatory
attribution, and the agent couldn't even get a stable read of their current
license page), Wikimedia Commons "cartoon character" categories (dominated by
actual studio-owned mascots like Scooby-Doo — trademark risk regardless of any
copyright tag), CraftPix's paid "Cute Cartoon Animal 2D Game Characters" set
(visually the closest match to today's OpenAI style, but it's a paid product with
its own per-item terms, not free).

**Music — best bet: build the pool from CC0-only tracks** — [Chosic's CC0
filter](https://www.chosic.com/free-music/all/?attribution=no) (has dedicated
"Lullaby" and "Children" genre pages, cross-check those specifically overlap the
CC0 filter before picking), OpenGameArt's CC0 audio collections (weaker style fit —
skews game/cinematic, expect to sift for gentle tracks), and FMA/free-stock-music.com's
CC0-tagged tracks (both are mixed-license aggregators, so the license must be
verified per track, not per site).

CC0 beats every attribution-required option here for a specific reason: **Bensound,
Uppbeat, and Incompetech are all legitimate and commercial-safe, but each is
Content-ID-registered specifically so a missing or mismatched attribution code
triggers an automatic claim** — and Incompetech's catalog has a documented history
of getting claimed via unrelated third-party registrations even when attribution is
done correctly. With ~1 video/day this is a real operational risk, not a
hypothetical one — CC0 removes it entirely because there's no attribution string
that can be forgotten or mistyped.

**Hard excludes, not just "avoid": TikTok's Commercial Music Library and
Instagram/Meta's Sound Collection.** Both are explicitly platform-locked in their
own terms — a track from either cannot legally travel to the other 3 platforms.
Since GiggleGarden renders once and publishes everywhere, using either for the
shared render would be an outright license violation, not a risk to manage.

**One thing to flag before you approve anything:** none of the free-image sources
match OpenAI's current painterly character style — they're flatter, more
"game-sprite," which may actually suit the "cute 2D flat-color" brief in
`CharacterStyle` (`GenConfig.cs`) better than you'd expect, or may look like a
downgrade depending on taste. Worth a quick look at 3-4 real samples before
picking a direction. I can pull specific image URLs for you to eyeball next, without
downloading anything into the repo, if that's useful.

---

## What I'd need from you to move forward

1. Which character source(s) to build the pool from (Kenney alone, or Kenney +
   hand-picked OpenGameArt/freesvg/OpenClipart items).
2. Which music source(s) to build the pool from (CC0-only, or CC0 + one of the
   attribution-required options if you're fine with the operational overhead).
3. Go-ahead to actually download the specific files once you've seen samples —
   nothing gets pulled into `assets/character-pool/` or `assets/music/` before that.

---

## Full research: character art

### Trustworthiness ranking

1. **Most trustworthy — genuine site-wide CC0, verified directly:** Kenney.nl,
   OpenClipart.org, freesvg.org, publicdomainvectors.org (see caveat below), CC0-tagged
   items on OpenGameArt.org and itch.io.
2. **Usable, strings attached:** CraftPix.net freebies (no attribution, but raw-file
   resale/redistribution banned — fine for use *in* a video, not for repackaging the
   assets themselves).
3. **Not safe as a free option for this use case:** Vecteezy free tier ($1,000
   video-budget cap + mandatory attribution), Freepik free tier (mandatory
   attribution, license page unstable at research time), Pixabay (no longer true
   CC0 since April 2023 — a similar but distinct "Content License," no attribution
   needed but don't call it CC0 internally), game-icons.net (CC BY, mandatory
   per-icon attribution, and it's icons not characters — wrong content type anyway).
4. **Exclude as a category:** Wikimedia Commons "cartoon character" categories —
   dominated by actual trademarked studio characters; even a PD-tagged image of one
   carries live trademark risk independent of its copyright status.

### Candidates

| # | Source | License | Attribution | Commercial/monetized OK | Format/res | Style | Flags |
|---|---|---|---|---|---|---|---|
| 1 | [Kenney.nl — Animal Pack Redux](https://kenney.nl/assets/animal-pack-redux) | CC0 | None | Yes | PNG sprites + vector source, multi-res | Flat, simplified cute animals — giraffe, panda, parrot, penguin, monkey, rabbit, snake, hippo, pig, elephant, 8 variants each | None |
| 2 | [Kenney.nl — Toon Characters 1](https://kenney.nl/assets/toon-characters-1) | CC0 | None | Yes | 270 assets, vector source | Human-style toon characters, not animals | None — lower relevance (humans not animals) |
| 3 | [OpenGameArt — Mascot Bunny Character](https://opengameart.org/content/mascot-bunny-character) | CC0 | None | Yes | ZIP 3.4MB, res not listed | Chibi cartoon bunny, idle/run/jump/hurt/dead frames | None |
| 4 | [OpenGameArt — Free CC0 Modular Animated Vector Characters 2D](https://opengameart.org/content/free-cc0-modular-animated-vector-characters-2d) | CC0 | None | Yes | Vector, 2048×2048 canvas | Fantasy-adventurer style, modular/recolorable | Style is generic-fantasy, not toddler-cute — needs adaptation |
| 5 | [OpenGameArt — Textured Cute Monster Pack](https://opengameart.org/content/textured-cute-monster-pack) | CC0 | None | Yes | FBX/OBJ/Blend (3D) | Cute lowpoly creatures — crabs, penguins, mushrooms, bees | 3D not 2D — needs rendering to flatten |
| 6 | [OpenGameArt — Cute Teddy Bear Character](https://opengameart.org/content/cute-teddy-bear-character) | CC0 | None | Yes | PNG sprites, ZIP 5.1MB | Cute teddy bear, full animation set | None — good genre/mood fit |
| 7 | [OpenClipart.org](https://openclipart.org) (site-wide, browse by tag) | CC0 1.0 | None | Yes | SVG (vector) | Crowd-sourced, quality varies per artist | Some uploads historically mis-tagged as CC0 when derivative — spot-check the uploader |
| 8 | [freesvg.org — Cartoon Animal](https://freesvg.org/cartoon-animal) | CC0 | None | Yes | SVG + some PNG | Cartoon animals, quality varies per contributor | CC0 excludes any recognizable trademark/logo even if hosted here |
| 9 | [publicdomainvectors.org](https://publicdomainvectors.org/en/cartoon-character-clip-art-images) | Claimed CC0 | Claimed none | Claimed yes | SVG/raster | Large aggregator catalog | **Unverified** — site blocked direct fetch (403); known in the stock-art community as an aggregator that doesn't always verify original provenance. Re-check any specific file's license page before use. |
| 10 | [itch.io — Animals Asset Pack (styloo)](https://styloo.itch.io/animals) | CC0 | None | Yes | FBX/GLB (3D) | Low-poly stylized animals | 3D format mismatch for a flat-2D pipeline |
| 11 | [itch.io — Tiny Creatures (clintbellanger)](https://clintbellanger.itch.io/tiny-creatures) | CC0 | None | Yes | 16×16 pixel sprites | Tiny pixel-art creatures | Resolution/style mismatch — too small/iconic for a toddler-cartoon look |
| 12 | [CraftPix.net freebies](https://craftpix.net/freebies/free-top-down-animals-farm-pixel-art-sprites/) | Custom "File License" (not CC0) | None required | Yes | PNG + PSD, layered | Pixel-art farm animals | Cannot resell/redistribute the raw asset files themselves; pixel-art style not flat-vector |
| 13 | [Vecteezy free tier](https://www.vecteezy.com/licensing-agreement) | Vecteezy Free License | **Required** | Capped — $1,000 production budget, no resale | Varies | Not evaluated per-asset | **Not recommended free** — budget cap is a bad fit for an ongoing channel; pay for Pro tier or skip |
| 14 | [Freepik free tier](https://www.freepik.com) | Freepik Free | **Required** ("Designed by Freepik" credit) | Yes, with credit | Varies | Not evaluated per-asset | **Not recommended** — mandatory visible credit is awkward in a wordless toddler video; agent couldn't get a stable read of the current license page (redirected/404'd twice) |
| 15 | [Pixabay illustrations/vectors](https://pixabay.com/service/license-summary/) | Pixabay "Content License" (not CC0 since Apr 2023) | None | Yes | Varies | Not evaluated per-asset | Functionally similar to CC0 for this use but a different legal instrument — don't label it CC0 internally |
| 16 | [game-icons.net](https://game-icons.net) | CC BY 3.0 | **Required per icon** | Yes | SVG, monochrome | Icons, not characters | Wrong content type — exclude |
| 17 | [Wikimedia Commons "cartoon characters"](https://commons.wikimedia.org/wiki/Category:Cartoon_characters) | Mixed, per-file | Varies | Not generalizable | Varies | Dominated by trademarked studio mascots (Scooby-Doo, Mighty Mouse, etc.) | **Exclude as a category** — trademark risk independent of any CC0/PD tag |

### For reference — CraftPix's paid set (not free, noted because it's the closest style match found)

[Cute Cartoon Animal 2D Game Characters](https://craftpix.net/sets/cute-cartoon-animal-2d-game-characters/) —
hand-drawn, flat, colorful (kittens, pandas, koalas, red pandas, etc.) — visually
the closest match to the channel's current painterly style, but it's a priced
product with its own license terms to check at purchase. Only worth it if the free
CC0 options don't look right in practice.

---

## Full research: background music

### Trustworthiness ranking, specifically for a 4-platform cross-post

The dominant filter here isn't license generosity, it's **platform-lock** — a
track only usable on the platform it came from is worthless for a pipeline that
renders once and publishes to YouTube + Instagram + TikTok + Facebook from the
same file.

1. **Hard excludes — platform-locked, confirmed in their own terms:**
   - **TikTok Commercial Music Library** — TikTok's own terms: "Commercial Uses
     outside of TikTok are not permitted and no rights are granted by TikTok for
     such uses."
   - **Instagram/Meta Sound Collection** — cleared for Facebook + Instagram only;
     does not travel to YouTube or TikTok (based on consistent secondary reporting;
     no single stable Meta terms URL was found to confirm firsthand, but confidence
     is high given multiple independent sources agree).
2. **Best — CC0, platform-agnostic by design, zero attribution risk:**
   [Chosic CC0 filter](https://www.chosic.com/free-music/all/?attribution=no) (has
   dedicated "Lullaby Background Music" and "Children Background Music" genre
   pages — cross-check those overlap the CC0 filter, they don't automatically),
   OpenGameArt CC0 audio collections (weaker style fit, skews
   game/cinematic/dramatic), Free Music Archive's CC0-tagged tracks (mixed-license
   aggregator, verify every individual track), free-stock-music.com's CC0 filter
   (mixed-license aggregator, same caveat).
3. **Usable but operationally heavier — legitimate, attribution required per
   track+video:**
   - **Bensound** — monetized use explicitly allowed with their Free License, but
     needs "the attribution text and its unique license code... generated when
     downloading," and per Bensound's own guidance **a fresh code is needed per
     track-per-video**.
   - **Uppbeat** — explicitly names YouTube/TikTok/Instagram/Facebook as permitted
     distribution in their own terms (one of the few sources that does), but the
     free tier needs a separate "Uppbeat Credit" generated per download, per video.
     Their "Channel Safelist" feature (pre-clearing a channel so credit isn't
     needed) is worth asking Uppbeat about directly if this source is chosen.
   - **YouTube Audio Library** — Google states outright these tracks "won't be
     claimed by a rights holder through the Content ID system," which is the
     strongest guarantee found in this research — but it's YouTube-only (no public
     catalog without a Studio login, no guarantee for Instagram/TikTok/Facebook
     uploads of the same file). Good as a supplemental source for the YouTube
     upload specifically, not a whole-pipeline solution.
4. **Legitimate license, real Content-ID risk anyway — the one to be most
   careful with:** **incompetech.com (Kevin MacLeod)**. The CC-BY 4.0 license is
   completely real and free, and MacLeod himself confirms monetization is fine
   with attribution — but **third-party distributors have repeatedly registered
   his catalog in Content ID without his involvement**, with a documented case of
   correctly-attributed content still getting claimed/pulled and requiring a
   multi-day dispute naming the specific (wrong) rights-holder. Incompetech's own
   site has a help page acknowledging this. Legitimate license, real practical
   risk — flagged specifically because this is the "did everything right and
   still got claimed" scenario.
5. **Likely paid-only for this use case:** freetouse.com — their free tier is
   explicitly scoped to individual "User-Generated Content," not content "on
   behalf of a company, brand, or organization." A monetized branded channel very
   plausibly falls outside that free carve-out; would need written confirmation
   from freetouse.com before relying on their free tier.
6. **Legally clean but not claim-proof:** Pixabay Music — no attribution required,
   commercial use allowed, but Pixabay's own FAQ admits "some tracks might trigger
   Content ID claims... resolvable by showing your Pixabay license" — i.e. even a
   no-attribution track can still get an automated claim requiring a manual
   dispute.

### Candidates

| # | Source | License | Attribution | Cross-platform (YT+IG+TikTok+FB) | Content-ID risk | Style fit |
|---|---|---|---|---|---|---|
| 1 | [YouTube Audio Library](https://support.google.com/youtube/answer/3376882) | Per-track: "Standard" (no attribution) or CC-BY | None, or required per-track | **YouTube only** — no guarantee elsewhere | Google states no Content ID claims on YouTube specifically | Has Calm/Kids/ambient filters (unverified without Studio login) |
| 2 | [Pixabay Music](https://pixabay.com/service/license-summary/) | Pixabay License | None | Yes, not platform-scoped | Pixabay's own FAQ: some tracks can still trigger claims | Has dedicated "lullaby"/"instrumental lullaby" categories — good fit |
| 3 | [Free Music Archive](https://freemusicarchive.org/License_Guide) | Mixed per track — CC0/CC-BY/CC-BY-NC/etc. | None (CC0) or required (CC-BY) | Yes for CC0/CC-BY tracks | Low if genuinely CC0 | Broad, not kids-curated — expect manual sifting; **verify every track's badge, NC variants are common here** |
| 4 | [incompetech.com](https://incompetech.com/music/royalty-free/licenses/) | CC-BY 4.0 | Required, exact text on FAQ | Yes, license is platform-agnostic | **Documented third-party Content ID claims despite correct attribution** | Strong stylistic fit (soft piano/acoustic/whimsical), but see risk note above |
| 5 | [Bensound](https://www.bensound.com/faq) | Free w/ attribution, or paid no-attribution | Required — unique code generated per download, valid for one video | Yes, terms not platform-scoped | Bensound registers tracks in Content ID themselves — missing/wrong code = claim | Strong fit — gentle acoustic guitar/ukulele/piano |
| 6 | [Uppbeat.io](https://uppbeat.io/user-agreement) | Free w/ credit, or paid subscription no-credit | Required per track per video (free tier) | **Yes — explicitly names all 4 platforms in their terms** | Same pattern as Bensound — missing credit risks a claim | Not independently browsed; purpose-built stock library, likely has kids/lullaby filters |
| 7 | [Chosic CC0 filter](https://www.chosic.com/free-music/all/?attribution=no) | CC0 | None | Yes | Lowest — no attribution string to get wrong | Has "Lullaby" (27 tracks) and "Children" (129 tracks) genre pages — best named fit found, verify CC0 overlap |
| 8 | [OpenGameArt CC0 audio](https://opengameart.org/content/good-cc0-audio) | CC0 | None | Yes | Low | Weak fit — skews chiptune/cinematic/game-RPG, not lullaby |
| 9 | [freetouse.com](https://freetouse.com/license) | Free (individual UGC only) or paid Commercial Plan | Required (free tier) | Not explicitly platform-scoped, but see legal caveat | Unverified | Not evaluated — likely needs the **paid** tier for a branded channel |
| 10 | [free-stock-music.com](https://www.free-stock-music.com/) | Mixed, CC0 filter available | None (CC0) or required (CC-BY) | Explicitly names YT/FB/IG/Twitch/X/Vimeo; **TikTok not confirmed** | Low for CC0 tracks | Not independently browsed — general stock library |
| 11 | TikTok Commercial Music Library | TikTok CML terms | N/A | **No — platform-locked, confirmed in TikTok's own terms** | N/A | **Excluded** |
| 12 | Instagram/Meta Sound Collection | Meta terms (secondary sources) | N/A | **No — platform-locked to Facebook/Instagram** | N/A | **Excluded** |

---

## Research method note

Both tracks were researched by independently reading each source's own license/
terms page rather than relying on the site's reputation or a search snippet.
Two pages could not be fetched directly (Chosic's `/free-music-policy/` and
free-stock-music.com's FAQ both returned HTTP 403) — those entries rely on
search-indexed content instead and are flagged above for a manual re-check before
anything is downloaded from them. `publicdomainvectors.org` has the same caveat.
