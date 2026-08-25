#!/usr/bin/env python3
"""
Unattended training campaign.

Each generation: self-play with the current best network, train on a replay buffer of
recent generations, export, verify against PyTorch, then gate the candidate against the
incumbent and promote only if it actually wins.

Designed to be left alone. State lives in campaign/state.json, so stopping with Ctrl-C
and re-running picks up at the next generation. Nothing is overwritten in place; every
generation's data and network are kept.

    ./campaign.py                          # run until stopped
    ./campaign.py --generations 20
    GAMES=500 ./campaign.py                # env vars also work, for parity with run_cycle.sh

Watch it with:  tail -f run/campaign/campaign.log
"""

import argparse
import json
import os
import shutil
import signal
import subprocess
import sys
import time
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
CHOSIM = os.path.join(HERE, "..", "ChoSim", "bin", "Release", "net10.0", "ChoSim")
PY = os.path.join(HERE, ".venv", "bin", "python")

_stop = False


def _on_signal(signum, frame):
    global _stop
    if _stop:
        print("\nsecond interrupt - exiting now")
        sys.exit(1)
    _stop = True
    print("\ninterrupt received; finishing this generation then stopping "
          "(press again to stop immediately)")


class Campaign:
    def __init__(self, args):
        self.a = args
        self.root = os.path.join(HERE, "run", "campaign")
        self.data_dir = os.path.join(self.root, "data")
        self.net_dir = os.path.join(self.root, "nets")
        for d in (self.root, self.data_dir, self.net_dir):
            os.makedirs(d, exist_ok=True)

        self.log_path = os.path.join(self.root, "campaign.log")
        self.state_path = os.path.join(self.root, "state.json")
        self.state = self._load_state()

    # ------------------------------------------------------------- plumbing

    def log(self, msg):
        line = f"[{datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S')}] {msg}"
        print(line, flush=True)
        with open(self.log_path, "a") as f:
            f.write(line + "\n")

    def _load_state(self):
        if os.path.exists(self.state_path):
            with open(self.state_path) as f:
                return json.load(f)
        return {"generation": 0, "best_onnx": None, "best_pt": None, "history": []}

    def _save_state(self):
        tmp = self.state_path + ".tmp"
        with open(tmp, "w") as f:
            json.dump(self.state, f, indent=2)
        os.replace(tmp, self.state_path)   # atomic, so an interrupt cannot corrupt it

    def run(self, cmd, capture=True):
        r = subprocess.run(cmd, capture_output=capture, text=True)
        if r.returncode != 0:
            self.log(f"FAILED: {' '.join(str(c) for c in cmd)}")
            if capture and r.stdout:
                self.log(r.stdout.strip()[-2000:])
            if capture and r.stderr:
                self.log(r.stderr.strip()[-2000:])
            raise RuntimeError("step failed")
        return r.stdout if capture else ""

    # -------------------------------------------------------------- pieces

    def replay_files(self):
        """Recent generations only. Training on just the newest makes the network chase
        its own latest quirks; keeping everything drags it toward long-obsolete play."""
        files = sorted(
            (os.path.join(self.data_dir, f) for f in os.listdir(self.data_dir)
             if f.endswith(".bin")),
            key=os.path.getmtime)
        return files[-self.a.buffer:]

    def selfplay(self, gen):
        out = os.path.join(self.data_dir, f"gen{gen:04d}.bin")
        cmd = [CHOSIM, "selfplay", "--variant", self.a.variant,
               "--games", str(self.a.games), "--sims", str(self.a.sims),
               "--out", out, "--seed", str(1000 + gen), "--quiet"]
        if self.state["best_onnx"]:
            cmd += ["--model", self.state["best_onnx"]]

        text = self.run(cmd)
        tail = [l for l in text.strip().splitlines() if "samples from" in l]
        self.log(f"  self-play: {tail[-1].strip() if tail else 'done'}")
        return out

    def train(self, gen):
        pt = os.path.join(self.net_dir, f"gen{gen:04d}.pt")
        cmd = [PY, os.path.join(HERE, "train.py"),
               "--data", *self.replay_files(),
               "--epochs", str(self.a.epochs), "--lr", str(self.a.lr),
               "--out", pt]
        if self.state["best_pt"]:
            cmd += ["--init", self.state["best_pt"]]

        text = self.run(cmd)
        last = [l for l in text.strip().splitlines() if l.startswith("epoch")]
        if last:
            self.log(f"  train: {last[-1].strip()}")
        return pt

    def export(self, gen, pt):
        onnx = os.path.join(self.net_dir, f"gen{gen:04d}.onnx")
        goldens = os.path.join(self.root, "goldens.bin")
        parity = os.path.join(self.root, "parity.bin")

        self.run([CHOSIM, "goldens", "--variant", self.a.variant,
                  "--count", "150", "--out", goldens])
        self.run([PY, os.path.join(HERE, "export.py"), "--model", pt, "--out", onnx,
                  "--positions", goldens, "--parity", parity, "--parity-count", "150"])

        # A broken encoder or export must never reach a gate result and be read as strength.
        text = self.run([CHOSIM, "parity", "--variant", self.a.variant,
                         "--in", parity, "--model", onnx])
        if "PASS" not in text:
            self.log("  parity FAILED - stopping rather than training on a broken chain")
            self.log(text.strip()[-1000:])
            raise RuntimeError("parity failed")
        self.log(f"  parity: {text.strip().splitlines()[0]}")
        return onnx

    def gate(self, candidate):
        """Promote only on a real win. The incumbent for generation 0 is uniform-prior MCTS."""
        incumbent = self.state["best_onnx"]
        opponent = f"nn:{self.a.sims}" if incumbent else f"mcts:{self.a.sims}"

        cmd = [CHOSIM, "match", "--variant", self.a.variant,
               "--a", f"nn:{self.a.sims}", "--b", opponent,
               "--games", str(self.a.gate_games), "--turns", "200",
               "--amodel", candidate, "--bmodel", incumbent or candidate,
               "--seed", str(90000 + self.state["generation"])]

        text = self.run(cmd)

        score = None
        for line in text.splitlines():
            if "score" in line and "%" in line:
                try:
                    score = float(line.split("score:")[1].split("%")[0].strip()) / 100.0
                except (IndexError, ValueError):
                    pass
        return score, opponent

    # ---------------------------------------------------------------- loop

    def generation(self):
        gen = self.state["generation"]
        t0 = time.time()
        self.log(f"generation {gen} starting "
                 f"(best: {os.path.basename(self.state['best_onnx']) if self.state['best_onnx'] else 'none yet'})")

        self.selfplay(gen)
        pt = self.train(gen)
        onnx = self.export(gen, pt)
        score, opponent = self.gate(onnx)

        promoted = score is not None and score >= self.a.promote
        if promoted:
            self.state["best_onnx"] = onnx
            self.state["best_pt"] = pt

        self.state["history"].append({
            "generation": gen,
            "score_vs_" + opponent: score,
            "promoted": promoted,
            "seconds": round(time.time() - t0, 1),
        })
        self.state["generation"] = gen + 1
        self._save_state()

        self.log(f"  gate vs {opponent}: "
                 f"{'n/a' if score is None else f'{score:.1%}'} -> "
                 f"{'PROMOTED' if promoted else 'kept incumbent'} "
                 f"({time.time() - t0:.0f}s)")

    def main(self):
        signal.signal(signal.SIGINT, _on_signal)
        signal.signal(signal.SIGTERM, _on_signal)

        self.log(f"campaign starting at generation {self.state['generation']} | "
                 f"{self.a.games} games x {self.a.sims} sims, buffer {self.a.buffer}, "
                 f"promote at {self.a.promote:.0%}")

        done = 0
        while not _stop and (self.a.generations == 0 or done < self.a.generations):
            try:
                self.generation()
            except RuntimeError:
                self.log("stopping: a step failed (state saved; re-run to retry)")
                return 1
            done += 1

        self.log("campaign stopped")
        return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--generations", type=int, default=0, help="0 runs until stopped")
    ap.add_argument("--games", type=int, default=int(os.environ.get("GAMES", 300)))
    ap.add_argument("--sims", type=int, default=int(os.environ.get("SIMS", 150)))
    ap.add_argument("--epochs", type=int, default=int(os.environ.get("EPOCHS", 25)))
    ap.add_argument("--lr", type=float, default=1e-3)
    ap.add_argument("--buffer", type=int, default=6, help="generations of data to train on")
    ap.add_argument("--gate-games", type=int, default=100)
    ap.add_argument("--promote", type=float, default=0.55)
    ap.add_argument("--variant", default="small")
    sys.exit(Campaign(ap.parse_args()).main())


if __name__ == "__main__":
    main()
