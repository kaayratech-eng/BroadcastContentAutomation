# GiggleGarden YouTube Automation — Windows / .NET 8

Three projects:
- **VideoGen** — generates finished videos (script → TTS → images → FFmpeg).
- **TokenCapture** — run once, saves the refresh token.
- **Uploader** — runs daily via Task Scheduler.

## 1. One-time setup — run ONCE per channel, not per video

`TokenCapture` takes the client secret **and a channel name**. The channel name
picks which subfolder the refresh token is saved into, so each channel gets its
own token and one channel's capture never overwrites another's:

```powershell
cd TokenCapture
dotnet run -- "C:\Secure\GiggleGarden\client_secret.json" gigglegarden
```
Browser opens → sign in as GiggleGarden's channel-owner account → approve.
You should see `Refresh token saved for channel "gigglegarden".`

Then, separately, for the second channel:
```powershell
dotnet run -- "C:\Secure\GiggleGarden\client_secret.json" chronicleandchaos
```
Browser opens again → sign in as **Chronicle & Chaos's** channel-owner account
(a different Google account/YouTube channel) → approve.
You should see `Refresh token saved for channel "chronicleandchaos".`

Both tokens now live side by side under `%APPDATA%\GiggleGarden\<channel>\` and
never expire from normal use (Google refresh tokens are long-lived unless
revoked). **The Uploader reuses them automatically on every run — you do not
re-run TokenCapture per video or per upload, only once per channel, ever**
(and again only if you revoke access or the token stops working).

## 2. Configure the uploader

Edit `Uploader\appsettings.json` → `Channels.<channel>.YouTube.TokenStorePath`
for each channel — must match the folder TokenCapture saved into above
(`%APPDATA%\GiggleGarden\gigglegarden`, `%APPDATA%\GiggleGarden\chronicleandchaos`).
- `AnthropicApiKey` — from https://console.anthropic.com (only needed if you use auto-generated metadata).
- Keep `PrivacyStatus: "private"` until the end-to-end test passes.

## 3. Test end-to-end (do this before scheduling anything)

1. Drop ONE short test .mp4 into `D:\Business\Videos`.
2. `cd Uploader && dotnet run`
3. First run generates `<video>.json` next to the file and stops (approval gate).
4. Open the JSON, review title/description/tags, set `"approved": true`.
5. `dotnet run` again → video uploads as **private**.
6. Check it in YouTube Studio: metadata correct, made-for-kids flag set.
7. Only then change `PrivacyStatus` to `"public"`.

## 4. Schedule the daily run

```powershell
cd Uploader
dotnet publish -c Release -o D:\Business\UploaderApp
```
Task Scheduler → Create Task:
- Trigger: Daily at your chosen time (kids' content peaks ~3–7pm local).
- Action: Start a program → `D:\Business\UploaderApp\Uploader.exe`
- "Run whether user is logged on or not" + "Wake the computer to run this task".

## How the approval gate works

Every auto-generated metadata file lands as a sidecar JSON with `"approved": false`.
Nothing uploads until you flip it to `true`. This is your 10-second human glance —
keep it on for kids' content. If you write your own sidecar JSON up front
(title/description/tags/approved:true), the trend+Claude step is skipped entirely.

## Quota notes

- Each upload ≈ 1,600 units; each trend search ≈ 100 units; daily cap 10,000.
- `MaxUploadsPerRun: 3` keeps you comfortably under the cap.

## Sidecar JSON format

```json
{
  "title": "Five Little Ducks | Fun Counting Song for Kids",
  "description": "English para...\n\nHindi para...\n\nPunjabi para...\n\n#kids #nurseryrhymes",
  "tags": ["nursery rhymes", "kids songs", "बच्चों के गाने"],
  "approved": true
}
```

---

# VideoGen — automated video creation

## Prerequisites (one-time, your side)

1. **FFmpeg**: download from https://www.gyan.dev/ffmpeg/builds/ (release full), extract,
   add the `bin` folder to PATH. Verify: `ffmpeg -version` in a new terminal.
2. **Azure Speech key**: portal.azure.com → Create resource → "Speech" → Free F0 tier
   is fine to start (0.5M chars/month free). Copy Key + Region into appsettings.
3. **OpenAI API key**: platform.openai.com → API keys (for scene illustrations).
4. **Anthropic API key**: console.anthropic.com (for script writing).
5. **Background music**: put a royalty-free loop at `D:\Business\VideoGen\assets\music.mp3`.
   Use YouTube Audio Library (free, safe) — pick something marked "no attribution required".
6. Font: `NirmalaB.ttf` ships with Windows and covers Devanagari + Gurmukhi. No action needed.

## Usage

```powershell
cd VideoGen
dotnet run -- --topic "counting ducks" --language en                      # uses GenConfig's default profile
dotnet run -- --language hi          # Claude picks the topic
dotnet run -- --language pa
dotnet run -- --profile chronicleandchaos --topic "the myth of Icarus"    # explicit channel
```

`--profile gigglegarden` or `--profile chronicleandchaos` picks which channel
the video is *for* (omit it and it falls back to `VideoGen/appsettings.json`'s
`Profile` setting). That choice gets stamped into the video's sidecar JSON as
`"channel"` automatically — you don't answer this question again at upload
time. The Uploader reads that field and picks the matching channel's
credentials from `Channels.<channel>` in its own `appsettings.json` with no
further input from you.

Each run produces BOTH a 1920x1080 video and a 1080x1920 Short in
`D:\Business\Videos`, each with a sidecar JSON at `approved:false`.
**Watch the video before flipping approved to true.** Generated visuals for
kids' content need your eyes every time — this gate is deliberate.

## Cost per video (approx)

Measured from real OpenAI usage data, not estimated - the image count below is
what actually gets generated per video with the current config, not the
original 7-images/video design this section was first written for.

- Script (Claude): ~$0.02
- TTS (Azure): free tier initially, then ~$0.05
- Images (OpenAI gpt-image-1, medium, ~$0.13 each): 17-21+ per video -
  2 character-reference images, 2 per scene (landscape + vertical, via
  `GenerateVerticalImages`), 1 thumbnail, plus 2 more for every scene whose
  narration exceeds `SplitLongSceneAfterSeconds` - roughly **$2.25-2.75**
- Total: roughly **$2.30-2.85/video**, not $0.50 - see `GenConfig.cs` for the
  toggles (`GenerateVerticalImages`, `UseCharacterReference`,
  `SplitLongSceneAfterSeconds`) if you want to trade image count for spend

## Full daily flow once everything is wired

Task Scheduler job 1 (e.g. 6am): run VideoGen (one video, rotating language).
You: watch outputs, flip approved:true (10 seconds each).
Task Scheduler job 2 (e.g. 4pm): run Uploader — uploads everything approved.
