using System.Collections.Generic;
using UnityEngine;

public static class SimRules
{
    // Set true temporarily when debugging chain move generation.
    public static bool debugLogChainMoves = false;

    // Must dwarf any material score so king-capture dominates evaluation.
    public const int KingCaptureScore = 1_000_000;

    // Optional evaluation term: territory matters only through mobility.
    // Keep weights small so material remains dominant.
    // Superko: a main stone may not recreate a board position already seen this game.
    // Requires SimState.positionHistory to be non-null; toggled off for A/B measurement.
    // No-progress draw: player turns in a row with no capture and no net change to either
    // side's stone count. 50 is one turn each way for 25 rounds; chess's own 50-move rule is
    // 100 plies, so raise this to 100 if you want the closer analogue. Tune with the harness.
    public static int noProgressTurnLimit = 50;

    public static bool superkoEnabled = true;

    // Whether the search also filters superko at interior nodes.
    //
    // Off by default, and that is a deliberate trade. Probing superko costs ~380us per Go node
    // (53 candidates x copy + apply + 64-square surround scan) against ~4us without it, so
    // enabling it inside the search is ~40x slower per Go node. The engine only ever *plays*
    // moves generated at the root, and the root is always filtered, so it can never play an
    // illegal move. All this costs is some accuracy about deep repetition lines - which is
    // exactly the approximation chess engines make for repetition anyway.
    //
    // Turn on to measure the difference; making it affordable means computing the resulting
    // board hash incrementally instead of copy-and-apply.
    public static bool superkoInSearch = false;

    public static bool useMobilityEval = false;
    public static int mobilityWeight = 2; // score per (whiteMoves - blackMoves)

    public static List<SimTurn> GenerateAllLegalFullTurns(SimState state)
        => GenerateAllLegalFullTurns(state, applySuperko: true);

    public static List<SimTurn> GenerateAllLegalFullTurns(SimState state, bool applySuperko)
    {
        var turns = new List<SimTurn>();

        // Phase 2: hybrid Go actions.
        // A full player turn can include:
        //  1) optional forced territory-removal click,
        //  2) optional pawn bonus stone click,
        //  3) mandatory main Go stone placement (which ends the turn).
        // We model these as separate decision nodes so the search can see
        // imminent surround captures (including king captures) without
        // relying on special-case evaluation heuristics.
        if (state != null && !state.phaseOne)
        {
            var color = (state.currentPlayer == PieceColor.White) ? SimStoneColor.White : SimStoneColor.Black;
            var enemy = (color == SimStoneColor.White) ? SimStoneColor.Black : SimStoneColor.White;

            if (state.waitingForTerritoryClick)
            {
                if (!state.lastMovedSquare.HasValue) return turns;
                var sq = state.lastMovedSquare.Value;

                // Corners of square (sx,sy) are intersections:
                // (sx,sy), (sx+1,sy), (sx,sy+1), (sx+1,sy+1)
                var corners = new Vector2Int[]
                {
                    new Vector2Int(sq.x, sq.y),
                    new Vector2Int(sq.x + 1, sq.y),
                    new Vector2Int(sq.x, sq.y + 1),
                    new Vector2Int(sq.x + 1, sq.y + 1)
                };

                foreach (var c in corners)
                {
                    if (c.x < 0 || c.y < 0 || c.x >= state.intersectionSize || c.y >= state.intersectionSize) continue;
                    if (state.stones[c.x, c.y] != enemy) continue;

                    turns.Add(new SimTurn
                    {
                        isInitialBlackStoneTurn = false,
                        chessMove = null,
                        territoryRemoval = new SimTerritoryRemoval { intersection = c },
                        mainStone = null,
                        bonusPawnStone = null
                    });
                }

                return turns;
            }

            if (state.waitingForPawnStoneChoice)
            {
                if (!state.lastMovedSquare.HasValue) return turns;
                var sq = state.lastMovedSquare.Value;
                if (sq.x < 0 || sq.y < 0 || sq.x >= state.boardSize || sq.y >= state.boardSize) return turns;

                var sp = state.squares[sq.x, sq.y];
                if (!sp.HasValue || sp.Value.type != PieceType.Pawn) return turns;

                // Corners of square (sx,sy) are intersections:
                // (sx,sy), (sx+1,sy), (sx,sy+1), (sx+1,sy+1)
                var corners = new Vector2Int[]
                {
                    new Vector2Int(sq.x, sq.y),
                    new Vector2Int(sq.x + 1, sq.y),
                    new Vector2Int(sq.x, sq.y + 1),
                    new Vector2Int(sq.x + 1, sq.y + 1)
                };

                foreach (var c in corners)
                {
                    if (c.x < 0 || c.y < 0 || c.x >= state.intersectionSize || c.y >= state.intersectionSize) continue;
                    if (state.stones[c.x, c.y] != SimStoneColor.None) continue;

                    turns.Add(new SimTurn
                    {
                        isInitialBlackStoneTurn = false,
                        chessMove = null,
                        territoryRemoval = null,
                        mainStone = null,
                        bonusPawnStone = new SimStonePlacement { intersection = c, color = color }
                    });
                }

                return turns;
            }

            // Normal phase-2 main stone placement.
            AddMainStoneTurns(state, color, turns, applySuperko);

            // Superko must never leave a player with nothing to play - that would deadlock the
            // game exactly the way the old no-suicide rule could. If it filtered out everything,
            // allow the repetition.
            if (applySuperko && turns.Count == 0)
            {
                AddMainStoneTurns(state, color, turns, applySuperko: false);
            }

            return turns;
        }

        for (int x = 0; x < state.boardSize; x++)
        {
            for (int y = 0; y < state.boardSize; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;

                var piece = sp.Value;
                if (piece.color != state.currentPlayer) continue;

                var from = new Vector2Int(x, y);

                // NOTE: GenerateExtendedReachMoves already includes standard moves.
                // Avoid generating standard twice unless we're explicitly debugging chain-only destinations.
                List<Vector2Int> finalMoves;

#if UNITY_EDITOR
                if (debugLogChainMoves)
                {
                    var standard = GenerateStandardMovesForPiece(state, from, piece);
                    var standardSet = new HashSet<Vector2Int>(standard);

                    var chainMoves = GenerateExtendedReachMoves(state, from, piece);

                    foreach (var dest in chainMoves)
                    {
                        if (!standardSet.Contains(dest))
                        {
                            Debug.Log($"[SimRules] Chain destination available for {piece.type} {piece.color} from {from} to {dest}");
                        }
                    }

                    // Chain moves already include standard moves, so use them directly.
                    finalMoves = chainMoves;
                }
                else
#endif
                {
                    finalMoves = GenerateExtendedReachMoves(state, from, piece);
                }

                foreach (var to in finalMoves)
                {
                    bool isPawn = piece.type == PieceType.Pawn;
                    int promoteRank = (piece.color == PieceColor.White) ? (state.boardSize - 1) : 0;
                    bool isPromotion = isPawn && to.y == promoteRank;

                    if (isPromotion)
                    {
                        // Create one turn per promotion choice.
                        var promoTypes = new PieceType[]
                        {
                            PieceType.Queen,
                            PieceType.Rook,
                            PieceType.Bishop,
                            PieceType.Knight
                        };

                        foreach (var pt in promoTypes)
                        {
                            var move = new SimChessMove
                            {
                                from = from,
                                to = to,
                                promotion = pt
                            };

                            var turn = new SimTurn
                            {
                                isInitialBlackStoneTurn = false,
                                chessMove = move,
                                territoryRemoval = null,
                                mainStone = null,
                                bonusPawnStone = null
                            };

                            turns.Add(turn);
                        }
                    }
                    else
                    {
                        var move = new SimChessMove
                        {
                            from = from,
                            to = to,
                            promotion = null
                        };

                        var turn = new SimTurn
                        {
                            // FIX: use correct field name from SimTurn (isInitialBlackStoneTurn)
                            isInitialBlackStoneTurn = false,
                            chessMove = move,
                            territoryRemoval = null,
                            mainStone = null,
                            bonusPawnStone = null
                        };

                        turns.Add(turn);
                    }
                }
            }
        }

        return turns;
    }

    /// <summary>
    /// True if playing a main stone here would recreate a board position already seen this game.
    /// For the live game, which resolves Go rules through RulesEngine rather than SimState.
    /// </summary>
    public static bool IsSuperkoViolation(SimState state, int ix, int iy, SimStoneColor color)
    {
        if (!superkoEnabled) return false;
        if (state?.positionHistory == null) return false;
        if (ix < 0 || iy < 0 || ix >= state.intersectionSize || iy >= state.intersectionSize) return false;
        if (state.stones[ix, iy] != SimStoneColor.None) return false;

        return state.positionHistory.Contains(BoardHashAfterMainStone(state, ix, iy, color));
    }

    static void AddMainStoneTurns(SimState state, SimStoneColor color, List<SimTurn> turns, bool applySuperko)
    {
        for (int ix = 0; ix < state.intersectionSize; ix++)
        {
            for (int iy = 0; iy < state.intersectionSize; iy++)
            {
                if (state.stones[ix, iy] != SimStoneColor.None) continue;
                if (!IsLegalGoPlacement(state, ix, iy, color, applySuperko)) continue;

                turns.Add(new SimTurn
                {
                    isInitialBlackStoneTurn = false,
                    chessMove = null,
                    territoryRemoval = null,
                    mainStone = new SimStonePlacement
                    {
                        intersection = new Vector2Int(ix, iy),
                        color = color
                    },
                    bonusPawnStone = null
                });
            }
        }
    }

