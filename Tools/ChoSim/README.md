# ChoSim — headless harness for the Cho engine

Runs `SimRules` / `SimSearch` / `SimZobrist` **outside Unity** so engine changes can be
measured instead of guessed at.

The game sources are *not* copied — `ChoSim.csproj` compiles the real files straight out of
`Assets/Scripts/`. Edit the engine, rebuild here, and you are measuring the same code Unity runs.

`UnityShim.cs` supplies the only Unity surface those files touch: `Vector2Int`, `Mathf.Abs/Max/Min`,
`Debug.Log`, and the `PieceColor` / `PieceType` enums. It lives outside `Assets/`, so Unity never
compiles it and it cannot collide with the real UnityEngine types.

## Running

```sh
cd "Tools/ChoSim"
dotnet build -c Release
./bin/Release/net10.0/ChoSim <command> [flags]
```

**Always measure in Release.** `dotnet build` defaults to Debug (`Optimize=false`), which
reports roughly a third of real throughput and is not what Unity ships.

| Command | What it answers |
|---|---|
| `show --plies N` | What does a position look like? (pieces + stones in one diagram) |
| `branching --games G --turns T` | How wide is each kind of decision node, really? |
| `bench --depth D --plies N` | Nodes, nodes/sec, and chosen move at depths 1..D |
| `profile --plies N` | Where does node time and allocation actually go? |
| `match --a SPEC --b SPEC --games G` | Is version A stronger than version B? |

Agent `SPEC` is `kind:depth`, kind ∈ `search` (one depth for every decision node),
`legacy` (reproduces the policy GameController ships today), `random` (strength floor).

## Measuring a change

`match` is deterministic: agents use a fixed depth and an effectively unlimited time budget, so
results depend only on engine behaviour, not on how loaded the machine is. Colors swap every game.

```sh
# before/after on the same seed, same number of games
dotnet run --no-build -- match --a legacy:3 --b search:3 --games 8 --seed 11
```

`bench` and `profile` *are* timing-sensitive — don't trust them while a `match` is running.

## Notes

- `Positions.StartPosition()` starts past the opening black-stone rule, because `SimRules`
  does not model it (`ApplyFullTurn` early-returns on `isInitialBlackStoneTurn` and the
  generator never emits one).
- `SimRules` has no pass/stalemate concept. When a node has no legal decision, `Driver.Step`
  resolves it the same way `GameController` does — by skipping the phase — and returns `false`
  so callers can count it.
- Evaluation knobs on `SimRules` are `static`, so `Knobs.Apply` re-applies them before every
  search; two differently-configured agents share one process during a match.
- `SimSearch.NodesSearched` is instrumentation added for this harness. It is the only change
  made to the shipped engine so far. Pre-change copies of all `Sim*.cs` are in `_backup/`.
