---
trigger: always_on
description: TempScore project facts — pipeline, repo layout, hardware, constraints and the 10-hour build schedule. Always active so every agent session starts grounded in the real project instead of guessing.
---

# TempScore — project context

TempScore turns a 60-second video into the same video with an original, offline-generated soundtrack. Everything runs on one laptop (Intel Core Ultra 5 336H NPU, NVIDIA RTX PRO 500 Blackwell 6 GB, 32 GB RAM). Nothing may ever leave the machine — no cloud calls, no telemetry, no network access outside `127.0.0.1`.

**`notes/TempScore-Implementation-Guide.md` at the repo root is the single source of truth** — full pipeline spec, code snippets, prompts, JSON schemas, risk/fallback table. Read it before writing or reviewing code. Never invent an API shape, file path, or prompt that conflicts with it.

## Pipeline (six stages)

1. **Extract frames** — ffmpeg, CPU. One frame every 3s + cut-detection timestamps.
2. **Describe frames** — Windows AI `ImageDescriptionGenerator`, NPU.
3. **Summarize** — Phi Silica (`LanguageModel`), NPU. Frame descriptions + cuts → scene JSON.
4. **Plan the music** — Phi Silica, NPU. Scenes → music plan JSON (style, bpm, key, segments).
5. **Generate music** — Python FastAPI service on `127.0.0.1:8765`, GPU/CUDA. MusicGen (safe default) or ACE-Step 1.5.
6. **Mux** — ffmpeg, CPU. Original video + generated score → `output.mp4`.

## Repo layout

```
tempscore/
├── TempScore.Core/      # Shared pure-logic library: Frame/Scene/MusicPlan/Segment
│                        # records, JSON parsing, validation, fallback table.
│                        # Multi-targeted net8.0;net10.0. Built/tested on the Mac;
│                        # the WinUI app below takes a ProjectReference to this
│                        # exact library — it is never copied into app/.
├── app/                 # WinUI 3, packaged, C#
│   ├── Services/        # FrameExtractor, FrameDescriber, SceneSummarizer, MusicPlanner, MusicClient, Muxer
│   └── MainPage.xaml(.cs)
├── music_service/       # FastAPI: /health, /generate; engines/musicgen.py, engines/acestep.py
├── samples/             # 3 test clips: dialogue, action, calm
└── README.md
```

`app/` has no `Models/` folder of its own — the Frame/Scene/MusicPlan/Segment records live only in `TempScore.Core/Models/`, consumed via `ProjectReference`. C# app and Python service talk only over HTTP to `127.0.0.1`. Every run writes to `%LOCALAPPDATA%\TempScore\runs\<timestamp>\` (frames, descriptions.json, summary.json, plan.json, score.wav, output.mp4) — this is the debugging/replay mechanism, keep it intact.

## Hard constraints

- Windows AI APIs require **package identity** — stay a packaged WinUI 3 app, never unpackaged.
- Every stage degrades gracefully. Phi Silica JSON failures get one retry, then a rule-based fallback — **the pipeline must never hard-stop**.
- `-c:v copy` on mux — never re-encode video.
- Music service binds to `127.0.0.1` only, never `0.0.0.0`.

## Risks and fallbacks

| Risk | Fallback |
| --- | --- |
| Phi Silica / Image Description unavailable | Phi-3.5/4-mini via Foundry Local; Phi vision ONNX via Windows ML |
| Broken JSON from Phi Silica | One retry, then rule-based mood→prompt table |
| ACE-Step too slow / OOM on 6 GB | MusicGen small or medium |
| Segments sound disconnected | Shared style/BPM/key in every prompt + 1s crossfade, or one ACE-Step call |
| Generation slow on stage | Pre-generated demo clip; live-regenerate only |
| Hardware/driver fails on stage | Recorded backup video |

## The 10-hour compressed build schedule (owned by `pm`)

The guide's own plan assumes a 24-hour hackathon. Compressed to ~10 hours by running agent-assisted lanes in parallel instead of one person working sequentially:

| Window | Focus | Milestone |
| --- | --- | --- |
| H0.0–1.0 | Smoke tests A–D; lock MusicGen vs ACE-Step | Engine decided |
| H1.0–3.0 | `architect` freezes JSON contracts; `senior-dev` builds C# + Python scaffolding in parallel | Both sides compile against the same shapes |
| H3.0–5.0 | SceneSummarizer + MusicPlanner prompts, defensive parsing, fallbacks; `qa` writes malformed-JSON cases alongside | Fallback paths proven to trigger |
| H5.0–6.0 | Muxer + full wiring | First end-to-end `output.mp4` |
| H6.0–7.5 | WinUI 3 UI, orchestrator progress, regenerate-from-plan | Demo-shaped app |
| H7.5–8.5 | `qa` on 3 test clips, `security` offline/sandbox pass, `code-reviewer` full-diff pass | Findings fixed |
| H8.5–9.5 | Mood slider, offline badge if time remains; record backup demo video | Safety net exists |
| H9.5–10.0 | Two dry-runs of the 3-minute demo script, offline | Go/no-go |

## Agents available in this repo

Six subagents live in `.agents/agents/`: `pm`, `architect`, `senior-dev`, `qa`, `security`, `code-reviewer`. Delegate to the matching one via `invoke_subagent` rather than improvising.

## Model tier and blocking authority

| Agent | Model tier | Blocking gate? |
| --- | --- | --- |
| `pm` | inherit (mid) | Yes, on scope — never overrides `security` |
| `architect` | pro | Yes — contracts change only through this agent |
| `senior-dev` | pro | No — implements and iterates until others are clean |
| `qa` | flash → pro for the H7.5–8.5 full pass | Yes, per stage |
| `security` | pro, never flash | **Yes, always — mandatory, no schedule exception** |
| `code-reviewer` | inherit (mid) | Yes, on correctness |

`security`'s sign-off on the offline boundary (network bind, subprocess argument safety, content-safety filters) is the one gate `pm` can never schedule around.

## Repeatable workflows

Three manual rules — `@`-mention them to pull the full instructions into a turn — turn the schedule above into invokable commands instead of prose:

- **`@review-stage`** — run the board (`security` mandatory, `architect`, `code-reviewer`, `qa`, `pm`) against a finished stage or the full pipeline. Never ships anything, only reports.
- **`@ship-stage`** — the full single-stage loop: `architect` confirms the contract, `senior-dev` implements, `review-stage` runs, `pm` scope-adjusts.
- **`@ship-parallel-lanes`** — for H1.0–3.0 (or any other file-disjoint split): `architect` freezes the shared contract first, then two `senior-dev` lanes build concurrently (C# pipeline vs Python music service), each reviewed independently and reconciled by `pm`.

Adapted from a larger multi-agent CI/CD governance setup in `other-project/` — its ticket-queue workflow (`ship-queue`) and GitHub-PR-metadata-waiver workflow (`review-pr-disjoint`) were left out, since there's no ticket backlog or GitHub PR to apply them to here, and its CI pipeline was left out because this repo isn't pushed anywhere yet. The model-tiering, mandatory-security-gate, and named-workflow patterns were worth keeping; the Linear/npm/auto-merge machinery wasn't.

## Attribution (Copilot/Antigravity-authored work only)

When Antigravity itself opens a PR or posts a review autonomously (e.g. via `ship-stage`), tag the PR body and review comments `Generated with Antigravity`, with no `Co-Authored-By` trailer on the commit. This is separate from, and does not override, any attribution Claude Code itself is instructed to add to commits or PRs it creates directly.