    public static void ApplyFullTurn(SimState state, SimTurn turn)
    {
        if (turn.isInitialBlackStoneTurn)
        {
            // Not used yet in this chess-only prototype.
            return;
        }

        if (state == null) return;

        // Phase 2 decisions.
        if (!state.phaseOne)
        {
            var playerStone = (state.currentPlayer == PieceColor.White) ? SimStoneColor.White : SimStoneColor.Black;
            var enemyStoneColor = (playerStone == SimStoneColor.White) ? SimStoneColor.Black : SimStoneColor.White;

            if (state.waitingForTerritoryClick)
            {
                if (!turn.territoryRemoval.HasValue) return;

                ApplyTerritoryRemoval(state, turn.territoryRemoval.Value.intersection, enemyStoneColor);

                state.waitingForTerritoryClick = false;

                // After removing a corner stone, a pawn bonus placement may become available.
                // Match live behavior: only if the last moved piece is *currently* a pawn.
                state.pendingPawnCornerOptions?.Clear();
                state.waitingForPawnStoneChoice = false;

                if (state.lastMovedSquare.HasValue)
                {
                    var sq = state.lastMovedSquare.Value;
                    if (sq.x >= 0 && sq.y >= 0 && sq.x < state.boardSize && sq.y < state.boardSize)
                    {
                        var sp = state.squares[sq.x, sq.y];
                        if (sp.HasValue && sp.Value.type == PieceType.Pawn)
                        {
                            FillPawnCornerOptions_Sim(state, sq);
                            if (state.pendingPawnCornerOptions.Count > 0)
                            {
                                state.waitingForPawnStoneChoice = true;
                            }
                        }
                    }
                }

                return;
            }

            if (state.waitingForPawnStoneChoice)
            {
                if (!turn.bonusPawnStone.HasValue) return;

                ApplyGoBonusPawnStone(state, turn.bonusPawnStone.Value);

                state.waitingForPawnStoneChoice = false;
                state.pendingPawnCornerOptions?.Clear();
                return;
            }

            // Normal main stone placement ends the turn.
            if (!turn.mainStone.HasValue) return;
            ApplyGoMainStone(state, turn.mainStone.Value);

            // No-progress rule, measured before the handover so it sees the player who moved.
            UpdateNoProgressCounter(state, state.currentPlayer);

            // End the turn: next player starts in phase one (chess).
            state.currentPlayer = (state.currentPlayer == PieceColor.White)
                ? PieceColor.Black
                : PieceColor.White;

            state.phaseOne = true;
            state.waitingForTerritoryClick = false;
            state.waitingForPawnStoneChoice = false;
            state.pendingPawnCornerOptions?.Clear();

            // Mirror live EndTurn behavior: clear justDoubleStepped on all pawns of the side
            // that is about to move now. This preserves a one-turn en passant window.
            for (int x = 0; x < state.boardSize; x++)
            {
                for (int y = 0; y < state.boardSize; y++)
                {
                    var sp = state.squares[x, y];
                    if (!sp.HasValue) continue;
                    var p = sp.Value;
                    if (p.type == PieceType.Pawn && p.color == state.currentPlayer)
                    {
                        p.justDoubleStepped = false;
                        state.squares[x, y] = p;
                    }
                }
            }

            return;
        }

        if (!turn.chessMove.HasValue)
        {
            // No move encoded.
            return;
        }

        var move = turn.chessMove.Value;
        var movedPiece = state.squares[move.from.x, move.from.y];
        bool movedPawn = movedPiece.HasValue && movedPiece.Value.type == PieceType.Pawn;

        ApplyChessMove(state, move);

        // After a chess move, the same player continues into phase 2 (territory / bonus / main stone).
        state.phaseOne = false;
        state.lastMovedSquare = move.to;

        // Decide if a forced territory removal click is required.
        // It triggers when the destination square is owned by the enemy (>=3 enemy corner stones)
        // and at least one enemy stone exists on a corner.
        state.waitingForTerritoryClick = false;
        state.waitingForPawnStoneChoice = false;
        state.pendingPawnCornerOptions?.Clear();

        var moverStone = (state.currentPlayer == PieceColor.White) ? SimStoneColor.White : SimStoneColor.Black;
        var enemyStone = (moverStone == SimStoneColor.White) ? SimStoneColor.Black : SimStoneColor.White;

        bool inEnemyTerritory = IsSquareOwnTerritoryApprox(state, (state.currentPlayer == PieceColor.White) ? PieceColor.Black : PieceColor.White, move.to);
        if (inEnemyTerritory && HasCornerStone_Sim(state, move.to, enemyStone))
        {
            state.waitingForTerritoryClick = true;
            return;
        }

        // If a pawn moved (and we are not blocked by a territory removal), offer pawn bonus stone.
        // Match live behavior: only if there is at least one empty corner.
        if (movedPawn)
        {
            FillPawnCornerOptions_Sim(state, move.to);
            if (state.pendingPawnCornerOptions.Count > 0)
            {
                state.waitingForPawnStoneChoice = true;
                return;
            }
        }
    }

    public static SimProgressCounts CountProgressMaterial(SimState state)
    {
        var c = new SimProgressCounts { valid = true };

        for (int ix = 0; ix < state.intersectionSize; ix++)
            for (int iy = 0; iy < state.intersectionSize; iy++)
            {
                var st = state.stones[ix, iy];
                if (st == SimStoneColor.White) c.whiteStones++;
                else if (st == SimStoneColor.Black) c.blackStones++;
            }

        for (int x = 0; x < state.boardSize; x++)
            for (int y = 0; y < state.boardSize; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;
                if (sp.Value.color == PieceColor.White) c.whitePieces++;
                else c.blackPieces++;
            }

        return c;
    }

    /// <summary>
    /// Advances the no-progress counter for the player who just finished a turn.
    ///
    /// Progress means a capture of any kind or a net change to either side's stone count.
    /// Piece counts cover chess captures and surround captures; stone counts cover Go captures
    /// and territory removals. Comparing against the same player's *previous* turn end is what
    /// makes the boundary-shuffle case work: over one full cycle each side places one stone and
    /// loses one, so the counts return to where they were.
    /// </summary>
    static void UpdateNoProgressCounter(SimState state, PieceColor justMoved)
    {
        var now = CountProgressMaterial(state);
        var prev = (justMoved == PieceColor.White) ? state.progressAfterWhiteTurn : state.progressAfterBlackTurn;

        if (now.Matches(prev)) state.noProgressTurns++;
        else state.noProgressTurns = 0;

        if (justMoved == PieceColor.White) state.progressAfterWhiteTurn = now;
        else state.progressAfterBlackTurn = now;

        if (noProgressTurnLimit > 0 && state.noProgressTurns >= noProgressTurnLimit && !state.gameOver)
        {
            state.gameOver = true;
            state.winner = null; // draw
        }
    }

    static bool HasCornerStone_Sim(SimState state, Vector2Int square, SimStoneColor color)
    {
        int sx = square.x;
        int sy = square.y;
        if (sx < 0 || sy < 0) return false;
        if (sx + 1 >= state.intersectionSize) return false;
        if (sy + 1 >= state.intersectionSize) return false;

        return state.stones[sx, sy] == color
            || state.stones[sx + 1, sy] == color
            || state.stones[sx, sy + 1] == color
            || state.stones[sx + 1, sy + 1] == color;
    }

    static void FillPawnCornerOptions_Sim(SimState state, Vector2Int pawnSquare)
    {
        state.pendingPawnCornerOptions?.Clear();

        int sx = pawnSquare.x;
        int sy = pawnSquare.y;
        if (sx < 0 || sy < 0) return;
        if (sx + 1 >= state.intersectionSize) return;
        if (sy + 1 >= state.intersectionSize) return;

        void AddIfEmpty(int ix, int iy)
        {
            if (ix < 0 || iy < 0 || ix >= state.intersectionSize || iy >= state.intersectionSize) return;
            if (state.stones[ix, iy] != SimStoneColor.None) return;
            state.pendingPawnCornerOptions.Add(new Vector2Int(ix, iy));
        }

        AddIfEmpty(sx, sy);
        AddIfEmpty(sx + 1, sy);
        AddIfEmpty(sx, sy + 1);
        AddIfEmpty(sx + 1, sy + 1);
    }

    static void ApplyGoMainStone(SimState state, SimStonePlacement placement)
    {
        int ix = placement.intersection.x;
        int iy = placement.intersection.y;
        if (ix < 0 || iy < 0 || ix >= state.intersectionSize || iy >= state.intersectionSize) return;
        if (state.stones[ix, iy] != SimStoneColor.None) return;

        // Place the stone
        state.stones[ix, iy] = placement.color;

        // Resolve captures of adjacent enemy groups
        var captured = new List<Vector2Int>();
        int capturedCount = ResolveCapturesAfterPlacement_Sim(state, ix, iy, captured);

        // Surround capture resolves while the placed stone is still on the board, so a stone
        // that is about to die by suicide can still complete a four-corner surround on its way
        // out. Removing our own stones afterwards can never create a new surround (that needs
        // *enemy* stones), so this only has to run once.
        CheckAllPiecesForSurroundCapture_Sim(state);

        // Suicide is legal. A placement that captures nothing and leaves its own group without
        // liberties takes that whole group off the board.
        bool isSuicide = capturedCount == 0 && !HasLiberties_Sim(state, ix, iy);

        // Update simple-ko point for the next player's normal Go move.
        // Only updated by normal main-stone placements (not bonus pawn stones).
        // NOTE: this must run on every path, including suicide, or a stale ko point survives.
        Vector2Int? nextKo = null;
        if (!isSuicide && capturedCount == 1 && captured.Count == 1)
        {
            var group = GetGroup_Sim(state, ix, iy);
            if (group.Count == 1)
            {
                int libs = CountLiberties_Sim(state, ix, iy);
                if (libs == 1)
                {
                    nextKo = captured[0];
                }
            }
        }
        state.goKoPoint = nextKo;

        if (isSuicide)
        {
            var selfGroup = GetGroup_Sim(state, ix, iy);
            foreach (var p in selfGroup)
            {
                state.stones[p.x, p.y] = SimStoneColor.None;
            }
        }
    }

    // Apply a pawn bonus stone placement.
    // IMPORTANT: ko does NOT apply and ko state is NOT updated by this placement.
    public static void ApplyGoBonusPawnStone(SimState state, SimStonePlacement placement)
    {
        if (state == null) return;

        int ix = placement.intersection.x;
        int iy = placement.intersection.y;
        if (ix < 0 || iy < 0 || ix >= state.intersectionSize || iy >= state.intersectionSize) return;
        if (state.stones[ix, iy] != SimStoneColor.None) return;

        state.stones[ix, iy] = placement.color;

        // Resolve captures of adjacent enemy groups
        var captured = new List<Vector2Int>();
        int capturedCount = ResolveCapturesAfterPlacement_Sim(state, ix, iy, captured);

        // Surround resolves before the stone can die, matching ApplyGoMainStone.
        CheckAllPiecesForSurroundCapture_Sim(state);

        // Suicide is legal; the group simply comes off the board.
        if (capturedCount == 0 && !HasLiberties_Sim(state, ix, iy))
        {
            var selfGroup = GetGroup_Sim(state, ix, iy);
            foreach (var p in selfGroup)
            {
                state.stones[p.x, p.y] = SimStoneColor.None;
            }
        }
    }

    // Apply the forced territory-removal click (remove one enemy stone on a corner of lastMovedSquare).
    // This does not interact with ko.
    public static void ApplyTerritoryRemoval(SimState state, Vector2Int intersection, SimStoneColor expectedEnemy)
    {
        if (state == null) return;
        int ix = intersection.x;
        int iy = intersection.y;
        if (ix < 0 || iy < 0 || ix >= state.intersectionSize || iy >= state.intersectionSize) return;
        if (state.stones[ix, iy] != expectedEnemy) return;

        state.stones[ix, iy] = SimStoneColor.None;

        // After removing a stone, newly-surrounded pieces may now be captured.
        CheckAllPiecesForSurroundCapture_Sim(state);
    }

    static int ResolveCapturesAfterPlacement_Sim(SimState state, int ix, int iy, List<Vector2Int> capturedOut)
    {
        int removed = 0;

        SimStoneColor placed = state.stones[ix, iy];
        if (placed == SimStoneColor.None) return 0;
        SimStoneColor enemy = (placed == SimStoneColor.White) ? SimStoneColor.Black : SimStoneColor.White;

        void TryCapture(int nx, int ny)
        {
            if (nx < 0 || ny < 0 || nx >= state.intersectionSize || ny >= state.intersectionSize) return;
            if (state.stones[nx, ny] != enemy) return;
            if (HasLiberties_Sim(state, nx, ny)) return;

            var group = GetGroup_Sim(state, nx, ny);
            foreach (var p in group)
            {
                if (state.stones[p.x, p.y] == enemy)
                {
                    state.stones[p.x, p.y] = SimStoneColor.None;
                    removed++;
                    capturedOut?.Add(p);
                }
            }
        }

        TryCapture(ix + 1, iy);
        TryCapture(ix - 1, iy);
        TryCapture(ix, iy + 1);
        TryCapture(ix, iy - 1);

        return removed;
    }

    static int CountLiberties_Sim(SimState state, int ix, int iy)
    {
        var c = state.stones[ix, iy];
        if (c == SimStoneColor.None) return 0;

        bool[,] visited = new bool[state.intersectionSize, state.intersectionSize];
        var stack = new Stack<Vector2Int>();
        var liberties = new HashSet<int>();

        stack.Push(new Vector2Int(ix, iy));
        visited[ix, iy] = true;

        int Enc(int x, int y) => (y * 16) + x; // safe for small boards

        while (stack.Count > 0)
        {
            var p = stack.Pop();

            void VisitNeighbor(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= state.intersectionSize || ny >= state.intersectionSize) return;

                var v = state.stones[nx, ny];
                if (v == SimStoneColor.None)
                {
                    liberties.Add(Enc(nx, ny));
                    return;
                }

                if (v != c) return;
                if (visited[nx, ny]) return;
                visited[nx, ny] = true;
                stack.Push(new Vector2Int(nx, ny));
            }

            VisitNeighbor(p.x + 1, p.y);
            VisitNeighbor(p.x - 1, p.y);
            VisitNeighbor(p.x, p.y + 1);
            VisitNeighbor(p.x, p.y - 1);
        }

