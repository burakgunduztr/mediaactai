"""TMT-9: real MusicGen engine, replacing TMT-3's StubEngine.

Same generate(prompt, seconds) -> (audio: np.ndarray, sample_rate: int)
signature as engines/stub.py, so server.py's crossfade/duration contract
(frozen since TMT-3) needs no changes beyond swapping which engine class
gets instantiated.

MusicGen's max_new_tokens only approximately controls output duration —
the guide's own "~50 tokens/sec" is an estimate, not exact — but every
caller downstream of /generate (server.py's reported `seconds`, the C#
Muxer's segment alignment against video timestamps) depends on getting
back exactly `seconds` worth of audio. So this generates a little extra
and trims/zero-pads to the exact sample count rather than trusting the
token budget precisely. See notes/switchingToFoundryLocal.md and MED-13
for the engine decision (MusicGen locked in, ACE-Step out of scope).

Device picked automatically: CUDA if available (the ThinkPad's RTX PRO
2000), else CPU (Mac dev/dry-run — same fallback TMT-4's smoke_test_c.py
and TMT-7's smoke_test_c_gpu.py already use).
"""

import math

import numpy as np
import torch
from transformers import AutoProcessor, MusicgenForConditionalGeneration

MODEL_NAME = "facebook/musicgen-small"
TOKENS_PER_SECOND = 50  # matches the guide's own "~50 tokens/sec" estimate
TOKEN_SAFETY_MARGIN = 10  # generate a little extra, then trim — never pad with silence unless truly short


class MusicGenEngine:
    def __init__(self, model_name: str = MODEL_NAME, device: str | None = None):
        self.device = device or ("cuda" if torch.cuda.is_available() else "cpu")
        # local_files_only=True, same offline reasoning as server.py's HF_HUB_OFFLINE/
        # TRANSFORMERS_OFFLINE env vars -- belt and suspenders, since this constructor
        # is also used directly by tests that don't go through server.py's env setup.
        self.processor = AutoProcessor.from_pretrained(model_name, local_files_only=True)
        self.model = MusicgenForConditionalGeneration.from_pretrained(
            model_name, local_files_only=True
        ).to(self.device)
        self.model.eval()  # inference only — no dropout/training-mode state to carry between calls
        self.sr = self.model.config.audio_encoder.sampling_rate

    def generate(self, prompt: str, seconds: float) -> tuple[np.ndarray, int]:
        max_new_tokens = math.ceil(seconds * TOKENS_PER_SECOND) + TOKEN_SAFETY_MARGIN
        inputs = self.processor(text=[prompt], return_tensors="pt").to(self.device)
        with torch.inference_mode():  # no autograd graph retained across repeated calls
            audio = self.model.generate(**inputs, max_new_tokens=max_new_tokens)
        # Off the GPU and detached immediately — nothing from this call is
        # kept referenced on self, so there's nothing for VRAM to accumulate
        # across repeated /generate calls beyond the one model loaded once above.
        raw = audio[0, 0].to("cpu", dtype=torch.float32).numpy()
        return _fit_to_duration(raw, seconds, self.sr), self.sr


def _fit_to_duration(audio: np.ndarray, seconds: float, sr: int) -> np.ndarray:
    target = round(seconds * sr)
    if len(audio) >= target:
        return audio[:target]
    return np.pad(audio, (0, target - len(audio)))
