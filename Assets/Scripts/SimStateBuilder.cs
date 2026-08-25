using UnityEngine;

public static class SimStateBuilder
{
    public static SimState FromLiveGame(GameController gc, BoardManager bm)
    {
        if (gc == null)
        {
            Debug.LogError("SimStateBuilder.FromLiveGame: GameController is null");
            return null;
        }
        if (bm == null)
        {
            Debug.LogError("SimStateBuilder.FromLiveGame: BoardManager is null");
            return null;
        }

        // The live board is square; SimState takes width and height separately.
        int bs = bm.boardSize;
        int isz = bm.intersectionSize;

        var s = new SimState(bs, bs);

        // --- Copy chess pieces ---
        for (int x = 0; x < bs; x++)
        {
            for (int y = 0; y < bs; y++)
            {
                Piece p = bm.squares[x, y];
                if (p == null)
                {
                    s.squares[x, y] = null;
                    continue;
                }

                var sp = new SimPiece
                {
                    color = p.color,
                    type = p.type,
                    hasMoved = p.hasMoved,
                    justDoubleStepped = p.justDoubleStepped
                };

                s.squares[x, y] = sp;

                if (p.type == PieceType.King)
                {
                    if (p.color == PieceColor.White)
                        s.whiteKingSquare = new Vector2Int(x, y);
                    else
                        s.blackKingSquare = new Vector2Int(x, y);
                }
            }
        }

        // --- Copy stones (Go board) ---
        for (int ix = 0; ix < isz; ix++)
        {
            for (int iy = 0; iy < isz; iy++)
            {
                var inter = bm.intersections[ix, iy];
                if (inter == null || inter.occupant == null)
                {
                    s.stones[ix, iy] = SimStoneColor.None;
                    continue;
                }

                // Map from your Stone component (if present) to SimStoneColor.
                var stone = inter.occupant.GetComponent<Stone>();
                if (stone != null)
                {
                    // Assume Stone.color is a StoneColor enum; map it here.
                    switch (stone.color)
                    {
                        case StoneColor.White:
                            s.stones[ix, iy] = SimStoneColor.White;
                            break;
                        case StoneColor.Black:
                            s.stones[ix, iy] = SimStoneColor.Black;
                            break;
                        default:
                            s.stones[ix, iy] = SimStoneColor.None;
                            break;
                    }
                }
                else
                {
                    // Fallback: try to infer from tag or name if needed
                    s.stones[ix, iy] = SimStoneColor.None;
                }
            }
        }

        // --- Copy basic global flags from GameController ---
        s.currentPlayer = gc.currentPlayer;

        // Simple-ko point (for normal Go main-stone moves).
        s.goKoPoint = gc.goKoPoint;

        // --- Derive castling rights and en passant window from the current live position ---
        // Castling: match live simplified rules (king/rook unmoved, rooks at files 0/7).
        bool HasUnmovedPieceAt(PieceColor c, PieceType t, int x, int y)
        {
            var sp = s.squares[x, y];
            if (!sp.HasValue) return false;
            var p = sp.Value;
            if (p.color != c || p.type != t) return false;
            return !p.hasMoved;
        }

        s.whiteCanCastleKingSide  = HasUnmovedPieceAt(PieceColor.White, PieceType.King, 4, 0) && HasUnmovedPieceAt(PieceColor.White, PieceType.Rook, 7, 0);
        s.whiteCanCastleQueenSide = HasUnmovedPieceAt(PieceColor.White, PieceType.King, 4, 0) && HasUnmovedPieceAt(PieceColor.White, PieceType.Rook, 0, 0);
        s.blackCanCastleKingSide  = HasUnmovedPieceAt(PieceColor.Black, PieceType.King, 4, 7) && HasUnmovedPieceAt(PieceColor.Black, PieceType.Rook, 7, 7);
        s.blackCanCastleQueenSide = HasUnmovedPieceAt(PieceColor.Black, PieceType.King, 4, 7) && HasUnmovedPieceAt(PieceColor.Black, PieceType.Rook, 0, 7);

        // En passant: locate the pawn that just double-stepped (should be at most one and should
        // belong to the side that just moved, i.e., opposite of currentPlayer).
        s.enPassantPawnSquare = null;
        for (int x = 0; x < bs; x++)
        {
            for (int y = 0; y < bs; y++)
            {
                var sp = s.squares[x, y];
                if (!sp.HasValue) continue;
                var p = sp.Value;
                if (p.type != PieceType.Pawn) continue;
                if (!p.justDoubleStepped) continue;
                if (p.color == s.currentPlayer) continue;
                s.enPassantPawnSquare = new Vector2Int(x, y);
                break;
            }
            if (s.enPassantPawnSquare.HasValue) break;
        }

        // --- mid-turn flow state ---
        // The snapshot has to know which decision is pending, or the AI cannot tell a main
        // stone from a forced territory removal and needs a separate code path per phase.
        s.phaseOne = gc.PhaseOne;
        s.waitingForTerritoryClick = gc.WaitingForTerritoryClick;
        s.waitingForPawnStoneChoice = gc.WaitingForPawnStoneChoice;
        s.lastMovedSquare = gc.LastMovedSquare;
        s.blackInitialStonePending = false;   // SimRules does not model the opening stone

        // --- castling rights ---
        // The live game has no flags for these; it decides castling from hasMoved on the king
        // and the corner rooks, so derive them the same way rather than leaving them all true.
        s.whiteCanCastleKingSide  = CanCastle(s, PieceColor.White, bs - 1, 0);
        s.whiteCanCastleQueenSide = CanCastle(s, PieceColor.White, 0, 0);
        s.blackCanCastleKingSide  = CanCastle(s, PieceColor.Black, bs - 1, bs - 1);
        s.blackCanCastleQueenSide = CanCastle(s, PieceColor.Black, 0, bs - 1);

        // Superko history is shared by reference, not copied: the AI's root move list has to be
        // filtered against the same positions the live game has actually reached.
        s.positionHistory = gc.PositionHistory;

        return s;
    }

    static bool CanCastle(SimState s, PieceColor color, int rookX, int rank)
    {
        var kingSq = color == PieceColor.White ? s.whiteKingSquare : s.blackKingSquare;
        if (!kingSq.HasValue) return false;

        var king = s.squares[kingSq.Value.x, kingSq.Value.y];
        if (!king.HasValue || king.Value.hasMoved) return false;

        if (rookX < 0 || rookX >= s.boardWidth || rank < 0 || rank >= s.boardHeight) return false;
        var rook = s.squares[rookX, rank];
        return rook.HasValue && rook.Value.type == PieceType.Rook
            && rook.Value.color == color && !rook.Value.hasMoved;
    }
}
