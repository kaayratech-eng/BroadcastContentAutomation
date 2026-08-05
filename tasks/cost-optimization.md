# Cost & Automation Suggestions (Phase 4)

Written proposals only — nothing here is implemented. Current baseline per the
README: ~$0.02 script + ~$0.05 TTS (after free tier) + ~$0.30 images ≈ <$0.50/video.

Kids-content constraint applies to every suggestion below: nothing here should
reduce script/visual quality or safety review to the point it risks
inappropriate output, and every suggestion keeps the mandatory human-approval
gate untouched.

---

## 1. Right-size the Claude models

**Replaces:** `claude-opus-5` in `VideoGen/GenConfig.cs` (script writing), and reconsider `claude-sonnet-4-6` in `Uploader/AppConfig.cs` (fallback metadata generation).

Both IDs are real, current models — this isn't fixing a bug, it's a cost lever. Opus 5 is Anthropic's most expensive tier ($5 input / $25 output per million tokens) for a task that's short, structured, and doesn't need frontier reasoning: 6-8 rhyming sentences plus image prompts, JSON-only output, explicit constraints already in the prompt (character limits, age range, format).

- **Tradeoff:** `claude-sonnet-5` (near-Opus quality, $3/$15, intro pricing $2/$10 through 2026-08-31) is a safe first step down — minimal quality risk for a well-constrained creative-writing task. `claude-haiku-4-5` ($1/$5) is worth A/B testing for the Uploader's metadata-generation fallback specifically (title/description/tags from a filename + trend data — a simpler task than full script writing), since that path only runs when no sidecar exists.
- **Effort:** trivial — one string change per config, then watch script quality over a handful of real runs before committing.
- **Fits the workflow:** no pipeline changes, same approval gate, same JSON contract. Purely a cost/quality dial.
- **Estimated savings:** output tokens dominate cost here (scripts run long relative to the prompt). Moving script generation from Opus to Sonnet is roughly a 40% cut on that call; Haiku on the metadata fallback is roughly 5x cheaper than Sonnet on that smaller call. Both are already a small slice of the ~$0.50/video total, so the dollar savings are modest — this is more about not overpaying for headroom the task doesn't use.

## 2. Batch the script-generation call

**Replaces:** the synchronous `POST /v1/messages` call in `ScriptGenerator.GenerateAsync`.

Since `VideoGen` already runs on a schedule (not interactively — the README's own "Full daily flow" describes a 6am unattended run), the real-time latency of a synchronous Claude call isn't needed. Anthropic's Message Batches API processes requests asynchronously at **50% off** standard token pricing.

- **Tradeoff:** requires restructuring `VideoGen`'s flow to submit a batch and poll for the result (batches typically complete within an hour, not instantly) — a bigger change than #1 for a smaller absolute saving, since script generation is already the cheapest line item (~$0.02/video). Worth it only if you're scaling to many videos/day; not worth the engineering effort at 1 video/day.
- **Effort:** medium — changes the control flow of `VideoGen/Program.cs`.
- **Fits the workflow:** works fine with the existing "generate overnight, review in the morning" cadence; doesn't work if you ever want on-demand single-video generation with `--topic`.

## 3. Try the already-built Stability AI image path before scaling image spend

**Replaces (optionally):** OpenAI `gpt-image-1` as the default `ImageProvider` in `VideoGen/GenConfig.cs`.

Images are the single largest cost line (~$0.30/video, the majority of total spend), and `ImageClient.cs` already has a working, unused Stability AI code path (`StabilityAsync`) — it just needs an API key. Stability's per-image pricing is typically lower than OpenAI's image API.

- **Tradeoff:** character consistency (the recurring duckling "Gigi" across every scene and every video) is the real risk — commercial APIs with strong prompt-following (OpenAI) tend to hold a described character more consistently than switching providers without also switching to a reference-image or fine-tuning workflow. Recommend a **side-by-side quality comparison on a handful of scenes** before switching the default, not a blind swap.
- **Effort:** low to test (code already exists) — just add `StabilityApiKey` and flip `ImageProvider` for a test run.
- **Fits the workflow:** no pipeline changes; it's a config flip that's already wired in.

**Separate, lower-risk lever on the same line item:** `GenerateVerticalImages: true` doubles image generation (7 scenes × landscape + portrait = 14 images/video instead of 7). The code comment in `GenConfig.cs` already documents this tradeoff explicitly (quality vs. spend) — if the vertical Shorts underperform the landscape videos on your channel, turning this off is a ~50% cut on the largest cost line with an already-understood quality tradeoff, no new tooling required.

## 4. Leave TTS alone for now

Azure Speech's free tier (0.5M characters/month) almost certainly covers a low-volume schedule (per the README's own "Full daily flow": 1 video/day). Self-hosted open-source alternatives (Piper, Coqui) exist and are free, but neural voice quality and language coverage for Hindi and Punjabi specifically (`hi-IN-SwaraNeural`, `pa-IN-VaaniNeural` are polished, purpose-built neural voices) are a real risk for a multilingual kids' channel where narration clarity matters. **Recommend revisiting only if you exceed the free tier** — not proactively, since the switching cost (voice quality risk) outweighs the (currently zero, since you're in the free tier) dollar saving.

## 5. Compliance flag tied to the C1 multi-platform rollout (not a cost item, but time-sensitive)

If you proceed with `tasks/todo.md` item C1 (wiring up Instagram/Facebook/TikTok publishing), note that YouTube's "Made for Kids" declaration doesn't have a direct equivalent enforced the same way on the other platforms. TikTok in particular has stricter, distinct rules around content targeting or appealing to children (age-gating, limited features on minor/under-13-flagged accounts, restricted ad personalization). **Before flipping any of those publishers live, research each platform's current kids/minors content policy** — this isn't something the code can guard against; it's a policy-compliance step on your side, separate from and prior to the engineering work in C1.
