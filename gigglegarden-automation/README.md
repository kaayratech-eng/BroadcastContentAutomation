# GiggleGarden YouTube Automation — Windows / .NET 8

Three projects:
- **VideoGen** — generates finished videos (script → TTS → images → FFmpeg).
- **TokenCapture** — run once, saves the refresh token.
- **Uploader** — runs daily via Task Scheduler.

## 1. One-time setup

```powershell
cd TokenCapture
dotnet run -- "C:\Secure\GiggleGarden\client_secret.json"
```
Browser opens → sign in as the channel-owner account → approve.
You should see `Refresh token saved.`

## 2. Configure the uploader

Edit `Uploader\appsettings.json`:
- `TokenStorePath` — set YOURUSER to your Windows username (must match where TokenCapture saved it).
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
dotnet run -- --topic "counting ducks" --language en
dotnet run -- --language hi          # Claude picks the topic
dotnet run -- --language pa
```

Each run produces BOTH a 1920x1080 video and a 1080x1920 Short in
`D:\Business\Videos`, each with a sidecar JSON at `approved:false`.
**Watch the video before flipping approved to true.** Generated visuals for
kids' content need your eyes every time — this gate is deliberate.

## Cost per video (approx)

- Script (Claude): ~$0.02
- TTS (Azure): free tier initially, then ~$0.05
- Images (7 scenes, OpenAI medium): ~$0.30
- Total: well under $0.50/video

## Full daily flow once everything is wired

Task Scheduler job 1 (e.g. 6am): run VideoGen (one video, rotating language).
You: watch outputs, flip approved:true (10 seconds each).
Task Scheduler job 2 (e.g. 4pm): run Uploader — uploads everything approved.
