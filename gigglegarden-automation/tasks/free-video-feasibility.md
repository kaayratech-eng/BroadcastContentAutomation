# Free video generation — feasibility report

**Verdict: no-go for now.** Vidu stays exactly as-is. This is a research report
only — nothing in this file changes pipeline behavior. See `IVideoProvider`
(`VideoGen/IVideoProvider.cs`) for the zero-behavior-change seam that was added so
a future provider *could* be dropped in without a rewrite, if a trigger condition
below is ever met.

Constraints this report was evaluated against (from the task): no new paid
provider, no automating a consumer chat UI or violating a tool's ToS, API/self-host/
local only. All findings below are sourced and dated (Aug 2026).

## Current baseline (what's being compared against)

`ViduClient` (`VideoGen/ViduClient.cs`) — Vidu image-to-video, one fixed character
reference frame per video reused across all 9 scenes (not frame-chained — see the
comment at `Program.cs:170-176`), billed per submit-call credits, off-peak delivery
up to 48h. Zero hardware, zero ops, zero code maintenance beyond the REST client
already written. This is the bar every free alternative below has to clear or beat.

## Options evaluated

### Qwen Chat (chat.qwen.ai) — disqualified (ToS)
Free to a human, but [it's a consumer web UI with no official free API](https://medium.com/data-science-in-your-pocket/free-unlimited-ai-video-generation-using-qwen-chat-486c430a5612).
Automating it means scripting a consumer chat UI, which the task's hard constraint
forbids outright. Alibaba's real API (DashScope/Model Studio) is paid — [the free
developer tier ended April 15 2026, replaced by a one-time 1M-token/90-day trial](https://yangmao.ai/en/providers/qwen/free-api/),
not a recurring free allowance. Character consistency also doesn't hold across
independent Qwen Chat generations. Disqualified on two independent grounds
(automation ToS + no durable free API), consistency not evaluated further.

### Meta AI / Movie Gen / "Vibes" — disqualified (ToS)
Meta's video model is **Movie Gen** (30B params, up to 1080p/16s with synced audio),
but [Meta has explicitly stated it is not releasing Movie Gen for open developer
use](https://ai.meta.com/research/movie-gen/) — no public API exists. The only
access point is **Vibes**, a consumer feed in the Meta AI app / meta.ai, launched
2026. Identical shape to the Qwen Chat finding: free to a human, zero programmatic
access. Automating it would mean scripting a consumer app/web UI — forbidden by the
same rule that rules out Qwen Chat. Not evaluated further on quality/consistency;
the automation gate alone is disqualifying.

### Self-hosted Wan 2.2 (Apache 2.0, Hugging Face) — the one credible free path
The only open option found with genuine character-consistency conditioning: supports
image-to-video and [first/last-frame reference anchoring](https://stable-diffusion-art.com/wan-2-2-first-last-frame-video/),
which could replicate (or improve on) the current single-reference-frame approach.

Hardware reality: T2V-1.3B fits in ~8GB VRAM, but that's text-to-video only. The
useful I2V variants need real hardware — TI2V-5B wants 24GB+, I2V-A14B wants 80GB.
No consumer GPU under $1,000 covers the useful variant; the realistic path is cloud
GPU rental (RTX 4090 ~$0.34–0.69/hr, A100 80GB ~$0.50–1.49/hr). "Free" here means
free model weights, not free compute — there is a real, ongoing cash cost, just paid
to a GPU host instead of Vidu.

### LTX-Video (open, Lightricks) — viable but weaker for character work
Multi-keyframe conditioning, faster than Wan, lower VRAM floor (12GB minimum, 16GB+
comfortable with quantization). Quality/consistency for character-driven work is
generally considered a notch below Wan 2.2.

### SVD / AnimateDiff — not competitive
Neither is dead, both are surpassed for this use case. Stable Video Diffusion
explicitly struggles with faces — a real problem for a character-driven kids
channel. AnimateDiff is SD1.5-generation quality. Not evaluated further.

### Bifurcate-to-5s-and-stitch strategy
Considered as a way to fit any of the above into shorter, cheaper generation units:
roughly doubles clip count (9 scenes × ~9s → ~18 five-second chunks), doubling both
GPU-time and the number of independent-generation seams where character drift can
creep in. ffmpeg (already in the pipeline, `VideoAssembler.cs`) trivially handles
the stitching for free — that part is a non-issue. Net effect versus the current
single-reference Vidu approach: **worse on consistency risk, worse on generation
count, only wins on being free** — and that win still requires owning/renting GPU
hours to realize.

## Recommendation table

| Option | Cost | Character consistency | Automatable (ToS-clean) | Hardware needed |
|---|---|---|---|---|
| Vidu (current) | Pay-per-clip credits | Good (fixed reference frame) | Yes (existing API) | None |
| Qwen Chat | Free (consumer) / paid API | Poor (no cross-gen consistency) | **No** — consumer UI only | None |
| Meta AI Vibes | Free (consumer) | Unknown, untested | **No** — consumer UI only | None |
| Wan 2.2 (self-host) | Free weights + GPU rental (~$0.34–1.49/hr) | Good (first/last-frame anchoring) | Yes (open weights, own infra) | 24–80GB VRAM |
| LTX-Video (self-host) | Free weights + GPU rental | Fair | Yes | 12–16GB+ VRAM |
| SVD / AnimateDiff | Free weights + GPU rental | Weak (face artifacts) | Yes | Varies |

## Go/no-go

**No-go for now.** Nothing free/API-able matches Vidu's zero-maintenance,
zero-hardware, pay-per-clip simplicity. Wan 2.2 self-hosted is the only technically
credible free-video path found, and it trades the *narration* cost savings this
task actually chases for a real (if modest) cash GPU-rental cost plus real ops
burden (managing weights, inference server, queueing, uptime) — not a net win.

**Revisit if:**
- You already have or plan to rent a 24GB+ GPU for another reason (the marginal
  cost of also running Wan 2.2 on it would then be close to zero), or
- Wan (or an equivalent open model) ships a low-cost hosted API that removes the
  self-hosting ops burden while staying cheaper than Vidu at your volume, or
- Vidu's pricing or reliability changes enough that the current cost/effort
  comparison flips.

No pipeline code was changed to support this — `IVideoProvider` (Part 4) exists
purely as an inert seam for whichever of these (or another option) becomes worth
adopting later.
