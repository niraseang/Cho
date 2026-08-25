using System.Collections.Generic;
using System.Diagnostics;

public static class SimSearch
{
    // Quiescence depth cap to prevent capture-sequence explosions.
    // This only applies after the main depth hits 0.
    const int DefaultQuiescenceMaxPlies = 8;

    enum Bound : byte
    {
        Exact = 0,
        Lower = 1,
        Upper = 2
    }

    struct TTEntry
    {
        public int depth;
        public int value;
        public Bound bound;
    }

    // Keep a per-search TT to reduce allocations and avoid stale cross-position interactions.
    static readonly Dictionary<ulong, TTEntry> _tt = new Dictionary<ulong, TTEntry>(capacity: 200_000);

    // Instrumentation: nodes visited during the most recent FindBestTurn call.
    // Read by the headless harness (Tools/ChoSim) to measure nodes/sec and pruning.
    public static long NodesSearched;

    public static SimTurn FindBestTurn(SimState root, int maxDepth = 4, int timeBudgetMs = 1000)
    {
        if (root == null) return default;
        if (maxDepth < 1) maxDepth = 1;
        if (timeBudgetMs < 1) timeBudgetMs = 1;

        _tt.Clear();
        NodesSearched = 0;

        // Root move generation can be non-trivial (especially in phase 2 with many Go candidates).
        // The time budget is intended primarily for the search itself, so start the stopwatch after
        // generating root moves.
        var rootTurns = SimRules.GenerateAllLegalFullTurns(root);
        if (rootTurns == null || rootTurns.Count == 0) return default;

        // Iterative deepening: keep the best move from the last fully completed depth.
        // IMPORTANT: if a deeper iteration aborts early (time budget), do NOT replace bestSoFar
        // with a move that may have been the only evaluated candidate.
        SimTurn bestSoFar = rootTurns[0];

        var sw = Stopwatch.StartNew();

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            // Stop if out of time before starting this depth.
            if (sw.ElapsedMilliseconds >= timeBudgetMs) break;

            bool aborted = false;
            int bestScoreAtDepth = int.MinValue;
            SimTurn bestAtDepth = bestSoFar;
            int evaluatedAtDepth = 0;

            // Simple move ordering: try previous best first.
            for (int i = 0; i < rootTurns.Count; i++)
            {
                var t = rootTurns[i];
                if (!TurnsEqual(t, bestSoFar)) continue;
                if (i == 0) break;
                rootTurns.RemoveAt(i);
                rootTurns.Insert(0, t);
                break;
            }

            // Then order remaining moves by capture priority (king captures and big captures first).
            if (rootTurns.Count > 2)
            {
                var pv = rootTurns[0];
                rootTurns.Sort((a, b) =>
                {
                    int kb = GetTurnOrderKey(root, b, pv);
                    int ka = GetTurnOrderKey(root, a, pv);
                    return kb.CompareTo(ka);
                });
            }

            foreach (var t in rootTurns)
            {
                if (sw.ElapsedMilliseconds >= timeBudgetMs)
                {
                    aborted = true;
                    break;
                }

                SimState child = root.DeepCopy();
                SimRules.ApplyFullTurn(child, t);

                bool samePlayerToMove = child.currentPlayer == root.currentPlayer;
                bool pushed = PushHistory(child, root, out ulong histHash);
                int score;
                try
                {
                    score = samePlayerToMove
                        ? Negamax(child, depth - 1, int.MinValue + 1, int.MaxValue - 1, sw, timeBudgetMs, ref aborted)
                        : -Negamax(child, depth - 1, int.MinValue + 1, int.MaxValue - 1, sw, timeBudgetMs, ref aborted);
                }
                finally
                {
                    if (pushed) child.positionHistory.Pop(histHash);
                }
                if (aborted) break;

                evaluatedAtDepth++;

                if (score > bestScoreAtDepth)
                {
                    bestScoreAtDepth = score;
                    bestAtDepth = t;
                }
            }

            // Only promote the best move when this depth finished evaluating the full root move list.
            // If we abort mid-depth, keep bestSoFar from the last completed depth.
            if (!aborted && evaluatedAtDepth == rootTurns.Count)
            {
                bestSoFar = bestAtDepth;
            }

            if (aborted) break;
        }

