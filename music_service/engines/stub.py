"""Stub engine for TMT-3 — returns a sine tone instead of real inference.

Same generate(prompt, seconds) -> (audio: np.ndarray, sample_rate: int) signature
the real MusicGen/ACE-Step engines will use, so server.py and the crossfade logic
never need to change when TMT-9 swaps this for the real engine.
"""

import numpy as np


class StubEngine:
    def __init__(self, sample_rate: int = 32000, frequency: float = 220.0):
        self.sr = sample_rate
        self.frequency = frequency

    def generate(self, prompt: str, seconds: float) -> tuple[np.ndarray, int]:
        n = int(seconds * self.sr)
        t = np.linspace(0, seconds, n, endpoint=False)
        audio = (0.3 * np.sin(2 * np.pi * self.frequency * t)).astype(np.float32)
        return audio, self.sr
