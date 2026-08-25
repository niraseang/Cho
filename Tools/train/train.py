"""
Training loop.

Start with --overfit: it trains on a handful of positions until the model memorises them.
If policy loss does not converge onto the entropy floor and value loss onto ~0, the wiring
is wrong and no amount of self-play data will fix it.
"""

import argparse
import time

import numpy as np
import torch
import torch.nn.functional as F

import cho_data as cd
from model import ChoNet, masked_policy_loss


def build_dataset(paths, limit=None):
    """Reads one or more self-play files. Several files form the replay buffer: training on
    only the newest generation makes a network chase its own most recent quirks."""
    if isinstance(paths, str):
        paths = [paths]

    samples, no_progress_limit = [], 50
    for path in paths:
        data = cd.read_selfplay(path)
        no_progress_limit = data["no_progress_limit"]
        samples.extend(data["samples"])

    if limit:
        samples = samples[:limit]

    data = {"no_progress_limit": no_progress_limit}

    planes, is_chess, idxs, targets, values = [], [], [], [], []

    for s in samples:
        p = cd.decode_position(s["position"])
        planes.append(cd.encode(p, data["no_progress_limit"]))
        is_chess.append(p.phase_one)

        visits = s["policy_visits"]
        total = visits.sum()
        targets.append(visits / total if total > 0 else np.full_like(visits, 1.0 / len(visits)))
        idxs.append(s["policy_index"])
        values.append(s["value"])

    max_len = max(len(i) for i in idxs)
    n = len(planes)

    idx_pad = np.zeros((n, max_len), dtype=np.int64)
    tgt_pad = np.zeros((n, max_len), dtype=np.float32)
    mask = np.zeros((n, max_len), dtype=bool)

    for i, (ix, tg) in enumerate(zip(idxs, targets)):
        idx_pad[i, :len(ix)] = ix
        tgt_pad[i, :len(tg)] = tg
        mask[i, :len(ix)] = True

    sample0 = cd.decode_position(samples[0]["position"])

    return {
        "planes": torch.from_numpy(np.stack(planes)),
        "is_chess": torch.tensor(is_chess, dtype=torch.bool),
        "idx": torch.from_numpy(idx_pad),
        "target": torch.from_numpy(tgt_pad),
        "mask": torch.from_numpy(mask),
        "value": torch.tensor(values, dtype=torch.float32),
        "shape": (cd.PLANE_COUNT, sample0.ih, sample0.iw),
        "board": (sample0.bw, sample0.bh),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", nargs="+", default=["selfplay.bin"],
                    help="one or more self-play files; several act as a replay buffer")
    ap.add_argument("--epochs", type=int, default=200)
    ap.add_argument("--batch", type=int, default=64)
    ap.add_argument("--lr", type=float, default=2e-3)
    ap.add_argument("--blocks", type=int, default=4)
    ap.add_argument("--channels", type=int, default=64)
    ap.add_argument("--overfit", type=int, default=0,
                    help="train on this many samples only, to verify the wiring")
    ap.add_argument("--out", default="")
    ap.add_argument("--init", default="",
                    help="warm-start from an existing .pt instead of random weights")
    args = ap.parse_args()

    torch.manual_seed(0)

    ds = build_dataset(args.data, limit=args.overfit or None)
    planes, h, w = ds["shape"]
    bw, bh = ds["board"]
    n = ds["planes"].shape[0]

    net = ChoNet(planes, h, w, bw, bh, channels=args.channels, blocks=args.blocks)

    # Each generation continues from the last rather than restarting, so early generations
    # are not thrown away every cycle.
    if args.init:
        ck = torch.load(args.init, map_location="cpu")
        net.load_state_dict(ck["state_dict"])
        print(f"warm-started from {args.init}")

    opt = torch.optim.Adam(net.parameters(), lr=args.lr, weight_decay=1e-4)

    chess_frac = ds["is_chess"].float().mean().item()
    print(f"{n} samples from {len(args.data)} file(s) | {planes}x{h}x{w} planes | "
          f"board {bw}x{bh} | chess nodes {chess_frac:.0%}")
    print(f"net: {net.parameter_count():,} parameters "
          f"({args.blocks} blocks x {args.channels} channels)")
    print(f"policy heads: chess {net.chess_size}, intersection {net.inter_size}")
    if args.overfit:
        print("OVERFIT MODE: policy loss should approach the entropy floor, value loss ~0")
    print()

    batch = min(args.batch, n)
    t0 = time.time()

    for epoch in range(1, args.epochs + 1):
        net.train()
        perm = torch.randperm(n)
        tot_p = tot_v = tot_e = 0.0
        steps = 0

        for i in range(0, n, batch):
            sel = perm[i:i + batch]
            out = net(ds["planes"][sel])

            p_loss, entropy = masked_policy_loss(
                out, ds["is_chess"][sel], ds["idx"][sel],
                ds["target"][sel], ds["mask"][sel])
            v_loss = F.mse_loss(out["value"], ds["value"][sel])

            loss = p_loss + v_loss
            opt.zero_grad()
            loss.backward()
            opt.step()

            tot_p += p_loss.item(); tot_v += v_loss.item(); tot_e += entropy.item()
            steps += 1

        if epoch == 1 or epoch % max(1, args.epochs // 10) == 0 or epoch == args.epochs:
            print(f"epoch {epoch:4d}  policy {tot_p/steps:6.4f}  "
                  f"(floor {tot_e/steps:6.4f}, excess {(tot_p-tot_e)/steps:+7.4f})  "
                  f"value {tot_v/steps:6.4f}")

    print(f"\n{time.time()-t0:.1f}s")

    if args.out:
        torch.save({"state_dict": net.state_dict(),
                    "shape": ds["shape"], "board": ds["board"],
                    "blocks": args.blocks, "channels": args.channels}, args.out)
        print(f"saved {args.out}")


if __name__ == "__main__":
    main()
