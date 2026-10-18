"""TMT-9: music_service wired to the real MusicGen engine.

/health and /generate per the frozen contract in notes/TempScore-Implementation-Guide.md
(Step 7), unchanged since TMT-3 — only which engine class gets instantiated changed.
ACE-Step is out of scope for this build (engine decision locked on MED-11, see
notes/switchingToFoundryLocal.md); engines.stub.StubEngine remains for reference/tests
that don't want real inference, but is no longer what the server starts by default.

Security note (MED-6 review, architect-approved clarification of the frozen contract):
`out_path` is request-controlled and this endpoint writes to it, so every request is
validated against RUNS_BASE_DIR before anything is computed or written — a crafted
out_path (path traversal, an absolute path elsewhere, a non-.wav target) is rejected
with 400, and a request that isn't application/json is rejected with 415 before the
body is ever trusted. See the containment check and MAX_* constants below.
"""

import os

# MED-19/TMT-15 security finding: transformers/huggingface_hub silently phones
# home to huggingface.co on every from_pretrained() call (revision checks,
# telemetry) unless told not to -- model download is a one-off Step 0 setup
# concern, not something this "fully offline" service should ever do at
# startup. Must be set before engines.musicgen imports transformers.
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"

import numpy as np
import soundfile as sf
import uvicorn
from fastapi import FastAPI, HTTPException
from fastapi import Request as HttpRequest
from pydantic import BaseModel

from engines.musicgen import MusicGenEngine

app = FastAPI()
engine = MusicGenEngine()  # loaded once at startup, reused across every request
FADE = 1.0  # seconds of crossfade between segments

# Bind to the loopback interface only — this is the one line that keeps the
# "fully offline" promise. Never 0.0.0.0, never configurable from a request.
HOST = "127.0.0.1"
PORT = 8765

# Every out_path must resolve inside this directory and end in .wav. Precedence
# (architect-approved): explicit override, then the real ThinkPad convention
# (%LOCALAPPDATA%\TempScore\runs, notes/TempScore-Implementation-Guide.md's run-folder
# rule), then a Mac/dev fallback. Never defaults to the working directory, since that
# would depend on where uvicorn happened to be started from.
if os.environ.get("TEMPSCORE_RUNS_DIR"):
    RUNS_BASE_DIR = os.path.realpath(os.environ["TEMPSCORE_RUNS_DIR"])
elif os.environ.get("LOCALAPPDATA"):
    RUNS_BASE_DIR = os.path.realpath(os.path.join(os.environ["LOCALAPPDATA"], "TempScore", "runs"))
else:
    RUNS_BASE_DIR = os.path.realpath(os.path.expanduser("~/.tempscore/runs"))
os.makedirs(RUNS_BASE_DIR, exist_ok=True)

MAX_SEGMENT_SECONDS = 20.0  # matches the guide's own segment-length cap
MAX_TOTAL_SECONDS = 300.0
MAX_PROMPT_CHARS = 500


class Segment(BaseModel):
    start: float
    end: float
    prompt: str


class GenerateRequest(BaseModel):
    segments: list[Segment]
    out_path: str


def _safe_out_path(out_path: str) -> str:
    """Resolve out_path and require it inside RUNS_BASE_DIR with a .wav suffix.

    Uses realpath + commonpath, not startswith — a sibling directory like
    `runs_evil/` would pass a naive string-prefix check but must not pass this one.
    """
    if not out_path.lower().endswith(".wav"):
        raise HTTPException(status_code=400, detail="out_path must end in .wav")

    resolved = os.path.realpath(out_path)
    base = RUNS_BASE_DIR
    resolved_cmp, base_cmp = (
        (os.path.normcase(resolved), os.path.normcase(base)) if os.name == "nt" else (resolved, base)
    )

    if resolved_cmp == base_cmp:
        raise HTTPException(status_code=400, detail="out_path cannot be the runs directory itself")
    try:
        if os.path.commonpath([base_cmp, resolved_cmp]) != base_cmp:
            raise HTTPException(status_code=400, detail="out_path must resolve inside the runs directory")
    except ValueError:
        # e.g. different drives on Windows — definitely not contained.
        raise HTTPException(status_code=400, detail="out_path must resolve inside the runs directory")

    # The service never creates directories on a caller's behalf — the run
    # folder is the C# app's job. Only the fixed, config-resolved base above
    # is auto-created, at startup, never per-request.
    if not os.path.isdir(os.path.dirname(resolved)):
        raise HTTPException(status_code=400, detail="out_path's parent directory does not exist")

    return resolved


def _validate_segments(segments: list[Segment]) -> None:
    if not segments:
        raise HTTPException(status_code=400, detail="segments must not be empty")
    total = 0.0
    for s in segments:
        if not (0 <= s.start < s.end):
            raise HTTPException(status_code=400, detail="each segment needs 0 <= start < end")
        if (s.end - s.start) > MAX_SEGMENT_SECONDS:
            raise HTTPException(status_code=400, detail=f"segment length must be <= {MAX_SEGMENT_SECONDS}s")
        if not s.prompt or len(s.prompt) > MAX_PROMPT_CHARS:
            raise HTTPException(status_code=400, detail=f"prompt must be non-empty and <= {MAX_PROMPT_CHARS} chars")
        total += s.end - s.start
    if total > MAX_TOTAL_SECONDS:
        raise HTTPException(status_code=400, detail=f"total segment length must be <= {MAX_TOTAL_SECONDS}s")


def crossfade(pieces: list[np.ndarray], sr: int) -> np.ndarray:
    f = int(sr * FADE)
    ramp = np.linspace(0, 1, f)
    out = pieces[0]
    for p in pieces[1:]:
        out = np.concatenate([out[:-f], out[-f:] * (1 - ramp) + p[:f] * ramp, p[f:]])
    return out


@app.get("/health")
def health():
    return {"ok": True, "engine": type(engine).__name__}


@app.post("/generate")
def generate(req: GenerateRequest, http_request: HttpRequest):
    # Gate every side effect behind these checks first — nothing below this
    # point runs until the request is proven safe. Kept `def`, not `async def`:
    # once TMT-9 swaps in a real (slow) GPU engine, a sync endpoint runs in
    # FastAPI's threadpool instead of blocking the event loop — so this
    # Content-Type check can't `await request.json()` to pre-empt FastAPI's
    # own body parsing. In practice FastAPI/Pydantic already rejects most
    # non-JSON content types on its own (422) before this line is reached;
    # this check is the explicit, intentional backstop for whatever slips
    # through that happens to still parse as valid JSON.
    content_type = http_request.headers.get("content-type", "")
    if not content_type.startswith("application/json"):
        raise HTTPException(status_code=415, detail="Content-Type must be application/json")
    _validate_segments(req.segments)
    safe_out_path = _safe_out_path(req.out_path)

    pieces: list[np.ndarray] = []
    sr = engine.sr
    for s in req.segments:
        audio, sr = engine.generate(s.prompt, (s.end - s.start) + FADE)
        pieces.append(audio)
    mix = crossfade(pieces, sr)
    mix = mix / max(1e-6, np.abs(mix).max()) * 0.9  # normalise
    sf.write(safe_out_path, mix, sr)
    return {"path": safe_out_path, "seconds": len(mix) / sr}


if __name__ == "__main__":
    # Hardcoded host/port so `python server.py` can never accidentally bind
    # anywhere but localhost — matches `uvicorn server:app --host 127.0.0.1 --port 8765`.
    uvicorn.run(app, host=HOST, port=PORT)
