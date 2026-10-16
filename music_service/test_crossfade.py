"""Direct unit test for crossfade(), decoupled from the stub engine's sine
wave — code-reviewer found that test_server.py's end-to-end click check gives
false confidence, because the stub's 220Hz tone happens to be phase-continuous
at these exact segment durations (6s = 1320 whole cycles) regardless of
whether blending actually runs. Verified empirically: a crossfade() with the
ramp blend ripped out still passed that test.

This test uses a hard +1.0/-1.0 step instead, which only a real linear ramp
can smooth — a naive concatenation produces a ~2.0 jump at the splice no
sine-phase coincidence can hide.
"""

import sys

import numpy as np

from server import FADE, crossfade


def main() -> int:
    sr = 1000
    fade_samples = int(sr * FADE)
    piece_len = fade_samples * 3

    a = np.ones(piece_len, dtype=np.float32)
    b = -np.ones(piece_len, dtype=np.float32)

    mixed = crossfade([a, b], sr)
    max_delta = float(np.abs(np.diff(mixed)).max())

    # A correct linear ramp spreads the 2.0 step across fade_samples, so each
    # individual sample delta is ~2.0/fade_samples. A naive hard concat (no
    # blend) produces one ~2.0 jump at the splice. Generous margin either way.
    bound_if_correct = (2.0 / fade_samples) * 2
    if max_delta > bound_if_correct:
        print(
            f"FAIL: crossfade() of a hard +1/-1 step produced a {max_delta:.4f} discontinuity "
            f"— expected <= {bound_if_correct:.4f} for a real ramp blend (a naive concat gives ~2.0)"
        )
        return 1

    print(
        f"PASS: crossfade() bounds the +1/-1 step discontinuity to {max_delta:.4f} "
        f"(naive concat would be ~2.0) — ramp blend confirmed, not just phase-coincidence"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
