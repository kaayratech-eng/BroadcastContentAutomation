# Graph Report - .  (2026-08-03)

## Corpus Check
- 53 files · ~65,672 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 111 nodes · 141 edges · 22 communities (21 shown, 1 thin omitted)
- Extraction: 79% EXTRACTED · 20% INFERRED · 1% AMBIGUOUS · INFERRED: 28 edges (avg confidence: 0.87)
- Token cost: 69,275 input · 0 output

## Community Hubs (Navigation)
- Video Pipeline Docs and Ops
- Channel Brand Identity Artwork
- YouTube Uploader Program
- FFmpeg Video Assembly
- Graphify Knowledge Graph Workflow
- Uploader Project Dependencies
- Script Generation Types
- AI Image Generation Client
- VideoGen Project Dependencies
- Text-to-Speech Client
- TokenCapture Project Dependencies

## God Nodes (most connected - your core abstractions)
1. `VideoGen project` - 11 edges
2. `GiggleGardenOfficial Brand Artwork` - 10 edges
3. `Uploader project` - 9 edges
4. `VideoAssembler` - 6 edges
5. `Graphify Knowledge Graph Workflow` - 6 edges
6. `Logger` - 5 edges
7. `GenConfig` - 5 edges
8. `VideoScript` - 5 edges
9. `GiggleGarden YouTube Automation System` - 5 edges
10. `Human Approval Gate` - 5 edges

## Surprising Connections (you probably didn't know these)
- `GiggleGarden automation setup guide (project copy)` --semantically_similar_to--> `GiggleGarden YouTube Automation System`  [INFERRED] [semantically similar]
  gigglegarden-automation/README.md → README.md
- `Gigi the little duckling (channel mascot)` --conceptually_related_to--> `GiggleGarden YouTube Automation System`  [AMBIGUOUS]
  graphify-out/transcripts/gigi-colors-en.txt → README.md
- `Uploader Debug build output manifest` --implements--> `Uploader project`  [INFERRED]
  gigglegarden-automation/Uploader/obj/Debug/net8.0/Uploader.csproj.FileListAbsolute.txt → README.md
- `Gigi Colors (EN) video transcript` --conceptually_related_to--> `Claude script writing and topic selection`  [AMBIGUOUS]
  graphify-out/transcripts/gigi-colors-en.txt → README.md
- `Gigi Colors (EN) video transcript` --conceptually_related_to--> `VideoGen project`  [INFERRED]
  graphify-out/transcripts/gigi-colors-en.txt → README.md

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **VideoGen generation pipeline (script to TTS to images to render)** — readme_videogen, readme_claude_script_writing, readme_azure_speech_tts, readme_openai_scene_images, readme_ffmpeg_render, readme_sidecar_json [EXTRACTED 1.00]
- **Daily publish flow gated on human approval** — readme_task_scheduler_daily_flow, readme_videogen, readme_videos_output_directory, readme_sidecar_json, readme_approval_gate, readme_uploader, readme_privacystatus_private_first [EXTRACTED 1.00]
- **YouTube OAuth credential and quota stack shared by TokenCapture and Uploader** — readme_tokencapture, readme_uploader, readme_appsettings_configuration, gigglegarden_automation_uploader_obj_release_net8_0_publishoutputs_c870a95c69_google_youtube_api_dependency, readme_youtube_quota_budget [INFERRED 0.85]
- **Kids Channel Visual Identity System** — generated_image_flat_vector_illustration_style, generated_image_warm_pastel_palette, generated_image_square_centered_composition, generated_image_gigglegardenofficial_wordmark, generated_image_kids_content_audience [INFERRED 0.85]
- **Garden Scene Cast and Environment** — generated_image_two_child_characters, generated_image_anthropomorphic_flowers, generated_image_garden_setting, generated_image_decorative_sparkle_motifs [EXTRACTED 1.00]

## Communities (22 total, 1 thin omitted)

### Community 0 - "Video Pipeline Docs and Ops"
Cohesion: 0.16
Nodes (25): GiggleGarden automation setup guide (project copy), TokenCapture Debug build output manifest, Uploader Debug build output manifest, Google.Apis.YouTube.v3 client dependency, Uploader publish manifest to D:\Business\UploaderApp, Uploader Release build output manifest, Gigi the little duckling (channel mascot), Gigi Colors (EN) video transcript (+17 more)

