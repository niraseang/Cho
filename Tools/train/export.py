"""
Exports a trained net to ONNX, and writes a parity file so the C# side can be checked.

The parity file holds positions together with the outputs PyTorch produced for them. C#
decodes each position, encodes planes with SimFeatures, runs the ONNX model, and compares.
That covers the whole chain in one test - encoder port, model export and runtime - rather
than trusting any link in it.
"""

import argparse
import struct

import numpy as np
import torch

import cho_data as cd
from model import ChoNet


def load(path):
    ck = torch.load(path, map_location="cpu")
    planes, h, w = ck["shape"]
    bw, bh = ck["board"]
    net = ChoNet(planes, h, w, bw, bh,
                 channels=ck.get("channels", 64), blocks=ck.get("blocks", 4))
    net.load_state_dict(ck["state_dict"])
    net.eval()
    return net, ck


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="net.pt")
    ap.add_argument("--out", default="net.onnx")
    ap.add_argument("--positions", default="goldens.bin",
                    help="source of positions for the parity file")
    ap.add_argument("--parity", default="parity.bin")
    ap.add_argument("--parity-count", type=int, default=200)
    args = ap.parse_args()

    net, ck = load(args.model)
    planes, h, w = ck["shape"]

    dummy = torch.zeros(1, planes, h, w)
    torch.onnx.export(
        net, dummy, args.out,
        input_names=["planes"],
        output_names=["chess", "promo", "inter", "value"],
        dynamic_axes={"planes": {0: "batch"},
                      "chess": {0: "batch"}, "promo": {0: "batch"},
                      "inter": {0: "batch"}, "value": {0: "batch"}},
        opset_version=17,
    )
    print(f"exported {args.out}  ({planes}x{h}x{w} in)")

    # --- parity ---------------------------------------------------------
    g = cd.read_goldens(args.positions)
    limit = g["no_progress_limit"]
    pairs = g["pairs"][:args.parity_count]

    with open(args.parity, "wb") as f:
        f.write(b"CHOPR1")
        f.write(struct.pack("<i", len(pairs)))

        for pos_bytes, _ in pairs:
            p = cd.decode_position(pos_bytes)
            x = torch.from_numpy(cd.encode(p, limit)).unsqueeze(0)

            with torch.no_grad():
                out = net(x)

            f.write(struct.pack("<H", len(pos_bytes)))
            f.write(pos_bytes)
            for key in ("chess", "promo", "inter"):
                arr = out[key][0].numpy().astype("<f4")
                f.write(struct.pack("<i", arr.size))
                f.write(arr.tobytes())
            f.write(struct.pack("<f", float(out["value"][0])))

    print(f"wrote {args.parity}  ({len(pairs)} positions)")

    # Sanity: confirm onnxruntime agrees with PyTorch before C# ever sees the file.
    try:
        import onnxruntime as ort
        sess = ort.InferenceSession(args.out, providers=["CPUExecutionProvider"])
        p = cd.decode_position(pairs[0][0])
        x = cd.encode(p, limit)[None]
        ort_out = sess.run(None, {"planes": x})
        with torch.no_grad():
            ref = net(torch.from_numpy(x))
        worst = max(
            float(np.abs(ort_out[i] - ref[k][None if ort_out[i].ndim > ref[k].ndim else slice(None)].numpy()).max())
            for i, k in enumerate(("chess", "promo", "inter", "value"))
        )
        print(f"onnxruntime vs pytorch: max abs diff {worst:.2e}")
    except Exception as e:
        print(f"(onnxruntime check skipped: {e})")


if __name__ == "__main__":
    main()