        return bestSoFar;
    }

    static int Negamax(
        SimState state,
        int depth,
        int alpha,
        int beta,
        Stopwatch sw,
        int timeBudgetMs,
        ref bool aborted)
    {
        if (aborted) return 0;
        if (sw.ElapsedMilliseconds >= timeBudgetMs)
        {
            aborted = true;
            return 0;
        }

        NodesSearched++;

        // Transposition table lookup.
        // Values are always from the current side-to-move perspective (negamax convention).
        int origAlpha = alpha;
        ulong key = SimZobrist.ComputeHash(state);
        if (_tt.TryGetValue(key, out var cached) && cached.depth >= depth)
        {
            switch (cached.bound)
            {
                case Bound.Exact:
                    return cached.value;
                case Bound.Lower:
                    if (cached.value > alpha) alpha = cached.value;
                    break;
                case Bound.Upper:
                    if (cached.value < beta) beta = cached.value;
                    break;
            }
            if (alpha >= beta) return cached.value;
        }

        // Terminal
        if (state.gameOver)
        {
            int terminal = SimRules.EvaluateForSideToMove(state);
            StoreTT(key, depth, terminal, origAlpha, beta);
            return terminal;
        }

        // Leaf: use quiescence search to resolve capture sequences.
        if (depth <= 0)
        {
            int q = Quiescence(state, alpha, beta, DefaultQuiescenceMaxPlies, sw, timeBudgetMs, ref aborted);
            StoreTT(key, depth, q, origAlpha, beta);
            return q;
        }

        var turns = SimRules.GenerateAllLegalFullTurns(state, SimRules.superkoInSearch);
        if (turns == null || turns.Count == 0)
        {
            int leaf = SimRules.EvaluateForSideToMove(state);
            StoreTT(key, depth, leaf, origAlpha, beta);
            return leaf;
        }

        // Capture-first ordering helps alpha-beta a lot.
        if (turns.Count > 1)
        {
            turns.Sort((a, b) => GetTurnOrderKey(state, b, default).CompareTo(GetTurnOrderKey(state, a, default)));
        }

        int best = int.MinValue;

        foreach (var t in turns)
        {
            if (sw.ElapsedMilliseconds >= timeBudgetMs)
            {
                aborted = true;
                break;
            }

            SimState child = state.DeepCopy();
            SimRules.ApplyFullTurn(child, t);

            bool samePlayerToMove = child.currentPlayer == state.currentPlayer;
            bool pushed = PushHistory(child, state, out ulong histHash);
            int score;
            try
            {
                score = samePlayerToMove
                    ? Negamax(child, depth - 1, alpha, beta, sw, timeBudgetMs, ref aborted)
                    : -Negamax(child, depth - 1, -beta, -alpha, sw, timeBudgetMs, ref aborted);
            }
            finally
            {
                if (pushed) child.positionHistory.Pop(histHash);
            }
            if (aborted) break;

            if (score > best) best = score;
            if (score > alpha) alpha = score;
            if (alpha >= beta) break; // alpha-beta cutoff
        }

        StoreTT(key, depth, best, origAlpha, beta);
        return best;
    }

    static int Quiescence(
        SimState state,
        int alpha,
        int beta,
        int remainingPlies,
        Stopwatch sw,
        int timeBudgetMs,
        ref bool aborted)
    {
        if (aborted) return 0;
        if (sw.ElapsedMilliseconds >= timeBudgetMs)
        {
            aborted = true;
            return 0;
        }

        NodesSearched++;

        int origAlpha = alpha;
        ulong key = SimZobrist.ComputeHash(state);
        int depthKey = -remainingPlies; // distinguish from main-search depths
        if (_tt.TryGetValue(key, out var cached) && cached.depth >= depthKey)
        {
            switch (cached.bound)
            {
                case Bound.Exact:
                    return cached.value;
                case Bound.Lower:
                    if (cached.value > alpha) alpha = cached.value;
                    break;
                case Bound.Upper:
                    if (cached.value < beta) beta = cached.value;
                    break;
            }
            if (alpha >= beta) return cached.value;
        }

        // Stand-pat evaluation (side-to-move perspective).
        int standPat = SimRules.EvaluateForSideToMove(state);
        if (standPat >= beta) return beta;
        if (standPat > alpha) alpha = standPat;

        if (remainingPlies <= 0)
        {
            StoreTT(key, depthKey, standPat, origAlpha, beta);
            return standPat;
        }
        if (state.gameOver)
        {
            StoreTT(key, depthKey, standPat, origAlpha, beta);
            return standPat;
        }

        // Only consider capture moves in quiescence.
        var captureTurns = GenerateCaptureTurns(state);
        if (captureTurns.Count == 0)
        {
            StoreTT(key, depthKey, standPat, origAlpha, beta);
            return standPat;
        }

        // Order captures: king captures first, then larger captures.
        if (captureTurns.Count > 1)
        {
            captureTurns.Sort((a, b) => GetTurnOrderKey(state, b, default).CompareTo(GetTurnOrderKey(state, a, default)));
        }

        foreach (var t in captureTurns)
        {
            if (sw.ElapsedMilliseconds >= timeBudgetMs)
            {
                aborted = true;
                break;
            }

            SimState child = state.DeepCopy();
            SimRules.ApplyFullTurn(child, t);

            bool samePlayerToMove = child.currentPlayer == state.currentPlayer;
            bool pushed = PushHistory(child, state, out ulong histHash);
            int score;
            try
            {
                score = samePlayerToMove
                    ? Quiescence(child, alpha, beta, remainingPlies - 1, sw, timeBudgetMs, ref aborted)
                    : -Quiescence(child, -beta, -alpha, remainingPlies - 1, sw, timeBudgetMs, ref aborted);
            }
            finally
            {
                if (pushed) child.positionHistory.Pop(histHash);
            }
            if (aborted) break;

            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }

        StoreTT(key, depthKey, alpha, origAlpha, beta);
        return alpha;
    }

    /// <summary>
    /// Records the position the search just moved into, so superko sees the search path and not
    /// just the moves actually played. Only turn handovers are game positions, so mid-turn nodes
    /// are skipped. Every push must be matched by a Pop - see the finally blocks at each call.
    /// </summary>
    static bool PushHistory(SimState child, SimState parent, out ulong hash)
    {
        hash = 0UL;
        if (!SimRules.superkoInSearch) return false;
        if (child.positionHistory == null) return false;
        if (child.currentPlayer == parent.currentPlayer) return false;

        hash = SimZobrist.ComputeBoardHash(child);
        child.positionHistory.Push(hash);
        return true;
    }

    static void StoreTT(ulong key, int depth, int value, int origAlpha, int beta)
    {
        Bound bound;
        if (value <= origAlpha) bound = Bound.Upper;
        else if (value >= beta) bound = Bound.Lower;
        else bound = Bound.Exact;

        _tt[key] = new TTEntry
        {
            depth = depth,
            value = value,
            bound = bound
        };
    }

    static List<SimTurn> GenerateCaptureTurns(SimState state)
    {
        var all = SimRules.GenerateAllLegalFullTurns(state, SimRules.superkoInSearch);
        if (all == null || all.Count == 0) return new List<SimTurn>();

        var captures = new List<SimTurn>();

        foreach (var t in all)
        {
            if (!t.chessMove.HasValue) continue;
            var mv = t.chessMove.Value;

            // A capture occurs if the destination square is occupied by an enemy piece.
            var dest = state.squares[mv.to.x, mv.to.y];
            if (!dest.HasValue) continue;

            var mover = state.squares[mv.from.x, mv.from.y];
            if (!mover.HasValue) continue;
            if (dest.Value.color == mover.Value.color) continue;

            captures.Add(t);
        }

        return captures;
    }

    static bool TurnsEqual(SimTurn a, SimTurn b)
    {
        if (a.isInitialBlackStoneTurn != b.isInitialBlackStoneTurn) return false;
        if (a.chessMove.HasValue != b.chessMove.HasValue) return false;
        if (a.chessMove.HasValue)
        {
            var am = a.chessMove.Value;
            var bm = b.chessMove.Value;
            if (am.from != bm.from) return false;
            if (am.to != bm.to) return false;
            if (am.promotion != bm.promotion) return false;
        }

        if (a.territoryRemoval.HasValue != b.territoryRemoval.HasValue) return false;
        if (a.territoryRemoval.HasValue && a.territoryRemoval.Value.intersection != b.territoryRemoval.Value.intersection) return false;

        if (a.mainStone.HasValue != b.mainStone.HasValue) return false;
        if (a.mainStone.HasValue)
        {
            var aa = a.mainStone.Value;
            var bb = b.mainStone.Value;
            if (aa.intersection != bb.intersection) return false;
            if (aa.color != bb.color) return false;
        }

        if (a.bonusPawnStone.HasValue != b.bonusPawnStone.HasValue) return false;
        if (a.bonusPawnStone.HasValue)
        {
            var aa = a.bonusPawnStone.Value;
            var bb = b.bonusPawnStone.Value;
            if (aa.intersection != bb.intersection) return false;
            if (aa.color != bb.color) return false;
        }

        return true;
    }

    static int GetTurnOrderKey(SimState state, SimTurn turn, SimTurn pv)
    {
        // Larger is searched earlier.
        int key = 0;

        // Prefer the PV move (from last iteration) at root.
        if (!pv.Equals(default) && TurnsEqual(turn, pv))
        {
            // Keep this small so tactical captures still dominate ordering.
            // This avoids pathological behavior under tight time budgets where the PV move
            // is searched (and selected) without ever considering obvious captures.
            key += 50_000;
        }

        // Go / phase-2 moves: prioritize tactical placements (surround wins/blocks, captures).
        if (!turn.chessMove.HasValue)
        {
            key += SimRules.GetGoTurnOrderKey(state, turn);
            return key;
        }
        var mv = turn.chessMove.Value;

        // Captures-first ordering (including king capture).
        var captured = state.squares[mv.to.x, mv.to.y];
        if (captured.HasValue)
        {
            if (captured.Value.type == PieceType.King)
            {
                // Immediate win under current rules.
                key += 9_000_000;
            }
            else
            {
                key += 1_000_000;
                key += PieceValueForOrdering(captured.Value.type);
            }
        }

        return key;
    }

    static int PieceValueForOrdering(PieceType t)
    {
        // Only used for move ordering, not evaluation.
        switch (t)
        {
            case PieceType.Pawn: return 100;
            case PieceType.Knight: return 300;
            case PieceType.Bishop: return 300;
            case PieceType.Rook: return 500;
            case PieceType.Queen: return 900;
            case PieceType.King: return 10000;
            default: return 0;
        }
    }
}
