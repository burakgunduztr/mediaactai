"""TMT-3/TMT-9 automated verification: starts the real server (bound to
127.0.0.1, not an in-process test client) against the real MusicGenEngine
(TMT-9 — no more stub), posts /health and /generate, checks the returned
WAV, and confirms the request-validation gates added after the MED-6
security review actually reject what they're supposed to. Exits 0 on
success, non-zero with a clear message otherwise.

Timeouts are generous (model load + real CPU inference, not instant stub
generation) — don't tighten these back down just because the stub was fast.
"""

import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

import numpy as np
import soundfile as sf

HOST = "127.0.0.1"
PORT = 8765
BASE = f"http://{HOST}:{PORT}"
FADE = 1.0


def wait_for_health(timeout: float = 180.0) -> dict:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"{BASE}/health", timeout=1) as resp:
                return json.loads(resp.read())
        except (urllib.error.URLError, ConnectionRefusedError):
            time.sleep(0.2)
    raise TimeoutError("server never answered /health within timeout")


def post_generate(segments: list[dict], out_path: str, content_type: str = "application/json") -> tuple[int, dict]:
    body = json.dumps({"segments": segments, "out_path": out_path}).encode()
    req = urllib.request.Request(
        f"{BASE}/generate", data=body, headers={"Content-Type": content_type}, method="POST"
    )
    try:
        with urllib.request.urlopen(req, timeout=180) as resp:
            return resp.status, json.loads(resp.read())
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read())


def expect_rejected(label: str, status: int, body: dict, acceptable_statuses: set[int]) -> bool:
    if status not in acceptable_statuses:
        print(f"FAIL ({label}): expected one of HTTP {acceptable_statuses}, got {status}: {body}")
        return False
    print(f"PASS ({label}): correctly rejected with HTTP {status} — {body.get('detail')}")
    return True


def main() -> int:
    server_dir = os.path.dirname(os.path.abspath(__file__))
    with tempfile.TemporaryDirectory() as runs_dir, tempfile.TemporaryDirectory() as outside_dir:
        env = {**os.environ, "TEMPSCORE_RUNS_DIR": runs_dir}
        proc = subprocess.Popen(
            [sys.executable, "server.py"],
            cwd=server_dir,
            env=env,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
        )
        try:
            if proc.poll() is not None:
                print(f"FAIL: server process exited immediately (code {proc.returncode})")
                return 1

            health = wait_for_health()
            print(f"/health -> {health}")
            if proc.poll() is not None:
                print(
                    f"FAIL: our server process already exited (code {proc.returncode}) by the time /health "
                    "answered — that response came from a stale process already holding the port, not ours"
                )
                return 1
            if not (health.get("ok") is True and health.get("engine") == "MusicGenEngine"):
                print(f"FAIL: /health did not report the real engine correctly: {health}")
                return 1

            all_ok = True
            segments = [
                {"start": 0, "end": 5, "prompt": "soft piano"},
                {"start": 5, "end": 10, "prompt": "strings"},
            ]
            expected_seconds = sum(s["end"] - s["start"] for s in segments) + FADE

            out_path = os.path.join(runs_dir, "test_generate.wav")
            status, result = post_generate(segments, out_path)
            print(f"/generate -> {status} {result}")
            if status != 200:
                print(f"FAIL: happy path returned {status}, expected 200")
                return 1

            if abs(result["seconds"] - expected_seconds) > 0.01:
                print(f"FAIL: reported seconds {result['seconds']} != expected {expected_seconds}")
                return 1

            audio, sr = sf.read(out_path)
            actual_seconds = len(audio) / sr
            if abs(actual_seconds - expected_seconds) > 0.01:
                print(f"FAIL: WAV duration {actual_seconds}s != expected {expected_seconds}s")
                return 1

            # Crossfade-without-clicks sanity check: no abrupt sample-to-sample
            # jump anywhere, including at the crossfade boundary.
            diffs = np.abs(np.diff(audio))
            if diffs.max() > 0.2:
                print(f"FAIL: discontinuity detected (max sample delta {diffs.max()}) — crossfade likely broken")
                return 1
            if not np.all(np.isfinite(audio)):
                print("FAIL: WAV contains NaN/Inf samples")
                return 1
            print(f"PASS (happy path): WAV duration {actual_seconds:.3f}s matches expected, no discontinuities")

            # --- Negative cases added after the MED-6 security review ---

            status, body = post_generate(segments, os.path.join(runs_dir, "..", "escaped.wav"))
            all_ok &= expect_rejected("path traversal (../)", status, body, {400})

            status, body = post_generate(segments, os.path.join(outside_dir, "outside.wav"))
            all_ok &= expect_rejected("absolute path outside runs dir", status, body, {400})

            status, body = post_generate(segments, os.path.join(runs_dir, "not_a_wav.txt"))
            all_ok &= expect_rejected("non-.wav target", status, body, {400})

            status, body = post_generate(
                [{"start": 0, "end": 25, "prompt": "too long"}], os.path.join(runs_dir, "toolong.wav")
            )
            all_ok &= expect_rejected("segment over 20s", status, body, {400})

            # Our own explicit check returns 415; FastAPI's own body-parsing can
            # reject a non-JSON content-type earlier (422) before our check ever
            # runs — both outcomes are security-equivalent: rejected, nothing
            # written. See the comment above generate() in server.py.
            status, body = post_generate(segments, os.path.join(runs_dir, "wrongtype.wav"), content_type="text/plain")
            all_ok &= expect_rejected("wrong Content-Type", status, body, {415, 422})
            if os.path.exists(os.path.join(runs_dir, "wrongtype.wav")):
                print("FAIL (wrong Content-Type): rejected request still wrote a file")
                all_ok = False

            if not all_ok:
                return 1

            print("PASS: all negative-case validation gates reject correctly")
            return 0
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()


if __name__ == "__main__":
    sys.exit(main())
