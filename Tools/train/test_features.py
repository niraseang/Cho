"""
Asserts the Python encoder reproduces the C# one exactly.

Regenerate the reference with:
    ChoSim goldens --variant small --count 400 --out goldens.bin
"""

import sys
import numpy as np
import cho_data as cd


def main(path):
    g = read = cd.read_goldens(path)
    limit = g["no_progress_limit"]
    pairs = g["pairs"]

    if not pairs:
        print("FAIL  no golden positions in file")
        return 1

    mismatched = []
    for i, (pos_bytes, expected_flat) in enumerate(pairs):
        p = cd.decode_position(pos_bytes)
        got = cd.encode(p, limit)

        expected = expected_flat.reshape(cd.PLANE_COUNT, p.ih, p.iw)
        if not np.array_equal(got, expected):
            diff = np.argwhere(got != expected)
            mismatched.append((i, len(diff), diff[:3].tolist()))

    total = len(pairs)
    if mismatched:
        print(f"FAIL  {len(mismatched)}/{total} positions differ from the C# encoder")
        for i, n, where in mismatched[:5]:
            planes = sorted({int(w[0]) for w in where})
            print(f"        position {i}: {n} cells differ, first planes {planes}")
        return 1

    # Also confirm the reference is not trivially all zeros, which would make equality meaningless.
    nonzero = sum(int(np.count_nonzero(f)) for _, f in pairs) / total
    print(f"PASS  {total} positions match the C# encoder exactly "
          f"({nonzero:.0f} non-zero cells per position)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "goldens.bin"))
