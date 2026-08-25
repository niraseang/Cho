# Training pipeline

PyTorch side of the Cho network. The rules, MCTS, feature encoding and self-play all
live in C# (`Assets/Scripts/Sim*.cs`, driven by `Tools/ChoSim`); this directory only
trains.

## Setup

```sh
cd Tools/train
python3 -m venv .venv
./.venv/bin/python -m pip install -r requirements.txt
```

Two version constraints, both real:

- **torch 2.2.2 is the last release with macOS x86_64 wheels.** On an Intel Mac you are
  pinned there. Linux and Apple Silicon can use any recent 2.x.
- **numpy must stay below 2.** torch 2.2.x is built against the numpy 1.x ABI and fails at
  import with `RuntimeError: Numpy is not available` — which surfaces as a confusing error
  deep inside `torch.from_numpy`, not at install time.

## The one duplicated piece

`cho_data.encode` is a port of `SimFeatures.Encode`. It is the only place this project
accepts a reimplementation, because shipping 5.5 KB float tensors instead of 157-byte
positions would cost ~55 GB at ten million samples.

It is kept honest by a golden reference rather than by trust:

```sh
../ChoSim/bin/Release/net10.0/ChoSim goldens --variant small --count 400 --out goldens.bin
./.venv/bin/python test_features.py goldens.bin
```

ChoSim writes positions together with the planes it actually produced; the test asserts
this port reproduces them exactly. **Run it after touching either encoder.**

## Generating data and training

```sh
../ChoSim/bin/Release/net10.0/ChoSim selfplay --variant small --games 200 --sims 200 --out sp.bin
../ChoSim/bin/Release/net10.0/ChoSim inspect --in sp.bin      # decodes and re-derives planes

./.venv/bin/python train.py --data sp.bin --overfit 96 --epochs 300   # wiring check
./.venv/bin/python train.py --data sp.bin --epochs 40 --out net.pt    # real run
```

### Read the overfit output correctly

Policy loss **cannot reach zero**: the target is the search's visit distribution, not a
label, so a perfect fit scores the target's own entropy. `train.py` prints that floor and
the excess over it. A raw loss of 1.74 sitting on a floor of 1.73 is a perfect fit, and
the `excess` column is the number to watch.

Healthy run: excess falls from ~0.9 to under 0.02 and value loss to ~0.004. If it does
not, the wiring is wrong and more data will not help.

## Network

~340k parameters at 4 blocks x 64 channels.

Two policy heads, because the game has two action spaces:

- **chess** — `from x to` over squares. AlphaZero-chess's ray-based encoding cannot be
  used: the chain rule lets a rook turn a corner, so a destination is not reachable along
  a ray from its origin. Kept spatial as one output channel per destination square, read
  at the origin square's cell, so `logit[from, to]` falls out of a 1x1 convolution. A
  dense `Linear(32*H*W -> 900)` also works but costs 1.2M parameters, four times the rest
  of the network combined.
- **intersection** — shared by main stones, territory removals and pawn bonus stones. All
  three are "which intersection", and the phase planes disambiguate.

The trunk is deliberately shallow. On a 6x7 board a 3x3 convolution covers the whole board
after about three blocks, so more depth buys refinement, not reach.

The value head is a scalar in [-1, 1]. Draws are currently rare, so a three-way WDL head
is the upgrade if that changes.

## Not done yet

Step 4 closes the loop: export to ONNX, load it in C# via `Microsoft.ML.OnnxRuntime`, and
have `SimMcts` call the network instead of `SimRules.Evaluate` for leaves and instead of
uniform priors. The benchmark to beat is **12.5% against `search:3`**, which is where MCTS
with uniform priors currently sits.

## Step 4: closing the loop

```sh
# train, export, and write a parity file in one pass
./.venv/bin/python train.py --data sp.bin --epochs 30 --out net.pt
../ChoSim/bin/Release/net10.0/ChoSim goldens --variant small --count 400 --out goldens.bin
./.venv/bin/python export.py --model net.pt --out net.onnx --positions goldens.bin --parity parity.bin

# verify the C# side reproduces PyTorch before trusting any game result
../ChoSim/bin/Release/net10.0/ChoSim parity --variant small --in parity.bin --model net.onnx

# play with it
../ChoSim/bin/Release/net10.0/ChoSim match --variant small --a nn:400 --b mcts:400 \
    --games 40 --model net.onnx
```

`parity` is the one to run after touching anything in the chain. It decodes each position
in C#, encodes planes with SimFeatures, runs the ONNX model, and compares against what
PyTorch produced for the same position - so the encoder port, the export and the runtime
are all covered by a single check.

`SimMcts` talks to `ISimEvaluator`, not to ONNX. The runtime dependency lives here in
ChoSim only, so the Unity assembly stays clean and could back the same interface with
Sentis instead.

## Measured so far (5x6 board)

A network trained on just 40 self-play games, against uniform-prior MCTS at equal
simulation counts:

| simulations | nn vs mcts | significance |
|---|---|---|
| 100 | **69.0%** (207-93), +139 Elo | z = 6.58, p = 4.6e-11 |
| 400 | 55.0% (22-18) | z = 0.63, p = 0.53 — not significant |

The policy head earns its keep where simulations are scarce and stops mattering as they
grow, which is the expected shape for a prior: with enough visits, uniform priors find
good moves anyway.

Neither beats the alpha-beta engine yet — `search:3` wins comfortably against both.

Note on sample sizes: the 100-simulation result first appeared as 85% over 20 games and
settled at 69% over 300. Twenty games is not a measurement. `match` is deterministic at
fixed depth, so a larger run costs only wall-clock.