### Community 1 - "Channel Brand Identity Artwork"
Cohesion: 0.29
Nodes (12): AI-Generated Image Asset, Anthropomorphic Smiling Flowers, Decorative Sparkle and Leaf Motifs, Flat Vector Illustration Style with Thick Outlines, Green Hill Garden Setting, GiggleGarden Channel Brand Identity, GiggleGardenOfficial Brand Artwork, GiggleGardenOfficial Wordmark (+4 more)

### Community 2 - "YouTube Uploader Program"
Cohesion: 0.22
Nodes (7): AppConfig, List, JsonOpts, Logger, VideoMetadata, JsonSerializerOptions, string

### Community 3 - "FFmpeg Video Assembly"
Cohesion: 0.38
Nodes (3): GenConfig, Task, VideoAssembler

### Community 4 - "Graphify Knowledge Graph Workflow"
Cohesion: 0.38
Nodes (7): GRAPH_REPORT.md architecture review doc, graphify explain command, graphify path command, graphify query command, graphify update (AST-only refresh), graphify wiki index navigation, Graphify Knowledge Graph Workflow

### Community 5 - "Uploader Project Dependencies"
Cohesion: 0.29
Nodes (6): net8.0, Google.Apis.YouTube.v3 (1.68.0.3414), Microsoft.Extensions.Configuration (8.0.0), Microsoft.Extensions.Configuration.Binder (8.0.2), Microsoft.Extensions.Configuration.Json (8.0.0), Microsoft.NET.Sdk

### Community 6 - "Script Generation Types"
Cohesion: 0.38
Nodes (5): List, Task, Scene, ScriptGenerator, VideoScript

### Community 8 - "VideoGen Project Dependencies"
Cohesion: 0.33
Nodes (5): net8.0, Microsoft.Extensions.Configuration (8.0.0), Microsoft.Extensions.Configuration.Binder (8.0.2), Microsoft.Extensions.Configuration.Json (8.0.0), Microsoft.NET.Sdk

### Community 9 - "Text-to-Speech Client"
Cohesion: 0.40
Nodes (3): Dictionary, Task, TtsClient

### Community 10 - "TokenCapture Project Dependencies"
Cohesion: 0.50
Nodes (3): net8.0, Google.Apis.YouTube.v3 (1.68.0.3414), Microsoft.NET.Sdk

## Ambiguous Edges - Review These
- `GiggleGarden YouTube Automation System` → `Gigi the little duckling (channel mascot)`  [AMBIGUOUS]
  graphify-out/transcripts/gigi-colors-en.txt · relation: conceptually_related_to
- `Claude script writing and topic selection` → `Gigi Colors (EN) video transcript`  [AMBIGUOUS]
  graphify-out/transcripts/gigi-colors-en.txt · relation: conceptually_related_to

## Knowledge Gaps
- **19 isolated node(s):** `net8.0`, `Google.Apis.YouTube.v3 (1.68.0.3414)`, `Microsoft.NET.Sdk`, `AppConfig`, `net8.0` (+14 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **1 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **What is the exact relationship between `GiggleGarden YouTube Automation System` and `Gigi the little duckling (channel mascot)`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **What is the exact relationship between `Claude script writing and topic selection` and `Gigi Colors (EN) video transcript`?**
  _Edge tagged AMBIGUOUS (relation: conceptually_related_to) - confidence is low._
- **Why does `VideoScript` connect `Script Generation Types` to `FFmpeg Video Assembly`?**
  _High betweenness centrality (0.007) - this node is a cross-community bridge._
- **Are the 2 inferred relationships involving `GiggleGardenOfficial Brand Artwork` (e.g. with `AI-Generated Image Asset` and `GiggleGarden Channel Brand Identity`) actually correct?**
  _`GiggleGardenOfficial Brand Artwork` has 2 INFERRED edges - model-reasoned connections that need verification._
- **What connects `net8.0`, `Google.Apis.YouTube.v3 (1.68.0.3414)`, `Microsoft.NET.Sdk` to the rest of the system?**
  _19 weakly-connected nodes found - possible documentation gaps or missing edges._