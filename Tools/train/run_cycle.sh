#!/usr/bin/env bash
# One generation of the training loop: self-play -> train -> export -> verify -> measure.
#
#   ./run_cycle.sh                 defaults: 200 games, 200 sims, 30 epochs
#   GAMES=1000 SIMS=400 ./run_cycle.sh
#
# Artifacts land in Tools/train/run/ and are gitignored.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHOSIM="$HERE/../ChoSim/bin/Release/net10.0/ChoSim"
PY="$HERE/.venv/bin/python"
OUT="$HERE/run"

GAMES="${GAMES:-200}"
SIMS="${SIMS:-200}"
EPOCHS="${EPOCHS:-30}"
VARIANT="${VARIANT:-small}"
EVAL_GAMES="${EVAL_GAMES:-40}"

for f in "$CHOSIM" "$PY"; do
  [ -x "$f" ] || { echo "missing: $f"; echo "build ChoSim with: cd $HERE/../ChoSim && dotnet build -c Release"; exit 1; }
done
mkdir -p "$OUT"

echo "=== 1/5  self-play: $GAMES games at $SIMS simulations ==="
"$CHOSIM" selfplay --variant "$VARIANT" --games "$GAMES" --sims "$SIMS" \
    --out "$OUT/sp.bin" --seed "${SEED:-1}" --quiet

echo
echo "=== 2/5  train: $EPOCHS epochs ==="
"$PY" "$HERE/train.py" --data "$OUT/sp.bin" --epochs "$EPOCHS" --out "$OUT/net.pt"

echo
echo "=== 3/5  export to ONNX ==="
"$CHOSIM" goldens --variant "$VARIANT" --count 200 --out "$OUT/goldens.bin"
"$PY" "$HERE/export.py" --model "$OUT/net.pt" --out "$OUT/net.onnx" \
    --positions "$OUT/goldens.bin" --parity "$OUT/parity.bin"

echo
echo "=== 4/5  verify C# reproduces PyTorch ==="
"$CHOSIM" parity --variant "$VARIANT" --in "$OUT/parity.bin" --model "$OUT/net.onnx"

echo
echo "=== 5/5  measure: does the network beat uniform priors at equal simulations? ==="
"$CHOSIM" match --variant "$VARIANT" --a "nn:$SIMS" --b "mcts:$SIMS" \
    --games "$EVAL_GAMES" --turns 200 --model "$OUT/net.onnx" --seed 999 | tail -4

echo
echo "artifacts in $OUT"
