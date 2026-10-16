# TempScore — repository instructions for Copilot

TempScore turns a 60-second video into the same video with an original, offline-generated soundtrack. Everything runs on one laptop (Intel Core Ultra 5 336H NPU, NVIDIA RTX PRO 500 Blackwell 6 GB, 32 GB RAM). Nothing may ever leave the machine — no cloud calls, no telemetry, no network access outside `127.0.0.1`.

**The single source of truth is `TempScore-Implementation-Guide.md` at the repo root.** It has the full six-stage pipeline spec, exact code snippets, prompts, JSON schemas, and the risk/fallback table. Read it before writing or reviewing any code here. Do not invent an API shape, file path, or prompt that conflicts with it — if something is ambiguous, say so instead of guessing.

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
├── app/                 # WinUI 3, packaged, C#
│   ├── Services/        # FrameExtractor, FrameDescriber, SceneSummarizer, MusicPlanner, MusicClient, Muxer
│   ├── Models/           # Frame, Scene, MusicPlan, Segment records
│   └── MainPage.xaml(.cs)
├── music_service/       # FastAPI: /health, /generate; engines/musicgen.py, engines/acestep.py
├── samples/             # 3 test clips: dialogue, action, calm
└── README.md
```

The C# app and Python service talk only over HTTP to `127.0.0.1`. Every run writes to `%LOCALAPPDATA%\TempScore\runs\<timestamp>\` (frames, descriptions.json, summary.json, plan.json, score.wav, output.mp4) — this is the debugging and replay mechanism, keep it intact.

## Hard constraints

- The Windows AI APIs require **package identity** — the app must stay a packaged WinUI 3 app, never an unpackaged console app.
- Every stage must degrade gracefully. Phi Silica JSON parsing failures get one retry, then a rule-based fallback — **the pipeline must never hard-stop** before producing some output.
- `-c:v copy` on mux — never re-encode video.
- The music service binds to `127.0.0.1` only, never `0.0.0.0`.

## Agents available in this repo

Six custom agents live in `.github/agents/`: `pm`, `architect`, `senior-dev`, `qa`, `security`, `code-reviewer`. Each knows this project's specifics and the compressed 10-hour build schedule the `pm` agent owns. Prefer delegating to the matching agent over improvising a generic one.

## Model tier and blocking authority

| Agent | Model tier | Blocking gate? |
| --- | --- | --- |
| `pm` | mid | Yes, on scope — never overrides `security` |
| `architect` | mid-to-top | Yes — contracts change only through this agent |
| `senior-dev` | top | No — implements and iterates until others are clean |
| `qa` | cheap → mid for the H7.5–8.5 full pass | Yes, per stage |
| `security` | mid-to-top, never cheap | **Yes, always — mandatory, no schedule exception** |
| `code-reviewer` | mid | Yes, on correctness |

`security`'s sign-off on the offline boundary (network bind, ffmpeg/subprocess argument safety, content-safety filters) is the one gate `pm` can never schedule around — see `.github/CODEOWNERS` for which files require it.

## Repeatable workflows

Three prompt files in `.github/prompts/` turn the schedule above into invokable commands instead of prose:

- **`/review-stage`** — run the board (`security` mandatory, `architect`, `code-reviewer`, `qa`, `pm`) against a finished stage or the full pipeline. Never ships anything, only reports.
- **`/ship-stage`** — the full single-stage loop: `architect` confirms the contract, `senior-dev` implements, `/review-stage` runs, `pm` scope-adjusts.
- **`/ship-parallel-lanes`** — for H1.0–3.0 (or any other file-disjoint split): `architect` freezes the shared contract first, then two `senior-dev` lanes build concurrently (C# pipeline vs Python music service), each reviewed independently and reconciled by `pm`.

Adapted from a larger multi-agent CI/CD governance setup in `other-project/` — its ticket-queue workflow (`ship-queue`) and GitHub-PR-metadata-waiver workflow (`review-pr-disjoint`) were left out because there's no ticket backlog or GitHub PR to apply them to here, and its GitHub Actions CI was left out because this repo isn't pushed anywhere yet. The model-tiering, mandatory-security-gate, and named-workflow patterns were worth keeping; the Linear/npm/auto-merge machinery wasn't.

## Attribution (Copilot/Antigravity-authored work only)

When Copilot or Antigravity itself opens a PR or posts a review autonomously (e.g. via `/ship-stage`), tag the PR body and review comments `Generated with Copilot` / `Generated with Antigravity`, with no `Co-Authored-By` trailer on the commit. This is separate from, and does not override, any attribution Claude Code itself is instructed to add to commits or PRs it creates directly.
