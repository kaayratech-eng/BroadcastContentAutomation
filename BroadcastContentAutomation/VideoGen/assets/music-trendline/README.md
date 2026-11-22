# Trendline Now — background music pool

Rotation pool for the Trendline Now daily-news channel. Each video picks a
different track from this folder so no single bed is reused back-to-back
(Rishi, 2026-11-21). The claimed bed from video #1
(`alex-morgan-tense-suspense-rising-dread-573934.mp3`, claimed by Elite
Alliance Music as "Tense Music - Birthday PAPA") is NOT in this folder and
must not be used — it stays in `music-mythology/tense-atmospheric/` as
evidence only.

## Active pool — Free Music Archive (downloaded 2026-11-21)

These five tracks are the active rotation pool. All were downloaded via
direct `files.freemusicarchive.org` URLs (the FMA track pages expose a
`fileUrl`; the `/download/` endpoint needs a sign-in, the direct file URL
does not). All are instrumental, no vocals, verified MP3 with `ffprobe`.

| File | Title — Artist | Mood / use | FMA page | Duration | License |
|---|---|---|---|---|---|
| `tense-newsroom-01.mp3` | broken-suspense-master — John Bartmann | Tense / serious + political stories (main bed, replaces the claimed track) | https://freemusicarchive.org/music/John_Bartmann/100-ambient-atmospheric-soundtracks-straylight-drones-collection/broken-suspense-master/ | 4:04 | **CC0 (public domain)** — no attribution, no restrictions |
| `tense-newsroom-02.mp3` | dystopia-master — John Bartmann | Tense / dramatic variant | https://freemusicarchive.org/music/John_Bartmann/100-ambient-atmospheric-soundtracks-straylight-drones-collection/dystopia-master/ | 4:04 | **CC0 (public domain)** — no attribution, no restrictions |
| `uplifting-tech-01.mp3` | Rising Light (Uplifting Orchestral) — AurosonMusic | Uplifting / energetic — tech, business, positive stories | https://freemusicarchive.org/music/aurosonmusic/single/rising-light-uplifting-orchestral/ | 2:26 | **CC BY 4.0** — attribution required (see below) |
| `calm-neutral-01.mp3` | Old chantry — Serat | Calm / neutral — general news | https://freemusicarchive.org/music/serat/piano-textures/old-chantry-1/ | 3:10 | **CC BY 4.0** — attribution required (see below) |
| `cinematic-epic-01.mp3` | A Hero for our Time — Geoff Harvey (Purple Planet Music) | Cinematic / epic — big dramatic stories | https://freemusicarchive.org/music/geoff-harvey-purple-planet-music/epic-missions/a-hero-for-our-timemp3/ | 2:15 | **CC BY 4.0** — attribution required (see below) |

Durations verified with `ffprobe` on 2026-11-21. The two John Bartmann
tracks are both 4:04 by design (the album renders every track to the same
length) but are different audio — confirmed by decoded-audio hash, and by
their ID3 titles. All five are ≥90s, so they meet the pipeline's floor.

## Attribution (CC BY 4.0 — REQUIRED for three tracks)

The two John Bartmann tracks are CC0 and need no credit. The other three
are CC BY 4.0: the video description MUST credit them, or the pipeline
must append a music-credit line. Exact credit lines:

- `Music: "Rising Light (Uplifting Orchestral)" by AurosonMusic — https://freemusicarchive.org/music/aurosonmusic/single/rising-light-uplifting-orchestral/ — CC BY 4.0`
- `Music: "Old chantry" by Serat — https://freemusicarchive.org/music/serat/piano-textures/old-chantry-1/ — CC BY 4.0`
- `Music: "A Hero for our Time" by Geoff Harvey (Purple Planet Music) — https://freemusicarchive.org/music/geoff-harvey-purple-planet-music/epic-missions/a-hero-for-our-timemp3/ — CC BY 4.0`

Until the uploader appends this automatically, add the line for the track
used to that video's description. A video using a CC0 track needs no line.

## Content ID note

FMA does not publish a Content ID registration status the way Pixabay does,
so none is listed above. These are CC BY / CC0 licences from the artists
themselves: a Content ID claim on one would be disputable with the licence
(CC0 tracks have no rights holder to claim at all). This is not the same
as the Elite Alliance Music block on video #1's Short — that was a
non-FMA track with a block policy.

## Why not Pixabay (superseded picks, NOT downloaded)

The five Pixabay picks below were selected 2026-11-21 by highest download
count within mood, but Pixabay downloads are BLOCKED: Pixabay's Cloudflare
download verification never completes in the cloud browser or via curl, so
no MP3 could be saved. Do NOT go back to Pixabay for these — the FMA pool
above replaces them. Kept here for reference only.

| File (not present) | Title — Artist | Pixabay page | Downloads* | Duration | Content ID |
|---|---|---|---|---|---|
| `tense-newsroom-01.mp3` | Tense — leberch | https://pixabay.com/music/suspense-tense-626199/ | 1,061 | 1:58 | Registered |
| `tense-newsroom-02.mp3` | Tense — leberch | https://pixabay.com/music/suspense-tense-626524/ | 1,026 | 3:50 | Registered |
| `uplifting-tech-01.mp3` | Background Music — HitsLab | https://pixabay.com/music/corporate-background-music-634364/ | 5,380 | 5:30 | Registered |
| `calm-neutral-01.mp3` | Documentaries Piano — leberch | https://pixabay.com/music/solo-piano-documentaries-piano-628035/ | 1,265 | 2:12 (confirmed in browser) | Registered |
| `cinematic-epic-01.mp3` | Storm Zone (Epic Version, full version) — Pink_Sound | https://pixabay.com/music/orchestral-storm-zone-epic-background-instrumental-music-for-video-full-version-587218/ | 367 | 4:38 | Registered |

\* Download counts as shown on Pixabay on 2026-11-21.

## Sources tried

- **Pixabay** — BLOCKED (Cloudflare download verification never completes
  in the browser or via curl; `cdn.pixabay.com` direct URLs are not
  exposed on track pages).
- **Mixkit** (`mixkit.co`) — BLOCKED (Cloudflare 403 on the site and on
  `assets.mixkit.co`; no direct download path found).
- **Free Music Archive** (`freemusicarchive.org`) — WORKS. Track pages
  load over plain curl and expose a direct `fileUrl` on
  `files.freemusicarchive.org` (the `/download/` endpoint needs a
  sign-in, the file URL does not). Licence check: most FMA tracks are
  CC BY-NC (not usable for a monetised channel) or CC BY-ND (no
  derivatives — not usable since the bed is looped/mixed). Only CC BY and
  CC0 were picked. Avoid Alex Morgan tracks: the claimed video #1 bed was
  an Alex Morgan file, so his catalogue carries claim risk even under CC BY.

## How the pipeline uses this folder

The video assembler normalises any bed to the configured background-music
LUFS at mix time, so mastering level does not matter. Tracks are looped
under the narration, so a track longer than the 1–3 min target is fine;
the only hard floor is ~90 seconds.
