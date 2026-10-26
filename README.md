# Film Spring Open 2026 — MediaActAI

**MediaActAI** is a hackathon project, built as a helper tool for film editors, composers, and other creators: hand it a short clip and it analyzes the footage, writes a scene‑by‑scene summary with mood and energy, and hands back the same video scored with the given music model — style, BPM, key, and per‑segment prompts included — generated entirely offline, on one laptop.

![C#](https://shieldcn.dev/badge/C%23-.NET%208-512BD4.svg?logo=csharp) ![WinUI](https://shieldcn.dev/badge/WinUI-3-0078D4.svg?logo=windows) ![Python](https://shieldcn.dev/badge/Python-3.11-3776AB.svg?logo=python) ![FastAPI](https://shieldcn.dev/badge/FastAPI-009688.svg?logo=fastapi) ![PyTorch](https://shieldcn.dev/badge/PyTorch-EE4C2C.svg?logo=pytorch) ![Transformers](https://shieldcn.dev/badge/Hugging%20Face-Transformers-FFD21E.svg?logo=huggingface) ![MusicGen](https://shieldcn.dev/badge/MusicGen-Meta%20AI-0866FF.svg?logo=meta) ![Foundry Local](https://shieldcn.dev/badge/Foundry%20Local-Microsoft-5E5E5E.svg?logo=microsoft) ![FFmpeg](https://shieldcn.dev/badge/FFmpeg-007808.svg?logo=ffmpeg)

### The story

For this hackathon, I decided to step out of my comfort zone. Leaving my usual MacBook workflow behind, I challenged myself to build a full, native WinUI application in C# on a sponsored Lenovo device. The goal was a tool for film industry workers, composers, and creators that analyzes short films, generates meaningful scene summaries, and outputs the same film scored with a real, locally generated soundtrack — the finished video and music combined into one `output.mp4`.

The pipeline underneath — scene understanding, mood/energy reasoning, a structured music plan — is also the foundation for two natural extensions beyond this hackathon build: a standalone **harmony/mood suggestion tool** for composers (surfacing the scene plan — style, BPM, key, per‑segment mood — without necessarily rendering audio), and a **DAW plugin** that feeds the same scene-aware plan directly into a composer's existing session instead of a muxed video file.

The application is a pipeline of local Microsoft Foundry models:

1. It starts with **`qwen3.5-2b`**, the vision model. `FrameDescriber` calls Foundry Local's vision endpoint through `FoundryLocalImageDescriber` to generate a text description of each extracted video frame.
2. Those descriptions are passed to **`phi-4-mini`**, which handles text generation through `FoundryLocalTextGenerator`. First, `SceneSummarizer` groups the timestamped frames into scenes and assigns each one a mood and energy guess. Then `MusicPlanner` converts that summary into an actionable JSON music plan — style, BPM, key, and a specific prompt for every segment.
3. Finally, that JSON plan drives **MusicGen**. Running inside a dedicated music service, MusicGen is the actual audio‑generation engine — it takes the segment prompts and the plan's global constraints and renders real `.wav` audio, crossfaded and timed to the film's segments.

To build this efficiently on an environment that was new to me, I leaned on agentic software development: planning every piece of work in Linear from day one, then using the Linear MCP alongside Claude Code to accelerate both research and the C# development itself.

### Demo & links

- 🎬 **Watch the demo:** [YouTube](https://youtu.be/mrqjKA4Qp5I)
- 🔗 **Project page:** [FSO Hackathon 2026](https://fso-hackaithon-2026.netlify.app/p/11-film-music-composer.html)

### Architecture at a glance

Everything that "understands" the video runs locally (NPU/CPU), the music is generated locally (GPU/CPU), and the two halves only ever talk to each other over `127.0.0.1`. Nothing leaves the machine.

- **C# / WinUI 3 app** (`app/`, `TempScore.Core/`) — owns the pipeline: extracts frames, describes them, summarizes the scene, plans the music, calls the music service, and muxes the result.
- **Python music service** (`music_service/`) — a small FastAPI server that only does one thing: turn a list of `{start, end, prompt}` segments into a `.wav` file, using MusicGen.
- **Foundry Local** — a local, OpenAI‑compatible inference server (Microsoft) that serves the text model (scene summary + music plan) and the vision model (frame description) that the C# app calls over HTTP.

This document gets the whole thing running first, then explains the pipeline logic end to end, exactly which local servers/ports/models are involved, and how to configure each one.

---

## How to run

### Prerequisites

- Windows 11, with Developer Mode on (Settings → System → For developers).
- `ffmpeg` and `ffprobe` on `PATH`: `winget install Gyan.FFmpeg`, then confirm with `ffmpeg -version`.
- Python 3.11 (64‑bit).
- .NET 8 SDK and Visual Studio 2022+ with the "WinUI application development" and ".NET desktop development" workloads. The WinUI app targets `net8.0-windows10.0.19041.0`; the `Services/*.cs` classes themselves are plain .NET with no WinUI dependency.
- [Foundry Local](https://github.com/microsoft/Foundry-Local) installed:
  ```powershell
  winget install Microsoft.FoundryLocal
  ```
- An NVIDIA GPU with CUDA 12.8+ is strongly recommended for `music_service` (MusicGen runs on CPU otherwise, much slower); the pipeline still works end to end on CPU for development.

### 1. Start Foundry Local and load the models

The CPU build of `phi-4-mini` is what the app expects by default. When this machine's NPU and its drivers support it, the OpenVINO NPU build can be faster — load that instead of the CPU commands, then point the app's text-model alias at the same id (§3.2). Confirm the exact id with `foundry model list`.

```powershell
# Text model (scene summary + music plan) — CPU, the default:
foundry model download phi-4-mini-instruct-generic-cpu   # or whatever your catalog shows — see §3.2 Models
foundry model load phi-4-mini-instruct-generic-cpu

# Same model, NPU — use these instead of the two commands above when the NPU and drivers support it:
foundry model download phi-4-mini-instruct-openvino-npu
foundry model load phi-4-mini-instruct-openvino-npu

foundry model download qwen3.5-2b-generic-cpu   # or whatever your catalog shows — see §3.1 Models
foundry model load qwen3.5-2b-generic-cpu

foundry server status   # confirm the daemon is running and copy the local service URL/port
```

Set the port for the app to use (current PowerShell session):

```powershell
$env:FOUNDRY_LOCAL_BASE_URL = "http://127.0.0.1:<port-from-above>"
```

Or persist it across sessions with `setx FOUNDRY_LOCAL_BASE_URL "http://127.0.0.1:<port-from-above>"` (requires a new terminal to take effect).

Sanity‑check the endpoint before touching the app:

```powershell
cd foundry_local
python smoke_test.py --base $env:FOUNDRY_LOCAL_BASE_URL
```

### 2. Start the music service

```powershell
cd music_service
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt

# GPU machines: reinstall torch/torchaudio against your CUDA index BEFORE trusting any
# GPU timing — the requirements.txt pins resolve to CPU wheels by default:
# pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu128

python server.py   # binds http://127.0.0.1:8765, loads MusicGen once at startup
```

Confirm it's up:

```powershell
curl http://127.0.0.1:8765/health
# {"ok": true, "engine": "MusicGenEngine"}
```

The first request to `/generate` (or server startup, depending on `transformers` caching) downloads `facebook/musicgen-small` from Hugging Face once; `server.py` sets `HF_HUB_OFFLINE=1`/`TRANSFORMERS_OFFLINE=1` so it **never** phones home afterward — pre‑download the model ahead of a demo/offline run:

```powershell
python -c "from transformers import AutoProcessor, MusicgenForConditionalGeneration; AutoProcessor.from_pretrained('facebook/musicgen-small'); MusicgenForConditionalGeneration.from_pretrained('facebook/musicgen-small')"
```

### 3. Run the WinUI app

1. Open `TempScore.App.slnx` in Visual Studio 2022+ with the "WinUI application development" and ".NET desktop development" workloads installed.
2. Make sure `FOUNDRY_LOCAL_BASE_URL` is set in the environment the app launches from (set it in your terminal before `devenv`, or in Visual Studio's project launch settings) — otherwise it silently falls back to the unreliable default port.
3. Build and run (`F5`). On launch the app calls `music_service`'s `/health` and shows "Engine: ready" / "Engine: unreachable" at the top of the window.
4. **Open video** → pick an `.mp4` (see `samples/` for three synthetic 60s test clips: `dialogue.mp4`, `action.mp4`, `calm.mp4`).
5. **Score it** → runs all six stages, shows live step progress, then plays the result.
6. Edit any segment's prompt/intensity in the plan panel and click **Regenerate**, or move the **mood slider** and click **Apply**, to re‑run only stages 5–6 against the edited plan.

---

## 1. How it works — the six pipeline stages

```
                     ┌──────────────── understands the video ───────────────┐
[1 Extract frames] ──► │ [2 Describe frames] ──► [3 Summarize] ──► [4 Plan music] │
 ffmpeg · CPU          │  Foundry Local vision     Foundry Local text·JSON       │
                     └──────────────────────────────────────────┬─────────────┘
                                                                  ▼
                                   [6 Mux with video] ◄── ┌─ [5 Generate music] ─┐
                                   ffmpeg · output.mp4    │  music_service·MusicGen│
                                                           └──────────────────────┘
```

Every run writes its intermediate artifacts to a fresh timestamped folder (see [§4 Run folder](#4-run-folder-contract)), so any stage can be inspected or replayed independently.

### Stage 1 — Extract frames (`app/Services/FrameExtractor.cs`)
Runs `ffmpeg`/`ffprobe` as subprocesses (argument arrays, never shell strings):
- One JPEG every 3 seconds, downscaled: `ffmpeg -vf "fps=1/3,scale=768:-2"`.
- Scene‑cut timestamps: `ffmpeg -vf "select='gt(scene,0.3)',showinfo"` (parsed from stderr).
- Clip duration via `ffprobe -show_entries format=duration`.
- Whether the clip has an audio track via `ffprobe -select_streams a`.

Output: a list of `Frame(Path, Timestamp, Description="")`, the cut times, the duration, and the audio flag.

### Stage 2 — Describe frames (`app/Services/FrameDescriber.cs`)
Sends each frame (base64 JPEG/PNG) to **Foundry Local's vision model** over its OpenAI‑compatible `/v1/chat/completions` endpoint, asking for a one‑sentence description. Each description is truncated to ~40 words. A transport failure on a single frame degrades that frame's description to `""` rather than aborting the run.

### Stage 3 — Summarize the scene (`app/Services/SceneSummarizer.cs`)
Sends all frame descriptions + cut times to **Foundry Local's text model**, asking for strict JSON:
```json
{"scenes":[{"start":0,"end":12,"summary":"...","mood":"calm","energy":2}]}
```
`mood` is one word; `energy` is 1 (still) – 5 (intense). If the response isn't valid JSON, it retries once with "Your last answer was not valid JSON. Return only the JSON." If it still fails (or the model is unreachable), it falls back to one scene per cut segment with the raw frame descriptions joined — **the pipeline never hard‑stops here.**

### Stage 4 — Plan the music (`app/Services/MusicPlanner.cs`)
Sends the scene summary to the **same Foundry Local text model**, asking for a music plan:
```json
{"style":"cinematic orchestral, piano lead","bpm":72,"key":"A minor",
 "segments":[{"start":0,"end":12,"intensity":2,"prompt":"soft solo piano, rain ambience, melancholic, 72 bpm, A minor"}]}
```
Same retry‑once‑then‑fallback behavior. Every plan — model‑produced, fallback, or user‑edited — passes through `MusicPlanValidation.Validate` (`TempScore.Core/Services/MusicPlanValidation.cs`) before use:
- BPM clamped to `[50, 160]`, default `90` if out of range or missing.
- Segments sorted, gaps/overlaps closed, last segment stretched/clamped to the clip's real duration.
- Any segment longer than 20s is split into equal pieces.
- Every prompt is rebuilt to start with `"<style>, <bpm> bpm, <key>"` so the whole piece stays sonically consistent (idempotent — re‑validating an already‑prefixed plan won't double the prefix).
- `Intensity` clamped to `[1, 5]`; final prompt truncated to 500 characters (matches the music service's own limit).

If both the real call and its retry fail, `FallbackTable.BuildFallbackPlan` (`TempScore.Core/Services/FallbackTable.cs`) builds a plan directly from the scene summary using a fixed mood → prompt table (`calm` → "soft piano and pads", `tense` → "low strings ostinato, pulsing synth", `happy` → "bright acoustic guitar and light percussion", `action` → "driving drums, brass hits", anything else → "soft piano and pads"), then runs it through the same validator.

### Stage 5 — Generate music (`music_service/`)
The C# app POSTs the validated plan's segments to the Python **music service**'s `/generate` endpoint. The service renders each segment with **MusicGen** (`facebook/musicgen-small`), crossfades adjacent segments (1s ramp), normalizes the mix, and writes a single `.wav` file at the `out_path` the caller supplied — which must resolve inside the server's configured runs directory and end in `.wav` (path‑traversal/absolute‑elsewhere/wrong‑extension requests are rejected with `400`).

### Stage 6 — Mux (`app/Services/Muxer.cs`)
`ffmpeg -c:v copy` always (video is never re‑encoded, so this step is ~1 second):
- **No audio track on the input clip:** replace the audio entirely with the generated score.
- **Has an audio track:** sidechain‑duck the score under the existing audio (`sidechaincompress` + `amix`) so dialogue/ambient sound is preserved and the score ducks under it automatically.

### Extra: regenerate & mood slider (`app/Orchestrator.cs`, `app/MainPage.xaml.cs`)
- **Regenerate from plan:** re‑runs only stages 5–6 against a hand‑edited plan (edited prompts/intensity from the UI's plan panel), skipping extraction/description/summarization/planning.
- **Mood slider:** appends one fixed mood word (`somber`, `calm`, `warm`, `energetic`, `triumphant`) to every segment's prompt in the *original* (un‑mutated) plan, then regenerates — so repeated slider moves never compound words onto each other.

Both paths re‑validate the plan before it reaches `/generate`, since a hand‑edited prompt/intensity must never reach the music service unchecked.

---

## 2. Local servers — addresses, ports, and how to point the app at them

Everything below binds to `127.0.0.1` only. This is intentional and is what keeps the "fully offline" guarantee — never change either server to bind `0.0.0.0`.

| Server | Default address | Fixed or variable? | Who starts it | Set via |
| --- | --- | --- | --- | --- |
| **music_service** (MusicGen) | `http://127.0.0.1:8765` | **Fixed** — hardcoded in `music_service/server.py` | `python server.py` or `uvicorn` | `MusicClient(baseAddress: ...)` constructor arg in `app/MainPage.xaml.cs` |
| **Foundry Local** (text + vision) | `http://127.0.0.1:<port>` | **Variable** — assigned fresh per machine/daemon start | `foundry model load <alias>` (starts the daemon on first load) | `FOUNDRY_LOCAL_BASE_URL` environment variable |

### 2.1 music_service — `http://127.0.0.1:8765`

This port **is** hardcoded (`music_service/server.py`, `HOST = "127.0.0.1"`, `PORT = 8765`) and is not meant to be reconfigured — the C# `MusicClient` defaults to the same value:

```csharp
// app/Services/MusicClient.cs
public MusicClient(string baseAddress = "http://127.0.0.1:8765", TimeSpan? timeout = null)
```

If you ever do need a different port, change `PORT` in `server.py` **and** pass the matching `baseAddress` into the `MusicClient` constructed in `app/MainPage.xaml.cs`.

The C# app calls:
- `GET /health` → `{"ok": true, "engine": "MusicGenEngine"}` — shown in the UI as "Engine: ready" / "Engine: unreachable".
- `POST /generate` → `{"segments": [...], "out_path": "..."}` → `{"path": "...", "seconds": ...}`.

### 2.2 Foundry Local — `http://127.0.0.1:<port>` (port is NOT fixed)

Unlike `music_service`, Foundry Local's local server port is **assigned at daemon startup and is not stable across machines or even across restarts**. Find it with:

```powershell
foundry server status   # or: foundry status
```

Look for the local service URL, e.g. `http://127.0.0.1:53430`. Then set it once per machine/session:

```powershell
$env:FOUNDRY_LOCAL_BASE_URL = "http://127.0.0.1:53430"   # current session
setx FOUNDRY_LOCAL_BASE_URL "http://127.0.0.1:53430"      # persist across sessions
```

`app/MainPage.xaml.cs` reads this at startup:

```csharp
private static readonly string FoundryBaseAddress =
    Environment.GetEnvironmentVariable("FOUNDRY_LOCAL_BASE_URL") ?? "http://127.0.0.1:53430";
```

If the env var isn't set, it falls back to `http://127.0.0.1:53430` — a value confirmed to work on one dev machine only. **Always set `FOUNDRY_LOCAL_BASE_URL` explicitly rather than relying on that fallback.**

The same base address is passed to all three Foundry Local clients wired up in `MainPage()`:

```csharp
_orchestrator = new Orchestrator(
    new FrameExtractor(),
    new FrameDescriber(new FoundryLocalImageDescriber(baseAddress: FoundryBaseAddress, aliasPrefix: VisionModelAlias)),
    new SceneSummarizer(new FoundryLocalTextGenerator(baseAddress: FoundryBaseAddress)),
    new MusicPlanner(new FoundryLocalTextGenerator(baseAddress: FoundryBaseAddress)),
    _engineHealthClient,
    new Muxer());
```

The `foundry_local/smoke_test.py` script follows the identical pattern on the Python side (`--base` flag or `FOUNDRY_LOCAL_BASE_URL` env var — never hardcode the port for a machine that isn't yours).

---

## 3. Models — which ones, and where to set the name

Foundry Local's chat endpoint needs the **exact catalog model id** (e.g. `Phi-4-mini-instruct-generic-cpu:4`), not the short CLI alias used by `foundry model download`/`load` (e.g. `phi-4-mini`) — passing the alias to `/v1/chat/completions` returns `404 Model not found`. Every client in this repo therefore takes an **alias prefix** and resolves the real id itself at first call via `GET /v1/models` (case‑insensitive prefix match) — you only ever need to set the alias, never the full versioned id.

| Role | Engine | Model (alias you set) | Where it's configured | Runs on |
| --- | --- | --- | --- | --- |
| Frame description (vision) | Foundry Local | `qwen3.5-2b-generic-cpu` (CPU‑targeted Qwen3.5‑VL; GPU variant `qwen3.5-2b` noted as an alternative) | `app/MainPage.xaml.cs` → `VisionModelAlias` const, passed into `FoundryLocalImageDescriber(aliasPrefix: ...)` | NPU/CPU/GPU, whatever the catalog loads it on |
| Scene summary + music plan (text) | Foundry Local | `phi-4-mini-instruct-generic-cpu` (default inside `FoundryLocalTextGenerator`) | `app/Services/SceneSummarizer.cs` → `FoundryLocalTextGenerator(aliasPrefix: ...)` constructor default; override by passing a different `aliasPrefix` where it's constructed in `MainPage.xaml.cs` | CPU |
| Music generation | MusicGen (HuggingFace `transformers`) | `facebook/musicgen-small` | `music_service/engines/musicgen.py` → `MODEL_NAME` constant | CUDA GPU if available, else CPU |

### 3.1 Setting the vision model alias

```csharp
// app/MainPage.xaml.cs
private const string VisionModelAlias = "qwen3.5-2b-generic-cpu";
```

Change this to whatever multimodal model your machine's `foundry model list` actually offers and you've loaded, e.g. `qwen3-vl-2b-instruct`, `qwen3-vl-4b-instruct`, `qwen3.5-4b`, `gemma-4-e2b-it`. Foundry Local's multimodal catalog is **region‑dependent** — always run `foundry model list` on the target machine rather than assuming a fixed list.

```bash
foundry model download qwen3.5-2b-generic-cpu
foundry model load qwen3.5-2b-generic-cpu
```

### 3.2 Setting the text model alias

```csharp
// app/Services/SceneSummarizer.cs
public FoundryLocalTextGenerator(
    string baseAddress = "http://127.0.0.1:53430",
    string aliasPrefix = "phi-4-mini-instruct-generic-cpu")
```

Download/load the matching model before first run — the explicit `-generic-cpu` variant, not the bare `phi-4-mini` alias (which lets Foundry Local auto-pick a GPU/NPU variant instead):

```powershell
foundry model download phi-4-mini-instruct-generic-cpu
foundry model load phi-4-mini-instruct-generic-cpu
```

If your machine's catalog resolves this to a different variant suffix (e.g. `Phi-4-mini-instruct-generic-cpu:4`, version number aside), that's fine — `ResolveModelIdAsync` only prefix-matches against `/v1/models`, version suffix included or not. If you'd rather target GPU or NPU instead, pass the matching `aliasPrefix` where `FoundryLocalTextGenerator` is constructed in `app/MainPage.xaml.cs` (two call sites — `SceneSummarizer` and `MusicPlanner` — pass the same value).

### 3.3 Setting the MusicGen model

```python
# music_service/engines/musicgen.py
MODEL_NAME = "facebook/musicgen-small"
```

`facebook/musicgen-medium` is a drop‑in alternative if you have more VRAM — just change this constant (or pass `model_name=...` to `MusicGenEngine(...)` in `music_service/server.py`) and re-download it ahead of time (see the music_service setup step in **How to run** above).

---

## 4. Run folder contract

Every `Orchestrator.RunAsync`/`RegenerateAsync` call writes to a fresh folder:

```
<runs-base>/<yyyyMMdd_HHmmss_fff>/
├── frames/            # extracted JPEGs
├── descriptions.json  # debug dump after stage 2
├── summary.json       # debug dump after stage 3
├── plan.json          # debug dump after stage 4 (also written by RegenerateAsync)
├── score.wav          # music_service's output (stage 5)
└── output.mp4          # final muxed video (stage 6)
```

Both the C# app and `music_service` resolve `<runs-base>` with the **same precedence**, so a path written by one side is always valid for the other:

1. `TEMPSCORE_RUNS_DIR` environment variable, if set.
2. Otherwise, `%LOCALAPPDATA%\TempScore\runs`.

`music_service`'s `/generate` independently re‑validates that `out_path` resolves inside this same directory and ends in `.wav`, rejecting anything else with `400` — so if you override `TEMPSCORE_RUNS_DIR`, set it identically for both the app process and the `music_service` process (same shell/session, or both via your OS's persistent env‑var mechanism).

---

## 5. Project layout

```
MediaActAI/
├── app/                          # WinUI 3 app (Windows-only to build/run fully)
│   ├── Orchestrator.cs           # sequences the six stages
│   ├── MainPage.xaml(.cs)        # UI + wiring of all clients/aliases
│   └── Services/
│       ├── FrameExtractor.cs     # stage 1 — ffmpeg
│       ├── FrameDescriber.cs     # stage 2 — Foundry Local vision model
│       ├── SceneSummarizer.cs    # stage 3 — Foundry Local text model
│       ├── MusicPlanner.cs       # stage 4 — Foundry Local text model
│       ├── MusicClient.cs        # stage 5 — HTTP client for music_service
│       └── Muxer.cs              # stage 6 — ffmpeg
├── TempScore.Core/                # platform-agnostic models + validation/fallback
│   ├── Models/                   # Frame, Scene, SceneSummary, Segment, MusicPlan
│   └── Services/                 # JsonParsing, MusicPlanValidation, FallbackTable
├── TempScore.Core.Tests/          # xunit tests for the Core logic
├── music_service/                 # FastAPI server + MusicGen engine
│   ├── server.py                 # /health, /generate
│   └── engines/musicgen.py       # MusicGen wrapper
├── foundry_local/smoke_test.py    # standalone Foundry Local connectivity check
├── samples/                        # three synthetic 60s test clips
└── notes/                          # design notes, setup guides, decision logs
```
