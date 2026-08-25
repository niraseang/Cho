using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChoSim
{
    public sealed class AgentConfig
    {
        public string Name = "search";
        public int Depth = 4;
        public int TimeBudgetMs = 1_000_000; // effectively unlimited: keeps A/B runs deterministic
        public bool UseMobilityEval = true;
        public int MobilityWeight = 2;

        public AgentConfig Clone() => (AgentConfig)MemberwiseClone();
        public override string ToString() => $"{Name}(d={Depth})";
    }

    public interface IAgent
    {
        string Name { get; }
        SimTurn? Choose(SimState state);
    }

    static class Knobs
    {
        // SimRules exposes evaluation settings as statics, so they must be re-applied
        // before every search when two differently-configured agents share a process.
        public static void Apply(AgentConfig cfg)
        {
            SimRules.useMobilityEval = cfg.UseMobilityEval;
            SimRules.mobilityWeight = cfg.MobilityWeight;
        }
    }

    /// <summary>Uniform random legal decision. Strength baseline.</summary>
    public sealed class RandomAgent : IAgent
    {
        readonly Random _rng;
        public string Name { get; }

        public RandomAgent(int seed, string name = "random")
        {
            _rng = new Random(seed);
            Name = name;
        }

        public SimTurn? Choose(SimState state)
        {
            var moves = SimRules.GenerateAllLegalFullTurns(state);
            if (moves == null || moves.Count == 0) return null;
            return moves[_rng.Next(moves.Count)];
        }
    }

    /// <summary>One search configuration applied to every decision node.</summary>
    public sealed class SearchAgent : IAgent
    {
        readonly AgentConfig _cfg;
        public string Name => _cfg.Name;

        public SearchAgent(AgentConfig cfg) { _cfg = cfg; }

        public SimTurn? Choose(SimState state)
        {
            var moves = SimRules.GenerateAllLegalFullTurns(state);
            if (moves == null || moves.Count == 0) return null;

            Knobs.Apply(_cfg);
            var t = SimSearch.FindBestTurn(state, _cfg.Depth, _cfg.TimeBudgetMs);

            // FindBestTurn returns default(SimTurn) when it cannot pick; fall back to a legal move
            // rather than stalling the game.
            if (!t.chessMove.HasValue && !t.mainStone.HasValue
                && !t.bonusPawnStone.HasValue && !t.territoryRemoval.HasValue)
            {
                return moves[0];
            }
            return t;
        }
    }

    /// <summary>
    /// Reproduces the policy GameController actually ships today:
    ///   phase 1 (chess)      -> FindBestTurn at aiMaxDepth
    ///   territory removal    -> depth-0 greedy on EvaluateForSideToMove
    ///   pawn bonus stone     -> depth-0 greedy on EvaluateForSideToMove
    ///   phase 2 (main stone) -> a separate FindBestTurn, depth clamped to 1..2
    /// Each decision starts from a fresh search, so the chess move never sees the stone.
    /// </summary>
    public sealed class LegacyAgent : IAgent
    {
        readonly AgentConfig _cfg;
        public string Name => _cfg.Name;

        public LegacyAgent(AgentConfig cfg) { _cfg = cfg; }

        public SimTurn? Choose(SimState state)
        {
            var moves = SimRules.GenerateAllLegalFullTurns(state);
            if (moves == null || moves.Count == 0) return null;

            Knobs.Apply(_cfg);

            if (state.waitingForTerritoryClick || state.waitingForPawnStoneChoice)
                return Greedy(state, moves);

            int depth = state.phaseOne ? _cfg.Depth : Math.Clamp(_cfg.Depth, 1, 2);
            var t = SimSearch.FindBestTurn(state, depth, _cfg.TimeBudgetMs);

            if (!t.chessMove.HasValue && !t.mainStone.HasValue
                && !t.bonusPawnStone.HasValue && !t.territoryRemoval.HasValue)
            {
                return moves[0];
            }
            return t;
        }

        static SimTurn Greedy(SimState state, List<SimTurn> moves)
        {
            SimTurn best = moves[0];
            int bestScore = int.MinValue;
            foreach (var m in moves)
            {
                var child = state.DeepCopy();
                SimRules.ApplyFullTurn(child, m);
                int score = SimRules.EvaluateForSideToMove(child);
                if (score > bestScore) { bestScore = score; best = m; }
            }
            return best;
        }
    }

    public enum GameOutcome { WhiteWins, BlackWins, Draw, TurnLimit }

    public sealed class GameResult
    {
        public GameOutcome Outcome;
        public int Turns;
        public int Decisions;
        public string Reason = "";
        public SimState Final;
    }

    public static class Driver
    {
        // Diagnostics for the superko restriction, reset by PlayGame.
        public static long SuperkoBlocked;   // candidate placements refused
        public static long SuperkoFallbacks; // turns where every placement was refused

        /// <summary>
        /// Advances one decision node. Returns false when the position is stuck
        /// (no legal decision) and the harness had to unstick it.
        /// </summary>
        public static bool Step(SimState s, IAgent agent, out SimTurn applied)
        {
            applied = default;

            var choice = agent.Choose(s);
            if (choice.HasValue)
            {
                applied = choice.Value;
                SimRules.ApplyFullTurn(s, applied);
                return true;
            }

            // No legal decision at this node. SimRules has no pass/stalemate concept,
            // so the harness resolves it the same way GameController does when the AI
            // finds nothing: skip the phase. These are counted and reported.
            if (s.phaseOne)
            {
                s.phaseOne = false;
                s.lastMovedSquare = null;
                s.waitingForTerritoryClick = false;
                s.waitingForPawnStoneChoice = false;
                s.pendingPawnCornerOptions?.Clear();
            }
            else if (s.waitingForTerritoryClick)
            {
                s.waitingForTerritoryClick = false;
            }
            else if (s.waitingForPawnStoneChoice)
            {
                s.waitingForPawnStoneChoice = false;
                s.pendingPawnCornerOptions?.Clear();
            }
            else
            {
                EndTurnWithoutStone(s);
            }
            return false;
        }

        /// <summary>Go board full (or fully illegal): end the turn as ApplyFullTurn would.</summary>
        static void EndTurnWithoutStone(SimState s)
        {
            s.currentPlayer = s.currentPlayer == PieceColor.White ? PieceColor.Black : PieceColor.White;
            s.phaseOne = true;
            s.waitingForTerritoryClick = false;
            s.waitingForPawnStoneChoice = false;
            s.pendingPawnCornerOptions?.Clear();

            for (int x = 0; x < s.boardWidth; x++)
                for (int y = 0; y < s.boardHeight; y++)
                {
                    var sp = s.squares[x, y];
                    if (!sp.HasValue) continue;
                    var p = sp.Value;
                    if (p.type == PieceType.Pawn && p.color == s.currentPlayer)
                    {
                        p.justDoubleStepped = false;
                        s.squares[x, y] = p;
                    }
                }
        }

        public static GameResult PlayGame(
            IAgent white,
            IAgent black,
            int maxTurns = 200,
            int randomOpeningDecisions = 0,
            int seed = 0,
            Action<SimState, SimTurn, IAgent> onDecision = null,
            Variant variant = Variant.Standard)
        {
            var s = Positions.Create(variant);
            var opening = new RandomAgent(seed, "opening");
            var seen = new Dictionary<ulong, int>();

            SuperkoBlocked = 0;
            SuperkoFallbacks = 0;

            int decisions = 0;
            int turns = 0;

            for (int i = 0; i < randomOpeningDecisions && !s.gameOver; i++)
            {
                Step(s, opening, out _);
                decisions++;
            }

            var prevPlayer = s.currentPlayer;

            // Hard ceiling on decisions as a backstop: a turn is normally 2-4 decisions.
            int decisionCap = maxTurns * 8;

            while (!s.gameOver && turns < maxTurns && decisions < decisionCap)
            {
                var agent = s.currentPlayer == PieceColor.White ? white : black;

                if (Step(s, agent, out var applied) && onDecision != null)
                    onDecision(s, applied, agent);

                decisions++;

                if (s.currentPlayer != prevPlayer)
                {
                    prevPlayer = s.currentPlayer;
                    turns++;

                    // Record the position that was actually reached, so superko forbids
                    // returning to it later in this game.
                    s.positionHistory?.Push(SimZobrist.ComputeBoardHash(s));

                    ulong key = SimZobrist.ComputeHash(s);
                    seen.TryGetValue(key, out int n);
                    seen[key] = n + 1;
                    if (n + 1 >= 3)
                    {
                        return new GameResult
                        {
                            Outcome = GameOutcome.Draw,
                            Turns = turns,
                            Decisions = decisions,
                            Reason = "threefold repetition",
                            Final = s
                        };
                    }
                }
            }

            GameOutcome outcome;
            string reason;
            if (s.gameOver && s.winner.HasValue)
            {
                outcome = s.winner.Value == PieceColor.White ? GameOutcome.WhiteWins : GameOutcome.BlackWins;
                reason = "king captured";
            }
            else if (s.gameOver)
            {
                outcome = GameOutcome.Draw;
                reason = s.noProgressTurns >= SimRules.noProgressTurnLimit
                    ? $"no progress for {s.noProgressTurns} turns"
                    : "game over, no winner";
            }
            else
            {
                outcome = GameOutcome.TurnLimit;
                reason = decisions >= decisionCap ? "decision cap" : "turn limit";
            }

            return new GameResult
            {
                Outcome = outcome,
                Turns = turns,
                Decisions = decisions,
                Reason = reason,
                Final = s
            };
        }
    }
}