        return liberties.Count;
    }

    static bool HasLiberties_Sim(SimState state, int ix, int iy)
    {
        var c = state.stones[ix, iy];
        if (c == SimStoneColor.None) return false;

        bool[,] visited = new bool[state.intersectionSize, state.intersectionSize];
        var stack = new Stack<Vector2Int>();
        stack.Push(new Vector2Int(ix, iy));
        visited[ix, iy] = true;

        while (stack.Count > 0)
        {
            var p = stack.Pop();

            void CheckNeighbor(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= state.intersectionSize || ny >= state.intersectionSize) return;
                if (state.stones[nx, ny] == SimStoneColor.None)
                {
                    // Found a liberty.
                    throw new HasLibertyException();
                }
                if (state.stones[nx, ny] != c) return;
                if (visited[nx, ny]) return;
                visited[nx, ny] = true;
                stack.Push(new Vector2Int(nx, ny));
            }

            try
            {
                CheckNeighbor(p.x + 1, p.y);
                CheckNeighbor(p.x - 1, p.y);
                CheckNeighbor(p.x, p.y + 1);
                CheckNeighbor(p.x, p.y - 1);
            }
            catch (HasLibertyException)
            {
                return true;
            }
        }

        return false;
    }

    class HasLibertyException : System.Exception { }

    static List<Vector2Int> GetGroup_Sim(SimState state, int ix, int iy)
    {
        var result = new List<Vector2Int>();
        var c = state.stones[ix, iy];
        if (c == SimStoneColor.None) return result;

        bool[,] visited = new bool[state.intersectionSize, state.intersectionSize];
        var stack = new Stack<Vector2Int>();
        stack.Push(new Vector2Int(ix, iy));
        visited[ix, iy] = true;

        while (stack.Count > 0)
        {
            var p = stack.Pop();
            result.Add(p);

            void TryAdd(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= state.intersectionSize || ny >= state.intersectionSize) return;
                if (visited[nx, ny]) return;
                if (state.stones[nx, ny] != c) return;
                visited[nx, ny] = true;
                stack.Push(new Vector2Int(nx, ny));
            }

            TryAdd(p.x + 1, p.y);
            TryAdd(p.x - 1, p.y);
            TryAdd(p.x, p.y + 1);
            TryAdd(p.x, p.y - 1);
        }

        return result;
    }

    static void CheckAllPiecesForSurroundCapture_Sim(SimState state)
    {
        for (int x = 0; x < state.boardSize; x++)
        {
            for (int y = 0; y < state.boardSize; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;
                var piece = sp.Value;

                SimStoneColor enemy = (piece.color == PieceColor.White) ? SimStoneColor.Black : SimStoneColor.White;

                if (IsSquareSurroundedBy(state, x, y, enemy))
                {
                    // Capture the chess piece.
                    state.squares[x, y] = null;

                    // Keep king caches consistent and mark terminal outcome.
                    if (piece.type == PieceType.King)
                    {
                        if (piece.color == PieceColor.White) state.whiteKingSquare = null;
                        else state.blackKingSquare = null;

                        state.gameOver = true;
                        state.winner = (piece.color == PieceColor.White) ? PieceColor.Black : PieceColor.White;
                    }
                }
            }
        }
    }

    static bool IsSquareSurroundedBy(SimState state, int sx, int sy, SimStoneColor enemy)
    {
        // Corners of square (sx,sy) are intersections:
        // (sx,sy), (sx+1,sy), (sx,sy+1), (sx+1,sy+1)
        if (sx < 0 || sy < 0) return false;
        if (sx + 1 >= state.intersectionSize) return false;
        if (sy + 1 >= state.intersectionSize) return false;

        return state.stones[sx, sy] == enemy &&
               state.stones[sx + 1, sy] == enemy &&
               state.stones[sx, sy + 1] == enemy &&
               state.stones[sx + 1, sy + 1] == enemy;
    }

    static bool IsLegalGoPlacement(SimState state, int ix, int iy, SimStoneColor color, bool applySuperko = true)
    {
        if (ix < 0 || iy < 0 || ix >= state.intersectionSize || iy >= state.intersectionSize) return false;
        if (state.stones[ix, iy] != SimStoneColor.None) return false;

        // Simple-ko: forbid playing on the ko point for normal main-stone placements.
        if (state.goKoPoint.HasValue && state.goKoPoint.Value.x == ix && state.goKoPoint.Value.y == iy)
        {
            return false;
        }

        // Suicide is legal in this game. A self-capturing placement is a real (if usually bad)
        // move: it can complete a four-corner surround on its way off the board, and it
        // guarantees a player always has somewhere to play.
        if (applySuperko && superkoEnabled && state.positionHistory != null)
        {
            if (state.positionHistory.Contains(BoardHashAfterMainStone(state, ix, iy, color)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Board hash of the position this main stone would produce, with the turn handed over.
    /// Plays the move on a throwaway copy: correct first, and cheap enough to keep until the
    /// harness says otherwise.
    /// </summary>
    static ulong BoardHashAfterMainStone(SimState state, int ix, int iy, SimStoneColor color)
    {
        var probe = state.DeepCopy();
        probe.positionHistory = null; // the probe must not consult or mutate the history

        ApplyGoMainStone(probe, new SimStonePlacement
        {
            intersection = new Vector2Int(ix, iy),
            color = color
        });

        // ApplyGoMainStone does not hand over the turn; ApplyFullTurn does. The superko key
        // includes side to move, so the probe has to reflect the handover.
        probe.currentPlayer = (state.currentPlayer == PieceColor.White) ? PieceColor.Black : PieceColor.White;

        return SimZobrist.ComputeBoardHash(probe);
    }

    static bool IsEmptyIntersection(SimState state, int ix, int iy)
    {
        if (ix < 0 || iy < 0 || ix >= state.intersectionSize || iy >= state.intersectionSize) return false;
        return state.stones[ix, iy] == SimStoneColor.None;
    }

    static bool WouldCaptureAdjacentEnemy(SimState state, int ix, int iy, SimStoneColor placedColor)
    {
        var enemy = (placedColor == SimStoneColor.White) ? SimStoneColor.Black : SimStoneColor.White;

        bool TryGroup(int nx, int ny)
        {
            if (nx < 0 || ny < 0 || nx >= state.intersectionSize || ny >= state.intersectionSize) return false;
            if (state.stones[nx, ny] != enemy) return false;

            // If the group has ANY liberty other than the placement point (ix,iy), it survives.
            // If its only liberty is (ix,iy), it will be captured.
            return !HasLibertyExcluding(state, nx, ny, ix, iy);
        }

        return TryGroup(ix + 1, iy) ||
               TryGroup(ix - 1, iy) ||
               TryGroup(ix, iy + 1) ||
               TryGroup(ix, iy - 1);
    }

    static bool HasLibertyExcluding(SimState state, int startX, int startY, int blockedX, int blockedY)
    {
        var c = state.stones[startX, startY];
        if (c == SimStoneColor.None) return false;

        bool[,] visited = new bool[state.intersectionSize, state.intersectionSize];
        var stack = new Stack<Vector2Int>();
        stack.Push(new Vector2Int(startX, startY));
        visited[startX, startY] = true;

        while (stack.Count > 0)
        {
            var p = stack.Pop();

            void VisitNeighbor(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= state.intersectionSize || ny >= state.intersectionSize) return;

                if (state.stones[nx, ny] == SimStoneColor.None)
                {
                    // Liberty, but ignore the placement point itself.
                    if (!(nx == blockedX && ny == blockedY))
                    {
                        throw new HasLibertyException();
                    }
                    return;
                }

                if (state.stones[nx, ny] != c) return;
                if (visited[nx, ny]) return;
                visited[nx, ny] = true;
                stack.Push(new Vector2Int(nx, ny));
            }

            try
            {
                VisitNeighbor(p.x + 1, p.y);
                VisitNeighbor(p.x - 1, p.y);
                VisitNeighbor(p.x, p.y + 1);
                VisitNeighbor(p.x, p.y - 1);
            }
            catch (HasLibertyException)
            {
                return true;
            }
        }

        return false;
    }

    static bool HasLiberties_Work(int size, SimStoneColor[,] stones, int ix, int iy)
    {
        var c = stones[ix, iy];
        if (c == SimStoneColor.None) return false;

        bool[,] visited = new bool[size, size];
        var stack = new Stack<Vector2Int>();
        stack.Push(new Vector2Int(ix, iy));
        visited[ix, iy] = true;

        while (stack.Count > 0)
        {
            var p = stack.Pop();

            bool TryNeighbor(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= size || ny >= size) return false;
                if (stones[nx, ny] == SimStoneColor.None) return true;
                if (stones[nx, ny] != c) return false;
                if (visited[nx, ny]) return false;
                visited[nx, ny] = true;
                stack.Push(new Vector2Int(nx, ny));
                return false;
            }

            if (TryNeighbor(p.x + 1, p.y)) return true;
            if (TryNeighbor(p.x - 1, p.y)) return true;
            if (TryNeighbor(p.x, p.y + 1)) return true;
            if (TryNeighbor(p.x, p.y - 1)) return true;
        }

        return false;
    }

    static List<Vector2Int> GetGroup_Work(int size, SimStoneColor[,] stones, int ix, int iy)
    {
        var result = new List<Vector2Int>();
        var c = stones[ix, iy];
        if (c == SimStoneColor.None) return result;

        bool[,] visited = new bool[size, size];
        var stack = new Stack<Vector2Int>();
        stack.Push(new Vector2Int(ix, iy));
        visited[ix, iy] = true;

        while (stack.Count > 0)
        {
            var p = stack.Pop();
            result.Add(p);

            void TryAdd(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= size || ny >= size) return;
                if (visited[nx, ny]) return;
                if (stones[nx, ny] != c) return;
                visited[nx, ny] = true;
                stack.Push(new Vector2Int(nx, ny));
            }

            TryAdd(p.x + 1, p.y);
            TryAdd(p.x - 1, p.y);
            TryAdd(p.x, p.y + 1);
            TryAdd(p.x, p.y - 1);
        }

        return result;
    }

    public static int Evaluate(SimState state)
    {
        // A drawn game is worth exactly nothing to either side. Without this the search reads
        // a no-progress draw as whatever the material happened to be, and will walk into a
        // drawn position while it is ahead.
        if (state != null && state.gameOver && !state.winner.HasValue) return 0;

        // Terminal: king capture is the only win condition.
        // Return a huge score so search always prioritizes king captures / avoids losing king.
        bool whiteHasKing = HasKing(state, PieceColor.White);
        bool blackHasKing = HasKing(state, PieceColor.Black);

        if (!whiteHasKing && blackHasKing) return -KingCaptureScore;
        if (!blackHasKing && whiteHasKing) return +KingCaptureScore;

        int score = 0; // White-point-of-view

        for (int x = 0; x < state.boardSize; x++)
        {
            for (int y = 0; y < state.boardSize; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;

                var p = sp.Value;
                int val = PieceValue(p.type);
                score += (p.color == PieceColor.White) ? val : -val;
            }
        }

        // Chess positional evaluation.
        // Keep weights small relative to material so the engine stays understandable and stable.
        score += EvaluateChessPosition(state);

        // Go evaluation (holistic): stone economy and tactical stability via liberties.
        // This helps the engine defend against Go-based attacks (captures/atari) rather than
        // only racing to create surround threats around chess pieces.
        score += EvaluateGoPosition(state);

        // Phase-2 relevant tactical term: "surround pressure".
        // Pieces are captured when all 4 corner intersections around their square are enemy stones.
        // Reward creating 1/2/3-corner surrounds on valuable enemy pieces and penalize allowing
        // the same pressure on your own pieces.
        score += EvaluateSurroundPressure(state);

        // Critical tactical term: if a piece is one Go move away from being surrounded (3 enemy corners
        // + 1 empty corner that the opponent can legally place on), treat it as an imminent capture threat.
        // This is rule-driven and applies to all pieces, preventing blunders like leaving the queen on d8
        // when White can complete the final corner next turn.
        score += EvaluateImmediateSurroundCaptureThreats(state);

        if (useMobilityEval)
        {
            int whiteMoves = CountLegalTurnsFor(state, PieceColor.White);
            int blackMoves = CountLegalTurnsFor(state, PieceColor.Black);
            int mobility = whiteMoves - blackMoves;
            score += mobilityWeight * mobility;
        }

        // Later add Go / surround effects
        return score;
    }

    static int EvaluateGoPosition(SimState state)
    {
        if (state == null || state.stones == null) return 0;

        int s = 0; // White POV

        // Stone material: modest weight.
        // Capturing stones becomes intrinsically valuable, which improves defense/offense.
        int white = 0;
        int black = 0;
        for (int ix = 0; ix < state.intersectionSize; ix++)
        {
            for (int iy = 0; iy < state.intersectionSize; iy++)
            {
                var c = state.stones[ix, iy];
                if (c == SimStoneColor.White) white++;
                else if (c == SimStoneColor.Black) black++;
            }
        }
        s += (white - black) * 2;

        // Group liberties / atari pressure.
        // Groups with 1 liberty are in atari and are likely to be captured soon.
        s += EvaluateGoAtariPressure(state);

        return s;
    }

    static int EvaluateGoAtariPressure(SimState state)
    {
        int size = state.intersectionSize;
        bool[,] visited = new bool[size, size];

        int s = 0; // White POV

        for (int ix = 0; ix < size; ix++)
        {
            for (int iy = 0; iy < size; iy++)
            {
                if (visited[ix, iy]) continue;
                var c = state.stones[ix, iy];
                if (c == SimStoneColor.None) continue;

                // BFS group
                var stack = new Stack<Vector2Int>();
                stack.Push(new Vector2Int(ix, iy));
                visited[ix, iy] = true;

                int groupSize = 0;
                var liberties = new HashSet<int>();
                int Enc(int x, int y) => (y * 16) + x;

                while (stack.Count > 0)
                {
                    var p = stack.Pop();
                    groupSize++;

                    void Visit(int nx, int ny)
                    {
                        if (nx < 0 || ny < 0 || nx >= size || ny >= size) return;
                        var v = state.stones[nx, ny];
                        if (v == SimStoneColor.None)
                        {
                            liberties.Add(Enc(nx, ny));
                            return;
                        }
                        if (v != c) return;
                        if (visited[nx, ny]) return;
                        visited[nx, ny] = true;
                        stack.Push(new Vector2Int(nx, ny));
                    }

                    Visit(p.x + 1, p.y);
                    Visit(p.x - 1, p.y);
                    Visit(p.x, p.y + 1);
                    Visit(p.x, p.y - 1);
                }

                int libCount = liberties.Count;

                // Score groups that are close to capture.
                // These are intentionally moderate so they guide play without overriding
                // the primary objective (king capture).
                int pressure;
                if (libCount <= 0) pressure = groupSize * 40;
                else if (libCount == 1) pressure = groupSize * 35;
                else if (libCount == 2) pressure = groupSize * 8;
                else pressure = 0;

                // Atari is special: if a group has exactly one liberty, it is usually immediately
                // capturable next move. Add an extra term so the AI prioritizes saving/capturing
                // these groups instead of ignoring them while racing surround threats.
                int immediateCaptureThreat = 0;
                if (libCount == 1)
                {
                    immediateCaptureThreat = groupSize * 60;
                }

                if (pressure != 0 || immediateCaptureThreat != 0)
                {
                    // If white group is in danger -> negative for White POV.
                    // If black group is in danger -> positive for White POV.
                    int total = pressure + immediateCaptureThreat;
                    if (c == SimStoneColor.White) s -= total;
                    else s += total;
                }
            }
        }

        return s;
    }

    static int EvaluateChessPosition(SimState state)
    {
        if (state == null) return 0;

        int s = 0; // White POV

        // Pawn structure (general, game-long signal)
        s += EvaluatePawnStructure(state);

        // Tactical pressure / piece safety (attack maps)
        s += EvaluatePieceSafetyAndPressure(state);

        // Castling rights / king safety proxy.
        // Even without check rules, losing castling options early tends to correlate with poor king placement.
        s += EvaluateCastlingRights(state);

        // Bishop pair
        int whiteBishops = 0;
        int blackBishops = 0;

        // Basic piece activity / centralization + king placement (no check rules, so keep mild)
        for (int x = 0; x < state.boardSize; x++)
        {
            for (int y = 0; y < state.boardSize; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;
                var p = sp.Value;

                if (p.type == PieceType.Bishop)
                {
                    if (p.color == PieceColor.White) whiteBishops++;
                    else blackBishops++;
                }

                int activity = PieceActivityBonus(p.type, x, y, p.color);
                s += (p.color == PieceColor.White) ? activity : -activity;
            }
        }

        if (whiteBishops >= 2) s += 25;
        if (blackBishops >= 2) s -= 25;

        return s;
    }

    static int EvaluateCastlingRights(SimState state)
    {
        int s = 0; // White POV

        bool whiteCastledLike = state.whiteKingSquare.HasValue && state.whiteKingSquare.Value.y == 0 &&
                                (state.whiteKingSquare.Value.x == 2 || state.whiteKingSquare.Value.x == 6);
        bool blackCastledLike = state.blackKingSquare.HasValue && state.blackKingSquare.Value.y == 7 &&
                                (state.blackKingSquare.Value.x == 2 || state.blackKingSquare.Value.x == 6);

        if (!whiteCastledLike)
        {
            if (!state.whiteCanCastleKingSide) s -= 10;
            if (!state.whiteCanCastleQueenSide) s -= 10;
        }
        if (!blackCastledLike)
        {
            if (!state.blackCanCastleKingSide) s += 10;
            if (!state.blackCanCastleQueenSide) s += 10;
        }

        return s;
    }

    static int EvaluatePieceSafetyAndPressure(SimState state)
    {
        // Build simple attack/defense maps for both sides.
        // This is a general-purpose chess term that encourages both defense (avoid hanging pieces)
        // and offense (create threats on valuable pieces).
        int n = state.boardSize;
        int[,] whiteAttacks = new int[n, n];
        int[,] blackAttacks = new int[n, n];

        FillAttackCounts(state, PieceColor.White, whiteAttacks);
        FillAttackCounts(state, PieceColor.Black, blackAttacks);

        int s = 0; // White POV

        int ThreatValue(PieceType t)
        {
            // Avoid making this term explode for kings; actual king captures are handled as terminal.
            int v = PieceValue(t);
            if (t == PieceType.King) v = 1200;
            return v;
        }

        for (int x = 0; x < n; x++)
        {
            for (int y = 0; y < n; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;
                var p = sp.Value;

                int attackers = (p.color == PieceColor.White) ? blackAttacks[x, y] : whiteAttacks[x, y];
                int defenders = (p.color == PieceColor.White) ? whiteAttacks[x, y] : blackAttacks[x, y];

                if (attackers <= 0) continue;

                int v = ThreatValue(p.type);

                // If a piece is attacked and insufficiently defended, treat as tactical liability.
                // If opponent pieces are in that state, treat as opportunity.
                int diff = attackers - defenders;

                int term = 0;
                if (diff > 0)
                {
                    // En prise / over-attacked.
                    // Cap the multiplier so a pile-up of attacks doesn't dominate evaluation.
                    int capped = Mathf.Min(2, diff);
                    term = (v * (10 + 6 * capped)) / 16; // ~0.6v to ~0.85v

                    // Extra penalty/bonus when completely undefended.
                    if (defenders == 0)
                    {
                        term += v / 4;
                    }
                }
                else
                {
                    // Attacked but defended adequately: small pressure term.
                    term = v / 32;
                }

                if (p.color == PieceColor.White)
                {
                    // White piece under threat is bad for White POV.
                    s -= term;
                }
                else
                {
                    // Black piece under threat is good for White POV.
                    s += term;
                }
            }
        }

        return s;
    }

    static void FillAttackCounts(SimState state, PieceColor attacker, int[,] attacks)
    {
        int n = state.boardSize;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                attacks[i, j] = 0;

        void Add(int x, int y)
        {
            if (x < 0 || y < 0 || x >= n || y >= n) return;
            attacks[x, y]++;
        }

        bool HasPiece(int x, int y)
        {
            if (x < 0 || y < 0 || x >= n || y >= n) return false;
            return state.squares[x, y].HasValue;
        }

        for (int x = 0; x < n; x++)
        {
            for (int y = 0; y < n; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;
                var p = sp.Value;
                if (p.color != attacker) continue;

                switch (p.type)
                {
                    case PieceType.Pawn:
                    {
                        int dy = (attacker == PieceColor.White) ? 1 : -1;
                        Add(x - 1, y + dy);
                        Add(x + 1, y + dy);
                        break;
                    }
                    case PieceType.Knight:
                    {
                        Add(x + 1, y + 2);
                        Add(x - 1, y + 2);
                        Add(x + 1, y - 2);
                        Add(x - 1, y - 2);
                        Add(x + 2, y + 1);
                        Add(x - 2, y + 1);
                        Add(x + 2, y - 1);
                        Add(x - 2, y - 1);
                        break;
                    }
                    case PieceType.Bishop:
                    {
                        AddRay(state, x, y, +1, +1, Add, HasPiece);
                        AddRay(state, x, y, +1, -1, Add, HasPiece);
                        AddRay(state, x, y, -1, +1, Add, HasPiece);
                        AddRay(state, x, y, -1, -1, Add, HasPiece);
                        break;
                    }
                    case PieceType.Rook:
                    {
                        AddRay(state, x, y, +1, 0, Add, HasPiece);
                        AddRay(state, x, y, -1, 0, Add, HasPiece);
                        AddRay(state, x, y, 0, +1, Add, HasPiece);
                        AddRay(state, x, y, 0, -1, Add, HasPiece);
                        break;
                    }
                    case PieceType.Queen:
                    {
                        // Rook + bishop rays
                        AddRay(state, x, y, +1, 0, Add, HasPiece);
                        AddRay(state, x, y, -1, 0, Add, HasPiece);
                        AddRay(state, x, y, 0, +1, Add, HasPiece);
                        AddRay(state, x, y, 0, -1, Add, HasPiece);
                        AddRay(state, x, y, +1, +1, Add, HasPiece);
                        AddRay(state, x, y, +1, -1, Add, HasPiece);
                        AddRay(state, x, y, -1, +1, Add, HasPiece);
                        AddRay(state, x, y, -1, -1, Add, HasPiece);
                        break;
                    }
                    case PieceType.King:
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            Add(x + dx, y + dy);
                        }
                        break;
                    }
                }
            }
        }
    }

    static void AddRay(
        SimState state,
        int fromX,
        int fromY,
        int dx,
        int dy,
        System.Action<int, int> add,
        System.Func<int, int, bool> hasPiece)
    {
        int n = state.boardSize;
        int x = fromX + dx;
        int y = fromY + dy;
        while (x >= 0 && y >= 0 && x < n && y < n)
        {
            add(x, y);
            if (hasPiece(x, y)) break;
            x += dx;
            y += dy;
        }
    }

    static int PieceActivityBonus(PieceType type, int x, int y, PieceColor color)
    {
        // Encourage development/centralization in a generic way.
        // We avoid move-generation-heavy terms here so evaluation stays fast.

        // Distance to the nearest center square among {d4,e4,d5,e5} in 0..7 coords.
        int CenterDist(int px, int py)
        {
            int d1 = Mathf.Abs(px - 3) + Mathf.Abs(py - 3);
            int d2 = Mathf.Abs(px - 4) + Mathf.Abs(py - 3);
            int d3 = Mathf.Abs(px - 3) + Mathf.Abs(py - 4);
            int d4 = Mathf.Abs(px - 4) + Mathf.Abs(py - 4);
            return Mathf.Min(Mathf.Min(d1, d2), Mathf.Min(d3, d4));
        }

        int dist = CenterDist(x, y);
        int centerBonus = Mathf.Max(0, 6 - dist); // 0..6

        switch (type)
        {
            case PieceType.Knight:
                return centerBonus * 6;
            case PieceType.Bishop:
                return centerBonus * 4;
            case PieceType.Rook:
                return centerBonus * 2;
            case PieceType.Queen:
                // Mild: early queen centralization isn't always good.
                return centerBonus * 1;
            case PieceType.Pawn:
                // Reward advancing pawns and occupying central files.
                int advance = (color == PieceColor.White) ? y : (7 - y);
                int fileBonus = (x == 3 || x == 4) ? 2 : 0;
                return advance * 2 + fileBonus;
            case PieceType.King:
                // No check/checkmate rules, so keep this gentle.
                // Slightly prefer kings not wandering early and slightly reward castled-ish squares.
                int startX = 4;
                int startY = (color == PieceColor.White) ? 0 : 7;
                int manhattanFromStart = Mathf.Abs(x - startX) + Mathf.Abs(y - startY);
                int wanderPenalty = manhattanFromStart * 10;

                // Small bonus if king is on c/g files on the back rank (castled proxy).
                bool castledLike = (y == startY) && (x == 2 || x == 6);
                int castleBonus = castledLike ? 30 : 0;
                return castleBonus - wanderPenalty;
            default:
                return 0;
        }
    }

    static int EvaluateImmediateSurroundCaptureThreats(SimState state)
    {
        if (state == null || state.stones == null) return 0;

        int s = 0; // White POV

        int ThreatValue(SimPiece p)
        {
            // If the threatened piece is the king, this is essentially a near-terminal loss.
            if (p.type == PieceType.King) return KingCaptureScore / 3;

            // For other pieces, make it clearly worse than ordinary "3-corner pressure".
            // Losing a queen/rook should dominate most positional considerations.
            return PieceValue(p.type) * 3;
        }

        for (int x = 0; x < state.boardSize; x++)
        {
            for (int y = 0; y < state.boardSize; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;
                var piece = sp.Value;

                SimStoneColor attacker = (piece.color == PieceColor.White) ? SimStoneColor.Black : SimStoneColor.White;

                // Determine the 4 corner intersections.
                int sx = x;
                int sy = y;
                if (sx < 0 || sy < 0) continue;
                if (sx + 1 >= state.intersectionSize) continue;
                if (sy + 1 >= state.intersectionSize) continue;

                int ax = 0;
                int emptyCount = 0;
                Vector2Int emptyCorner = default;
                bool multipleEmpty = false;
                bool hasDefenderCorner = false;
                Vector2Int defenderCorner = default;

                void CheckCorner(int ix, int iy)
                {
                    var c = state.stones[ix, iy];
                    if (c == attacker) ax++;
                    else if (c == SimStoneColor.None)
                    {
                        emptyCount++;
                        if (emptyCount == 1) emptyCorner = new Vector2Int(ix, iy);
                        else multipleEmpty = true;
                    }
                    else
                    {
                        // A non-attacker, non-empty corner stone: potential critical blocker.
                        // In the ax==3 case, this is the only remaining corner.
                        hasDefenderCorner = true;
                        defenderCorner = new Vector2Int(ix, iy);
                    }
                }

                CheckCorner(sx, sy);
                CheckCorner(sx + 1, sy);
                CheckCorner(sx, sy + 1);
                CheckCorner(sx + 1, sy + 1);

                // Exactly 3 attacker corners and exactly 1 empty corner implies an immediate capture
                // if the attacker can legally place on that empty corner.
                if (ax == 3 && emptyCount == 1 && !multipleEmpty)
                {
                    if (IsLegalGoPlacement_ForThreat(state, emptyCorner.x, emptyCorner.y, attacker))
                    {
                        int tv = ThreatValue(piece);
                        if (piece.color == PieceColor.White) s -= tv;
                        else s += tv;
                    }
                }

                // Collapsible defense: 3 attacker corners and the last corner is held by a defender stone.
                // If that defender group is capturable in ONE Go move, then the attacker has a forced
                // sequence "capture blocker" -> "place final corner next turn".
                // Score this as a near-immediate threat (strong, but slightly less than direct completion).
                if (ax == 3 && emptyCount == 0 && hasDefenderCorner)
                {
                    // Ensure the defender corner is actually the defending side's stone.
                    SimStoneColor defender = state.stones[defenderCorner.x, defenderCorner.y];
                    if (defender != SimStoneColor.None && defender != attacker)
                    {
                        if (IsDefenderBlockerCapturableInOne(state, defenderCorner.x, defenderCorner.y, attacker))
                        {
                            int tv = ThreatValue(piece) / 2;
                            if (tv < 1) tv = 1;
                            if (piece.color == PieceColor.White) s -= tv;
                            else s += tv;
                        }
                    }
                }
            }
        }

        return s;
    }

    // Fast Go placement legality check for evaluation/threat detection.
    // Avoids DeepCopy allocations while still being correct for suicide and simple-ko.
    static bool IsLegalGoPlacement_ForThreat(SimState state, int ix, int iy, SimStoneColor color)
    {
        if (ix < 0 || iy < 0 || ix >= state.intersectionSize || iy >= state.intersectionSize) return false;
        if (state.stones[ix, iy] != SimStoneColor.None) return false;

        // Simple ko
        if (state.goKoPoint.HasValue && state.goKoPoint.Value.x == ix && state.goKoPoint.Value.y == iy) return false;

        // Immediate liberty => always legal.
        if (IsEmptyIntersection(state, ix + 1, iy) ||
            IsEmptyIntersection(state, ix - 1, iy) ||
            IsEmptyIntersection(state, ix, iy + 1) ||
            IsEmptyIntersection(state, ix, iy - 1))
        {
            return true;
        }

        SimStoneColor enemy = (color == SimStoneColor.White) ? SimStoneColor.Black : SimStoneColor.White;
        int placeIdx = ix + state.intersectionSize * iy;

        // If we capture an adjacent enemy group by filling its last liberty, placement is legal.
        bool CapturesAdjacentEnemy()
        {
            bool Check(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= state.intersectionSize || ny >= state.intersectionSize) return false;
                if (state.stones[nx, ny] != enemy) return false;
                AnalyzeGroupLiberties(state, nx, ny, out int libs, out bool hasOtherLib, placeIdx);
                return libs == 1 && !hasOtherLib; // only liberty is the placement point
            }

            return Check(ix + 1, iy) || Check(ix - 1, iy) || Check(ix, iy + 1) || Check(ix, iy - 1);
        }

        if (CapturesAdjacentEnemy()) return true;

        // If we connect to a friendly group that has a liberty elsewhere, placement is legal.
        bool ConnectsToFriendlyWithOtherLib()
        {
            bool Check(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= state.intersectionSize || ny >= state.intersectionSize) return false;
                if (state.stones[nx, ny] != color) return false;
                AnalyzeGroupLiberties(state, nx, ny, out int libs, out bool hasOtherLib, placeIdx);
                return libs > 1 || hasOtherLib;
            }

            return Check(ix + 1, iy) || Check(ix - 1, iy) || Check(ix, iy + 1) || Check(ix, iy - 1);
        }

        if (ConnectsToFriendlyWithOtherLib()) return true;

        // Otherwise, this is suicide.
        return false;
    }

    static bool IsDefenderBlockerCapturableInOne(SimState state, int blockerX, int blockerY, SimStoneColor attacker)
    {
        if (blockerX < 0 || blockerY < 0 || blockerX >= state.intersectionSize || blockerY >= state.intersectionSize) return false;
        var defender = state.stones[blockerX, blockerY];
        if (defender == SimStoneColor.None || defender == attacker) return false;

        // If the blocker group has exactly one liberty, and the attacker can legally play on that liberty,
        // then the blocker is capturable in one.
        if (!TryGetSingleLiberty(state, blockerX, blockerY, out var lib)) return false;
        return IsLegalGoPlacement_ForThreat(state, lib.x, lib.y, attacker);
    }

    static bool TryGetSingleLiberty(SimState state, int startX, int startY, out Vector2Int liberty)
    {
        liberty = default;
        int size = state.intersectionSize;
        EnsureGoBuffers(size);

        _goStamp++;
        if (_goStamp == int.MaxValue)
        {
            _goStamp = 1;
            System.Array.Clear(_goVisitStamp, 0, _goVisitStamp.Length);
        }

        _goLibToken++;
        if (_goLibToken == int.MaxValue)
        {
            _goLibToken = 1;
            System.Array.Clear(_goLibStamp, 0, _goLibStamp.Length);
        }

        SimStoneColor c = state.stones[startX, startY];
        if (c == SimStoneColor.None) return false;

        int Enc(int x, int y) => x + size * y;

        int firstLibIdx = -1;
        int libCount = 0;

        int sp = Enc(startX, startY);
        int top = 0;
        _goStack[top++] = sp;
        _goVisitStamp[sp] = _goStamp;

        while (top > 0)
        {
            int idx = _goStack[--top];
            int x = idx % size;
            int y = idx / size;

            void Visit(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= size || ny >= size) return;
                int nidx = Enc(nx, ny);
                var v = state.stones[nx, ny];
                if (v == SimStoneColor.None)
                {
                    if (_goLibStamp[nidx] != _goLibToken)
                    {
                        _goLibStamp[nidx] = _goLibToken;
                        libCount++;
                        if (libCount == 1) firstLibIdx = nidx;
                    }
                    return;
                }
                if (v != c) return;
                if (_goVisitStamp[nidx] == _goStamp) return;
                _goVisitStamp[nidx] = _goStamp;
                _goStack[top++] = nidx;
            }

            Visit(x + 1, y);
            Visit(x - 1, y);
            Visit(x, y + 1);
            Visit(x, y - 1);

            if (libCount > 1)
            {
                // Early out: not a single-liberty group.
                return false;
            }
        }

        if (libCount != 1 || firstLibIdx < 0) return false;
        liberty = new Vector2Int(firstLibIdx % size, firstLibIdx / size);
        return true;
    }

    // Go move ordering key used by the search to find tactically relevant placements early.
    // This is not evaluation; it only prioritizes moves likely to matter (captures, immediate surrounds, blocks).
    public static int GetGoTurnOrderKey(SimState state, SimTurn turn)
    {
        if (state == null) return 0;

        SimStonePlacement? placement = null;
        if (turn.mainStone.HasValue) placement = turn.mainStone;
        else if (turn.bonusPawnStone.HasValue) placement = turn.bonusPawnStone;
        else return 0;

        var p = placement.Value;
        int ix = p.intersection.x;
        int iy = p.intersection.y;
        if (ix < 0 || iy < 0 || ix >= state.intersectionSize || iy >= state.intersectionSize) return 0;

        int key = 0;

        SimStoneColor us = p.color;
        SimStoneColor them = (us == SimStoneColor.White) ? SimStoneColor.Black : SimStoneColor.White;

        // 1) If this placement completes an immediate surround capture of a chess piece, search first.
        // A placement at (ix,iy) is a corner for up to 4 chess squares.
        void ConsiderSquare(int sx, int sy)
        {
            if (sx < 0 || sy < 0 || sx >= state.boardSize || sy >= state.boardSize) return;
            var sp = state.squares[sx, sy];
            if (!sp.HasValue) return;
            var piece = sp.Value;

            // Capture enemy pieces
            if ((piece.color == PieceColor.White && us == SimStoneColor.Black) || (piece.color == PieceColor.Black && us == SimStoneColor.White))
            {
                // Count our stones on the other 3 corners.
                int corners = 0;
                if (state.stones[sx, sy] == us || (sx == ix && sy == iy)) corners++;
                if (state.stones[sx + 1, sy] == us || (sx + 1 == ix && sy == iy)) corners++;
                if (state.stones[sx, sy + 1] == us || (sx == ix && sy + 1 == iy)) corners++;
                if (state.stones[sx + 1, sy + 1] == us || (sx + 1 == ix && sy + 1 == iy)) corners++;

                if (corners == 4)
                {
                    // King surround is an immediate win.
                    if (piece.type == PieceType.King) key += 8_000_000;
                    else key += 500_000 + PieceValue(piece.type) * 10;
                }
            }
            else
            {
                // Defensive block: if this placement prevents an immediate surround completion
                // on our own piece (we are filling the last empty corner).
                SimStoneColor attacker = them;
                int attackerCorners = 0;
                int empties = 0;

                void Check(int cx, int cy)
                {
                    var c = state.stones[cx, cy];
                    if (c == attacker) attackerCorners++;
                    else if (c == SimStoneColor.None)
                    {
                        // Only count empties that are not the placement point.
                        if (cx != ix || cy != iy) empties++;
                    }
                }

                if (sx + 1 >= state.intersectionSize || sy + 1 >= state.intersectionSize) return;
                Check(sx, sy);
                Check(sx + 1, sy);
                Check(sx, sy + 1);
                Check(sx + 1, sy + 1);

                // If there are 3 attacker corners and the only missing corner is (ix,iy), then this is a critical block.
                if (attackerCorners == 3 && empties == 0)
                {
                    // Strongly prioritize defending kings, but applies to all pieces.
                    if (piece.type == PieceType.King) key += 4_000_000;
                    else key += 300_000 + PieceValue(piece.type) * 6;
                }
            }
        }

        ConsiderSquare(ix, iy);
        ConsiderSquare(ix - 1, iy);
        ConsiderSquare(ix, iy - 1);
        ConsiderSquare(ix - 1, iy - 1);

        // 2) Captures: if this move captures any adjacent enemy group, prioritize.
        // Cheap check: if any adjacent enemy group is in atari with only liberty at (ix,iy).
        bool CapturesSomething()
        {
            bool Check(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= state.intersectionSize || ny >= state.intersectionSize) return false;
                if (state.stones[nx, ny] != them) return false;
                if (!TryGetSingleLiberty(state, nx, ny, out var lib)) return false;
                return lib.x == ix && lib.y == iy;
            }

            return Check(ix + 1, iy) || Check(ix - 1, iy) || Check(ix, iy + 1) || Check(ix, iy - 1);
        }

        if (CapturesSomething())
        {
            key += 250_000;
        }

        return key;
    }

    // Group liberty analysis without allocations.
    // Outputs:
    //  - libs: number of unique liberties
    //  - hasLibertyOtherThanTarget: true if there exists a liberty that is NOT targetLibIdx
    static int[] _goVisitStamp;
    static int[] _goLibStamp;
    static int[] _goStack;
    static int _goStamp;
    static int _goLibToken;

    static void EnsureGoBuffers(int size)
    {
        int cap = size * size;
        if (_goVisitStamp == null || _goVisitStamp.Length < cap) _goVisitStamp = new int[cap];
        if (_goLibStamp == null || _goLibStamp.Length < cap) _goLibStamp = new int[cap];
        if (_goStack == null || _goStack.Length < cap) _goStack = new int[cap];
    }

    static void AnalyzeGroupLiberties(SimState state, int startX, int startY, out int libs, out bool hasLibertyOtherThanTarget, int targetLibIdx)
    {
        int size = state.intersectionSize;
        EnsureGoBuffers(size);

        _goStamp++;
        if (_goStamp == int.MaxValue)
        {
            // reset stamps to avoid overflow issues
            _goStamp = 1;
            System.Array.Clear(_goVisitStamp, 0, _goVisitStamp.Length);
        }

        _goLibToken++;
        if (_goLibToken == int.MaxValue)
        {
            _goLibToken = 1;
            System.Array.Clear(_goLibStamp, 0, _goLibStamp.Length);
        }

        SimStoneColor c = state.stones[startX, startY];
        int localLibs = 0;
        bool localHasOther = false;

        int Enc(int x, int y) => x + size * y;

        int sp = Enc(startX, startY);
        int top = 0;
        _goStack[top++] = sp;
        _goVisitStamp[sp] = _goStamp;

        while (top > 0)
        {
            int idx = _goStack[--top];
            int x = idx % size;
            int y = idx / size;

            void Visit(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= size || ny >= size) return;
                int nidx = Enc(nx, ny);
                var v = state.stones[nx, ny];
                if (v == SimStoneColor.None)
                {
                    if (_goLibStamp[nidx] != _goLibToken)
                    {
                        _goLibStamp[nidx] = _goLibToken;
                        localLibs++;
                        if (nidx != targetLibIdx) localHasOther = true;
                    }
                    return;
                }
                if (v != c) return;
                if (_goVisitStamp[nidx] == _goStamp) return;
                _goVisitStamp[nidx] = _goStamp;
                _goStack[top++] = nidx;
            }

            Visit(x + 1, y);
            Visit(x - 1, y);
            Visit(x, y + 1);
            Visit(x, y - 1);
        }

        libs = localLibs;
        hasLibertyOtherThanTarget = localHasOther;
    }

    static int EvaluatePawnStructure(SimState state)
    {
        // Simple, robust pawn structure evaluation:
        // - doubled pawns penalty
        // - isolated pawns penalty
        // - passed pawns bonus (scaled by advancement)

        int s = 0; // White POV

        int[] whiteFileCount = new int[8];
        int[] blackFileCount = new int[8];

        bool[,] whitePawn = new bool[8, 8];
        bool[,] blackPawn = new bool[8, 8];

        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;
                var p = sp.Value;
                if (p.type != PieceType.Pawn) continue;

                if (p.color == PieceColor.White)
                {
                    whiteFileCount[x]++;
                    whitePawn[x, y] = true;
                }
                else
                {
                    blackFileCount[x]++;
                    blackPawn[x, y] = true;
                }
            }
        }

        // Doubled pawn penalties
        for (int f = 0; f < 8; f++)
        {
            if (whiteFileCount[f] > 1) s -= 12 * (whiteFileCount[f] - 1);
            if (blackFileCount[f] > 1) s += 12 * (blackFileCount[f] - 1);
        }

        bool HasNeighborPawn(bool[,] pawns, int file)
        {
            if (file < 0 || file >= 8) return false;
            for (int y = 0; y < 8; y++)
            {
                if (pawns[file, y]) return true;
            }
            return false;
        }

        // Isolated + passed pawns (iterate actual pawn squares)
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 8; y++)
            {
                if (whitePawn[x, y])
                {
                    bool isolated = !HasNeighborPawn(whitePawn, x - 1) && !HasNeighborPawn(whitePawn, x + 1);
                    if (isolated) s -= 10;

                    bool passed = true;
                    for (int fx = Mathf.Max(0, x - 1); fx <= Mathf.Min(7, x + 1); fx++)
                    {
                        for (int by = y + 1; by < 8; by++)
                        {
                            if (blackPawn[fx, by]) { passed = false; goto DoneWhite; }
                        }
                    }
                DoneWhite:
                    if (passed)
                    {
                        int advance = y;
                        s += 8 + advance * 3;
                    }
                }

                if (blackPawn[x, y])
                {
                    bool isolated = !HasNeighborPawn(blackPawn, x - 1) && !HasNeighborPawn(blackPawn, x + 1);
                    if (isolated) s += 10;

                    bool passed = true;
                    for (int fx = Mathf.Max(0, x - 1); fx <= Mathf.Min(7, x + 1); fx++)
                    {
                        for (int wy = y - 1; wy >= 0; wy--)
                        {
                            if (whitePawn[fx, wy]) { passed = false; goto DoneBlack; }
                        }
                    }
                DoneBlack:
                    if (passed)
                    {
                        int advance = 7 - y;
                        s -= 8 + advance * 3;
                    }
                }
            }
        }

        return s;
    }

    static int EvaluateSurroundPressure(SimState state)
    {
        if (state == null || state.stones == null) return 0;

        int s = 0; // White POV

        int PressurePieceValue(PieceType t)
        {
            // IMPORTANT: This value is only for non-terminal surround *pressure* evaluation.
            // Actual king loss is handled by terminal king capture + immediate surround-capture detection.
            // Using PieceValue(King)=10000 here causes huge distortions (king running early).
            switch (t)
            {
                case PieceType.King: return 1200;
                default: return PieceValue(t);
            }
        }

        int DefenseValue(int pieceValue, int friendlyCorners, int enemyCorners)
        {
            // Defensive stones matter most when the piece is actually under corner pressure.
            // Make this scale with both friendly coverage and enemy pressure.
            if (enemyCorners <= 0 || friendlyCorners <= 0) return 0;

            // Base per-friendly-corner value.
            int baseV = pieceValue / 60;
            if (baseV < 1) baseV = 1;

            // Scale by enemyCorners so defending against 2-3 attacker corners is strongly preferred.
            int scaled = baseV * friendlyCorners * enemyCorners;

            // Cap so it doesn't dominate material.
            int cap = pieceValue / 2;
            if (scaled > cap) scaled = cap;
            return scaled;
        }

        // For each piece, count attacker stones on its 4 corners.
        // If a piece is Black, White stones are the attackers (positive).
        // If a piece is White, Black stones are the attackers (negative).
        for (int x = 0; x < state.boardSize; x++)
        {
            for (int y = 0; y < state.boardSize; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;

                var p = sp.Value;
                int val = PressurePieceValue(p.type);

                if (p.color == PieceColor.Black)
                {
                    int enemyCorners = CountCornerStones(state, x, y, SimStoneColor.White);
                    int friendlyCorners = CountCornerStones(state, x, y, SimStoneColor.Black);
                    s += SurroundValue(val, enemyCorners);
                    s -= DefenseValue(val, friendlyCorners, enemyCorners);
                }
                else
                {
                    int enemyCorners = CountCornerStones(state, x, y, SimStoneColor.Black);
                    int friendlyCorners = CountCornerStones(state, x, y, SimStoneColor.White);
                    s -= SurroundValue(val, enemyCorners);
                    s += DefenseValue(val, friendlyCorners, enemyCorners);
                }
            }
        }

        return s;
    }

    static int CountCornerStones(SimState state, int sx, int sy, SimStoneColor attacker)
    {
        // Corners of square (sx,sy) are intersections:
        // (sx,sy), (sx+1,sy), (sx,sy+1), (sx+1,sy+1)
        if (sx < 0 || sy < 0) return 0;
        if (sx + 1 >= state.intersectionSize) return 0;
        if (sy + 1 >= state.intersectionSize) return 0;

        int n = 0;
        if (state.stones[sx, sy] == attacker) n++;
        if (state.stones[sx + 1, sy] == attacker) n++;
        if (state.stones[sx, sy + 1] == attacker) n++;
        if (state.stones[sx + 1, sy + 1] == attacker) n++;
        return n;
    }

    static int SurroundValue(int pieceValue, int attackerCorners)
    {
        // Nonlinear pressure: 3 corners is a near-capture threat, so it's valued heavily.
        switch (attackerCorners)
        {
            case 0: return 0;
            case 1: return pieceValue / 40; // small
            case 2: return pieceValue / 12; // moderate
            case 3: return pieceValue / 3;  // strong threat
            default:
                // 4-corner should have already removed the piece during ApplyGoMainStone,
                // but keep a bounded value if it ever occurs.
                return pieceValue;
        }
    }

    public static int EvaluateForSideToMove(SimState state)
    {
        int whitePOV = Evaluate(state);
        return state.currentPlayer == PieceColor.White ? whitePOV : -whitePOV;
    }

    static int PieceValue(PieceType t)
    {
        switch (t)
        {
            case PieceType.Pawn:   return 100;
            case PieceType.Knight: return 300;
            case PieceType.Bishop: return 300;
            case PieceType.Rook:   return 500;
            case PieceType.Queen:  return 900;
            case PieceType.King:   return 10000;
            default:               return 0;
        }
    }

    static void ApplyChessMove(SimState state, SimChessMove move)
    {
        var from = move.from;
        var to   = move.to;

        var sp = state.squares[from.x, from.y];
        if (!sp.HasValue) return;

        var piece = sp.Value;

        // EN PASSANT: pawn moves diagonally to an empty square capturing a pawn that just double-stepped.
        if (piece.type == PieceType.Pawn)
        {
            bool toOccupied = state.squares[to.x, to.y].HasValue;
            if (!toOccupied && to.x != from.x)
            {
                int dir = piece.color == PieceColor.White ? 1 : -1;
                var enemySq = new Vector2Int(to.x, to.y - dir);
                if (enemySq.x >= 0 && enemySq.y >= 0 && enemySq.x < state.boardSize && enemySq.y < state.boardSize)
                {
                    var epPawn = state.squares[enemySq.x, enemySq.y];
                    if (epPawn.HasValue && epPawn.Value.type == PieceType.Pawn && epPawn.Value.color != piece.color && epPawn.Value.justDoubleStepped)
                    {
                        state.squares[enemySq.x, enemySq.y] = null;
                    }
                }
            }
        }

        // Detect capture (including king capture) before overwriting destination.
        var captured = state.squares[to.x, to.y];
        if (captured.HasValue && captured.Value.type == PieceType.King)
        {
            // King capture ends the game.
            state.gameOver = true;
            state.winner = piece.color;

            if (captured.Value.color == PieceColor.White) state.whiteKingSquare = null;
            else state.blackKingSquare = null;
        }

        // CASTLING: king moves two squares horizontally, slide rook
        if (piece.type == PieceType.King && Mathf.Abs(to.x - from.x) == 2 && to.y == from.y)
        {
            int dir = to.x > from.x ? 1 : -1;
            int rookFromX = dir == 1 ? 7 : 0;
            int rookToX = from.x + dir;

            int y = from.y;
            if (rookFromX >= 0 && rookFromX < state.boardSize)
            {
                var rookSq = state.squares[rookFromX, y];
                if (rookSq.HasValue)
                {
                    var rook = rookSq.Value;
                    if (rook.type == PieceType.Rook && rook.color == piece.color)
                    {
                        state.squares[rookFromX, y] = null;
                        rook.hasMoved = true;
                        rook.justDoubleStepped = false;
                        state.squares[rookToX, y] = rook;
                    }
                }
            }
        }

        // Clear origin
        state.squares[from.x, from.y] = null;

        // Normal capture / overwrite destination
        state.squares[to.x, to.y] = piece;

        // Update king-square caches
        if (piece.type == PieceType.King)
        {
            if (piece.color == PieceColor.White)
                state.whiteKingSquare = to;
            else
                state.blackKingSquare = to;
        }

        // Update castling rights flags (used by hashing; movegen can also consult them if desired).
        // Under your simplified rules, castling is allowed iff king/rook haven't moved and path is empty.
        if (piece.type == PieceType.King)
        {
            if (piece.color == PieceColor.White)
            {
                state.whiteCanCastleKingSide = false;
                state.whiteCanCastleQueenSide = false;
            }
            else
            {
                state.blackCanCastleKingSide = false;
                state.blackCanCastleQueenSide = false;
            }
        }
        if (piece.type == PieceType.Rook)
        {
            // Rook moved from its starting file removes that side's corresponding castling.
            if (piece.color == PieceColor.White)
            {
                if (from.x == 0 && from.y == 0) state.whiteCanCastleQueenSide = false;
                if (from.x == 7 && from.y == 0) state.whiteCanCastleKingSide = false;
            }
            else
            {
                if (from.x == 0 && from.y == 7) state.blackCanCastleQueenSide = false;
                if (from.x == 7 && from.y == 7) state.blackCanCastleKingSide = false;
            }
        }

        // Mark as moved
        piece.hasMoved = true;

        // Track pawn double-step (useful for future en passant support).
        if (piece.type == PieceType.Pawn)
        {
            piece.justDoubleStepped = Mathf.Abs(to.y - from.y) == 2;

            // Track en passant availability as the pawn that just double-stepped.
            // (If it didn't double-step, clear.)
            state.enPassantPawnSquare = piece.justDoubleStepped ? to : (Vector2Int?)null;
        }
        else
        {
            piece.justDoubleStepped = false;
            state.enPassantPawnSquare = null;
        }

        // PROMOTION: if a pawn reaches the last rank, promote to the specified piece type.
        // If promotion is missing (shouldn't happen for generated moves), default to Queen.
        if (piece.type == PieceType.Pawn)
        {
            int promoteRank = (piece.color == PieceColor.White) ? (state.boardSize - 1) : 0;
            if (to.y == promoteRank)
            {
                piece.type = move.promotion ?? PieceType.Queen;
                piece.justDoubleStepped = false;
                state.enPassantPawnSquare = null;
            }
        }

        state.squares[to.x, to.y] = piece;
    }

    static bool HasKing(SimState state, PieceColor color)
    {
        // Validate cached king squares if present.
        // (Kings can also be removed by 4-corner surround capture during Go steps.)
        if (color == PieceColor.White && state.whiteKingSquare.HasValue)
        {
            var sq = state.whiteKingSquare.Value;
            if (sq.x >= 0 && sq.y >= 0 && sq.x < state.boardSize && sq.y < state.boardSize)
            {
                var sp = state.squares[sq.x, sq.y];
                if (sp.HasValue && sp.Value.type == PieceType.King && sp.Value.color == PieceColor.White)
                    return true;
            }
            state.whiteKingSquare = null;
        }
        if (color == PieceColor.Black && state.blackKingSquare.HasValue)
        {
            var sq = state.blackKingSquare.Value;
            if (sq.x >= 0 && sq.y >= 0 && sq.x < state.boardSize && sq.y < state.boardSize)
            {
                var sp = state.squares[sq.x, sq.y];
                if (sp.HasValue && sp.Value.type == PieceType.King && sp.Value.color == PieceColor.Black)
                    return true;
            }
            state.blackKingSquare = null;
        }

        // Fallback: scan board (small, safe, robust).
        for (int x = 0; x < state.boardSize; x++)
        {
            for (int y = 0; y < state.boardSize; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;
                var p = sp.Value;
                if (p.type == PieceType.King && p.color == color) return true;
            }
        }
        return false;
    }

    static int CountLegalTurnsFor(SimState state, PieceColor color)
    {
        // Count legal phase-one turns for the given side on this position.
        // Uses the same move generation as search, but avoids allocating a full List<SimTurn>.
        var prev = state.currentPlayer;
        state.currentPlayer = color;

        int count = 0;
        for (int x = 0; x < state.boardSize; x++)
        {
            for (int y = 0; y < state.boardSize; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;

                var piece = sp.Value;
                if (piece.color != state.currentPlayer) continue;

                var from = new Vector2Int(x, y);
                // Includes standard + chain
                var moves = GenerateExtendedReachMoves(state, from, piece);
                count += moves.Count;
            }
        }

        state.currentPlayer = prev;
        return count;
    }

    // --- Territorial chain movement on SimState ---
    static List<Vector2Int> GenerateExtendedReachMoves(SimState state, Vector2Int from, SimPiece piece)
    {
        var result = new HashSet<Vector2Int>();

        // Baseline: standard moves are always allowed
        var standardMoves = GenerateStandardMovesForPiece(state, from, piece);
        foreach (var m in standardMoves) result.Add(m);

        var frontier = new Queue<(Vector2Int from, bool fromIsTerritory)>();
        var visited = new HashSet<Vector2Int>();
        frontier.Enqueue((from, false));
        visited.Add(from);

        bool firstLayer = true;

        while (frontier.Count > 0)
        {
            int layerCount = frontier.Count;

            for (int i = 0; i < layerCount; i++)
            {
                var node = frontier.Dequeue();
                var cur = node.from;
                bool fromIsTerritory = node.fromIsTerritory;

                List<Vector2Int> stepMoves;
                if (firstLayer)
                {
                    stepMoves = GenerateFirstHopChainSteps(state, cur, piece);
                }
                else
                {
                    if (!fromIsTerritory)
                        continue;

                    stepMoves = GenerateNonCaptureTerritoryMoves(state, cur, piece);
                }

                foreach (var to in stepMoves)
                {
                    if (!visited.Add(to))
                        continue;

                    // FIX: pass piece.color (PieceColor), not the SimPiece itself
                    bool toIsTerritory = IsSquareOwnTerritoryApprox(state, piece.color, to);
                    if (!toIsTerritory)
                        continue;

                    frontier.Enqueue((to, toIsTerritory));
                    result.Add(to);
                }
            }

            firstLayer = false;
        }

        result.Remove(from);
        return new List<Vector2Int>(result);
    }

    static List<Vector2Int> GenerateFirstHopChainSteps(SimState state, Vector2Int from, SimPiece piece)
    {
        var steps = new List<Vector2Int>();
        int x = from.x, y = from.y;
        int bs = state.boardSize;

        bool InBounds(int a, int b) => a >= 0 && b >= 0 && a < bs && b < bs;
        bool IsEmpty(int a, int b) => !state.squares[a, b].HasValue;

        void TryAddRay(int dx, int dy)
        {
            int nx = x + dx, ny = y + dy;
            while (InBounds(nx, ny) && IsEmpty(nx, ny))
            {
                steps.Add(new Vector2Int(nx, ny));
                nx += dx; ny += dy;
            }
        }

        void TryAddKnight(int nx, int ny)
        {
            if (!InBounds(nx, ny)) return;
            if (!IsEmpty(nx, ny)) return;
            steps.Add(new Vector2Int(nx, ny));
        }

        switch (piece.type)
        {
            case PieceType.Pawn:
                int dir = piece.color == PieceColor.White ? 1 : -1;
                if (InBounds(x, y + dir) && IsEmpty(x, y + dir)) steps.Add(new Vector2Int(x, y + dir));
                break;
            case PieceType.Rook:
                TryAddRay(1, 0); TryAddRay(-1, 0); TryAddRay(0, 1); TryAddRay(0, -1);
                break;
            case PieceType.Bishop:
                TryAddRay(1, 1); TryAddRay(-1, 1); TryAddRay(1, -1); TryAddRay(-1, -1);
                break;
            case PieceType.Queen:
                TryAddRay(1, 0); TryAddRay(-1, 0); TryAddRay(0, 1); TryAddRay(0, -1);
                TryAddRay(1, 1); TryAddRay(-1, 1); TryAddRay(1, -1); TryAddRay(-1, -1);
                break;
            case PieceType.Knight:
                int[,] deltas = new int[,] { { 1, 2 }, { 2, 1 }, { -1, 2 }, { -2, 1 }, { 1, -2 }, { 2, -1 }, { -1, -2 }, { -2, -1 } };
                for (int i = 0; i < 8; i++)
                    TryAddKnight(x + deltas[i, 0], y + deltas[i, 1]);
                break;
            case PieceType.King:
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (InBounds(nx, ny) && IsEmpty(nx, ny)) steps.Add(new Vector2Int(nx, ny));
                    }
                break;
        }

        return steps;
    }

    static List<Vector2Int> GenerateNonCaptureTerritoryMoves(SimState state, Vector2Int from, SimPiece piece)
    {
        var steps = new List<Vector2Int>();
        int x = from.x, y = from.y;
        int bs = state.boardSize;

        bool InBounds(int a, int b) => a >= 0 && b >= 0 && a < bs && b < bs;
        bool IsEmpty(int a, int b) => !state.squares[a, b].HasValue;

        bool IsOwnTerritory(int a, int b)
        {
            if (!InBounds(a, b)) return false;
            // FIX: pass piece.color (PieceColor) instead of SimPiece
            return IsSquareOwnTerritoryApprox(state, piece.color, new Vector2Int(a, b));
        }

        void TryAddSlide(int dx, int dy)
        {
            int nx = x + dx, ny = y + dy;
            while (InBounds(nx, ny) && IsEmpty(nx, ny) && IsOwnTerritory(nx, ny))
            {
                int cx = x + dx, cy = y + dy;
                bool allTerritory = true;
                while (cx != nx || cy != ny)
                {
                    if (!IsOwnTerritory(cx, cy)) { allTerritory = false; break; }
                    cx += dx; cy += dy;
                }
                if (!allTerritory) break;

                steps.Add(new Vector2Int(nx, ny));

                nx += dx; ny += dy;
            }
        }

        void TryAddKnight(int nx, int ny)
        {
            if (!InBounds(nx, ny)) return;
            if (!IsEmpty(nx, ny)) return;
            if (!IsOwnTerritory(nx, ny)) return;
            steps.Add(new Vector2Int(nx, ny));
        }

        switch (piece.type)
        {
            case PieceType.Pawn:
                int dir = piece.color == PieceColor.White ? 1 : -1;
                int ny = y + dir;
                if (IsOwnTerritory(x, y) && IsOwnTerritory(x, ny) && InBounds(x, ny) && IsEmpty(x, ny))
                    steps.Add(new Vector2Int(x, ny));
                break;

            case PieceType.Rook:
                TryAddSlide(1, 0); TryAddSlide(-1, 0); TryAddSlide(0, 1); TryAddSlide(0, -1);
                break;

            case PieceType.Bishop:
                TryAddSlide(1, 1); TryAddSlide(-1, 1); TryAddSlide(1, -1); TryAddSlide(-1, -1);
                break;

            case PieceType.Queen:
                TryAddSlide(1, 1); TryAddSlide(-1, 1); TryAddSlide(1, -1); TryAddSlide(-1, -1);
                break;

            case PieceType.Knight:
                int[,] deltas = new int[,] { { 1, 2 }, { 2, 1 }, { -1, 2 }, { -2, 1 }, { 1, -2 }, { 2, -1 }, { -1, -2 }, { -2, -1 } };
                for (int i = 0; i < 8; i++)
                {
                    int nx = x + deltas[i, 0];
                    int ny2 = y + deltas[i, 1];
                    TryAddKnight(nx, ny2);
                }
                break;

            case PieceType.King:
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, nyKing = y + dy;
                        if (!InBounds(nx, nyKing)) continue;
                        if (!IsEmpty(nx, nyKing)) continue;
                        if (!IsOwnTerritory(nx, nyKing)) continue;
                        steps.Add(new Vector2Int(nx, nyKing));
                    }
                break;
        }

        return steps;
    }

    // Territory rule: a square belongs to a player if that player has stones
    // on at least three of the intersections immediately surrounding that square.
    // Square (x,y) is surrounded by intersections:
    // (x,   y  ), (x+1, y  ), (x,   y+1), (x+1, y+1)
    static bool IsSquareOwnTerritoryApprox(SimState state, PieceColor color, Vector2Int square)
    {
        int x = square.x;
        int y = square.y;

        // Check bounds for intersections around this square
        if (x < 0 || y < 0) return false;
        if (x + 1 >= state.intersectionSize) return false;
        if (y + 1 >= state.intersectionSize) return false;

        int count = 0;

        // Map PieceColor -> SimStoneColor
        SimStoneColor ownStone = (color == PieceColor.White) ? SimStoneColor.White : SimStoneColor.Black;

        if (state.stones[x, y] == ownStone) count++;
        if (state.stones[x + 1, y] == ownStone) count++;
        if (state.stones[x, y + 1] == ownStone) count++;
        if (state.stones[x + 1, y + 1] == ownStone) count++;

        return count >= 3;
    }

    // Standard chess move generator used both as baseline and by extended reach.
    static List<Vector2Int> GenerateStandardMovesForPiece(SimState state, Vector2Int from, SimPiece piece)
    {
        var moves = new List<Vector2Int>();
        int x = from.x;
        int y = from.y;
        int bs = state.boardSize;
        var color = piece.color;

        bool InBounds(int a, int b) => a >= 0 && b >= 0 && a < bs && b < bs;
        bool IsEmpty(int a, int b) => !state.squares[a, b].HasValue;
        bool IsEnemy(int a, int b)
        {
            var sq = state.squares[a, b];
            return sq.HasValue && sq.Value.color != color;
        }

        switch (piece.type)
        {
            case PieceType.Pawn:
                {
                    int dir = color == PieceColor.White ? 1 : -1;
                    int ny1 = y + dir;
                    if (InBounds(x, ny1) && IsEmpty(x, ny1))
                        moves.Add(new Vector2Int(x, ny1));

                    // Initial double-step (match live chess rules).
                    int startRow = (color == PieceColor.White) ? 1 : 6;
                    int ny2 = y + 2 * dir;
                    if (!piece.hasMoved && y == startRow && InBounds(x, ny2) && IsEmpty(x, ny1) && IsEmpty(x, ny2))
                        moves.Add(new Vector2Int(x, ny2));

                    int nx1 = x + 1, nyCap = y + dir;
                    int nx2 = x - 1;
                    if (InBounds(nx1, nyCap) && IsEnemy(nx1, nyCap))
                        moves.Add(new Vector2Int(nx1, nyCap));
                    if (InBounds(nx2, nyCap) && IsEnemy(nx2, nyCap))
                        moves.Add(new Vector2Int(nx2, nyCap));

                    // EN PASSANT (match live rules): capture an adjacent pawn that just double-stepped.
                    void TryEnPassant(int sideX)
                    {
                        int ex = x + sideX;
                        int ey = y;
                        int ty = y + dir;

                        if (!InBounds(ex, ey) || !InBounds(ex, ty)) return;
                        var adj = state.squares[ex, ey];
                        if (!adj.HasValue) return;
                        var p = adj.Value;
                        if (p.type != PieceType.Pawn || p.color == color) return;
                        if (!p.justDoubleStepped) return;

                        // Target square must be empty (en passant is a capture onto empty square).
                        if (!IsEmpty(ex, ty)) return;

                        moves.Add(new Vector2Int(ex, ty));
                    }

                    TryEnPassant(1);
                    TryEnPassant(-1);
                }
                break;

            case PieceType.Rook:
                AddSlides(state, from, piece, moves, 1, 0);
                AddSlides(state, from, piece, moves, -1, 0);
                AddSlides(state, from, piece, moves, 0, 1);
                AddSlides(state, from, piece, moves, 0, -1);
                break;

            case PieceType.Bishop:
                AddSlides(state, from, piece, moves, 1, 1);
                AddSlides(state, from, piece, moves, -1, 1);
                AddSlides(state, from, piece, moves, 1, -1);
                AddSlides(state, from, piece, moves, -1, -1);
                break;

            case PieceType.Queen:
                AddSlides(state, from, piece, moves, 1, 0);
                AddSlides(state, from, piece, moves, -1, 0);
                AddSlides(state, from, piece, moves, 0, 1);
                AddSlides(state, from, piece, moves, 0, -1);
                AddSlides(state, from, piece, moves, 1, 1);
                AddSlides(state, from, piece, moves, -1, 1);
                AddSlides(state, from, piece, moves, 1, -1);
                AddSlides(state, from, piece, moves, -1, -1);
                break;

            case PieceType.Knight:
                {
                    int[,] deltas = new int[,]
                    {
                        { 1, 2 }, { 2, 1 }, { -1, 2 }, { -2, 1 },
                        { 1, -2 }, { 2, -1 }, { -1, -2 }, { -2, -1 }
                    };
                    for (int i = 0; i < 8; i++)
                    {
                        int nx = x + deltas[i, 0];
                        int ny = y + deltas[i, 1];
                        if (!InBounds(nx, ny)) continue;
                        if (IsEmpty(nx, ny) || IsEnemy(nx, ny))
                            moves.Add(new Vector2Int(nx, ny));
                    }
                }
                break;

            case PieceType.King:
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx;
                        int ny = y + dy;
                        if (!InBounds(nx, ny)) continue;
                        if (IsEmpty(nx, ny) || IsEnemy(nx, ny))
                            moves.Add(new Vector2Int(nx, ny));
                    }

                // CASTLING (simplified, match live: no check detection, just unmoved pieces and empty path)
                if (!piece.hasMoved)
                {
                    void TryCastle(int rookX, int stepX)
                    {
                        int ky = y;
                        int kx = x;

                        if (!InBounds(rookX, ky)) return;
                        var rookSq = state.squares[rookX, ky];
                        if (!rookSq.HasValue) return;
                        var rook = rookSq.Value;
                        if (rook.type != PieceType.Rook || rook.color != color || rook.hasMoved) return;

                        int curX = kx + stepX;
                        while (curX != rookX)
                        {
                            if (!InBounds(curX, ky)) return;
                            if (!IsEmpty(curX, ky)) return;
                            curX += stepX;
                        }

                        int destKx = x + 2 * stepX;
                        if (!InBounds(destKx, ky)) return;
                        if (!IsEmpty(destKx, ky)) return;

                        moves.Add(new Vector2Int(destKx, ky));
                    }

                    TryCastle(0, -1);
                    TryCastle(7, 1);
                }
                break;
        }

        return moves;
    }

    static void AddSlides(SimState state, Vector2Int from, SimPiece piece, List<Vector2Int> moves, int dx, int dy)
    {
        int x = from.x;
        int y = from.y;
        int bs = state.boardSize;
        var color = piece.color;

        bool InBounds(int a, int b) => a >= 0 && b >= 0 && a < bs && b < bs;
        bool IsEmpty(int a, int b) => !state.squares[a, b].HasValue;
        bool IsEnemy(int a, int b)
        {
            var sq = state.squares[a, b];
            return sq.HasValue && sq.Value.color != color;
        }

        int nx = x + dx;
        int ny = y + dy;

        while (InBounds(nx, ny))
        {
            if (IsEmpty(nx, ny))
            {
                moves.Add(new Vector2Int(nx, ny));
            }
            else
            {
                if (IsEnemy(nx, ny))
                    moves.Add(new Vector2Int(nx, ny));
                break;
            }

            nx += dx;
            ny += dy;
        }
    }
}
