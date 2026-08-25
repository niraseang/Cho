using System;
using UnityEngine;

/// <summary>
/// Turns a SimState into the plane stack a policy/value network consumes, and encodes moves
/// into the matching policy indices.
///
/// Two design points drive the layout:
///
/// 1. Everything lives on the INTERSECTION grid, one larger than the square grid on each axis.
///    A piece on square (x,y) occupies cell (x,y), so the piece and the four corners that can
///    capture it form a 2x2 block. A single 3x3 convolution therefore sees a whole surround,
///    complete or threatened, and territory (3 of 4 corners) sits in the same receptive field.
///    Piece planes leave the last row and column zero.
///
/// 2. Everything is canonicalised to the side to move: when Black moves, the board is flipped
///    vertically and the colours swapped, so the mover always advances "up". Halves what the
///    network must learn, and matters here because pawns are directional.
///    The flip preserves the corner relationship - square y maps to boardHeight-1-y and its
///    corners at iy and iy+1 map to iy+1 and iy of the flipped grid - so the 2x2 block survives.
///
/// Tensor layout is (plane, row, col) = (C, H, W) with H = intersectionHeight, W =
/// intersectionWidth, flattened as plane*H*W + y*W + x. That is what PyTorch conv2d expects.
/// </summary>
public static class SimFeatures
{
    // --- piece planes, own then opponent, ordered by PieceType ---
    public const int OwnPieces      = 0;   // 6 planes
    public const int OppPieces      = 6;   // 6 planes

    // --- stones ---
    public const int OwnStones      = 12;
    public const int OppStones      = 13;

    // --- group liberties: exactly 1, exactly 2, 3 or more ---
    public const int OwnLiberties   = 14;  // 3 planes
    public const int OppLiberties   = 17;  // 3 planes

    // --- opponent stones on 1 / 2 / 3 corners of a square (4 would already have captured) ---
    public const int SurroundPress  = 20;  // 3 planes

    // --- territory ownership per square ---
    public const int OwnTerritory   = 23;
    public const int OppTerritory   = 24;

    // --- which decision this is, broadcast over the board ---
    public const int PhaseChess     = 25;
    public const int PhaseTerritory = 26;
    public const int PhaseBonus     = 27;
    public const int PhaseMainStone = 28;

    // --- misc ---
    public const int KoPoint        = 29;
    public const int PlaceableMask  = 30;
    public const int LastMoved      = 31;
    public const int NoProgress     = 32;

    public const int PlaneCount     = 33;

    public static int TensorSize(SimState s) =>
        PlaneCount * s.intersectionHeight * s.intersectionWidth;

    /// <summary>True when the board must be flipped to put the mover at the bottom.</summary>
    public static bool NeedsFlip(SimState s) => s.currentPlayer == PieceColor.Black;

    // ------------------------------------------------------------ coordinates

    /// <summary>Canonical intersection cell for (ix, iy).</summary>
    public static void MapIntersection(SimState s, int ix, int iy, out int cx, out int cy)
    {
        cx = ix;
        cy = NeedsFlip(s) ? (s.intersectionHeight - 1 - iy) : iy;
    }

    /// <summary>Canonical square cell for (x, y). Shares the grid with intersections.</summary>
    public static void MapSquare(SimState s, int x, int y, out int cx, out int cy)
    {
        cx = x;
        cy = NeedsFlip(s) ? (s.boardHeight - 1 - y) : y;
    }

    // ---------------------------------------------------------------- encoding

    public static void Encode(SimState s, float[] dest)
    {
        if (s == null || dest == null) return;

        int W = s.intersectionWidth;
        int H = s.intersectionHeight;
        int planeStride = H * W;

        Array.Clear(dest, 0, Math.Min(dest.Length, PlaneCount * planeStride));

        void Set(int plane, int cx, int cy, float v)
        {
            if (cx < 0 || cy < 0 || cx >= W || cy >= H) return;
            dest[plane * planeStride + cy * W + cx] = v;
        }

        void Fill(int plane, float v)
        {
            int off = plane * planeStride;
            for (int i = 0; i < planeStride; i++) dest[off + i] = v;
        }

        var mover = s.currentPlayer;

        // --- pieces -------------------------------------------------------
        for (int x = 0; x < s.boardWidth; x++)
        {
            for (int y = 0; y < s.boardHeight; y++)
            {
                var sp = s.squares[x, y];
                if (!sp.HasValue) continue;

                var p = sp.Value;
                int block = (p.color == mover) ? OwnPieces : OppPieces;
                MapSquare(s, x, y, out int cx, out int cy);
                Set(block + (int)p.type, cx, cy, 1f);
            }
        }

        var ownStone = (mover == PieceColor.White) ? SimStoneColor.White : SimStoneColor.Black;
        var oppStone = (ownStone == SimStoneColor.White) ? SimStoneColor.Black : SimStoneColor.White;

        // --- stones and liberties ----------------------------------------
        var libs = LibertyMap(s);
        for (int ix = 0; ix < W; ix++)
        {
            for (int iy = 0; iy < H; iy++)
            {
                var c = s.stones[ix, iy];
                if (c == SimStoneColor.None) continue;

                MapIntersection(s, ix, iy, out int cx, out int cy);
                Set(c == ownStone ? OwnStones : OppStones, cx, cy, 1f);

                int block = (c == ownStone) ? OwnLiberties : OppLiberties;
                int l = libs[ix, iy];
                int bucket = l <= 1 ? 0 : (l == 2 ? 1 : 2);
                Set(block + bucket, cx, cy, 1f);
            }
        }

        // --- surround pressure and territory, per square -------------------
        for (int x = 0; x < s.boardWidth; x++)
        {
            for (int y = 0; y < s.boardHeight; y++)
            {
                if (x + 1 >= W || y + 1 >= H) continue;

                CountCorners(s, x, y, ownStone, oppStone, out int own, out int opp);

                MapSquare(s, x, y, out int cx, out int cy);

                if (opp >= 1 && opp <= 3) Set(SurroundPress + (opp - 1), cx, cy, 1f);
                if (own >= 3) Set(OwnTerritory, cx, cy, 1f);
                if (opp >= 3) Set(OppTerritory, cx, cy, 1f);
            }
        }

        // --- which decision this is ---------------------------------------
        if (s.phaseOne) Fill(PhaseChess, 1f);
        else if (s.waitingForTerritoryClick) Fill(PhaseTerritory, 1f);
        else if (s.waitingForPawnStoneChoice) Fill(PhaseBonus, 1f);
        else Fill(PhaseMainStone, 1f);

        // --- misc ----------------------------------------------------------
        if (s.goKoPoint.HasValue)
        {
            MapIntersection(s, s.goKoPoint.Value.x, s.goKoPoint.Value.y, out int kx, out int ky);
            Set(KoPoint, kx, ky, 1f);
        }

        // Empty and not the ko point. Suicide is legal, and superko is not applied inside the
        // tree, so those are the only two constraints this plane can express.
        for (int ix = 0; ix < W; ix++)
        {
            for (int iy = 0; iy < H; iy++)
            {
                if (s.stones[ix, iy] != SimStoneColor.None) continue;
                if (s.goKoPoint.HasValue && s.goKoPoint.Value.x == ix && s.goKoPoint.Value.y == iy) continue;
                MapIntersection(s, ix, iy, out int cx, out int cy);
                Set(PlaceableMask, cx, cy, 1f);
            }
        }

        if (s.lastMovedSquare.HasValue)
        {
            var sq = s.lastMovedSquare.Value;
            if (sq.x >= 0 && sq.y >= 0 && sq.x < s.boardWidth && sq.y < s.boardHeight)
            {
                MapSquare(s, sq.x, sq.y, out int lx, out int ly);
                Set(LastMoved, lx, ly, 1f);
            }
        }

        int limit = Math.Max(1, SimRules.noProgressTurnLimit);
        Fill(NoProgress, Math.Min(1f, s.noProgressTurns / (float)limit));
    }

    static void CountCorners(SimState s, int x, int y,
                             SimStoneColor ownStone, SimStoneColor oppStone,
                             out int own, out int opp)
    {
        own = 0;
        opp = 0;

        for (int i = 0; i < 4; i++)
        {
            int ix = x + (i & 1);
            int iy = y + (i >> 1);
            var c = s.stones[ix, iy];
            if (c == ownStone) own++;
            else if (c == oppStone) opp++;
        }
    }

    // ------------------------------------------------------------- liberties

    [ThreadStatic] static int[,] _libScratch;
    [ThreadStatic] static int[,] _libStamp;
    [ThreadStatic] static int _libToken;

    /// <summary>Liberty count of the group each stone belongs to. Empty points read 0.</summary>
    public static int[,] LibertyMap(SimState s)
    {
        int W = s.intersectionWidth, H = s.intersectionHeight;

        if (_libScratch == null || _libScratch.GetLength(0) < W || _libScratch.GetLength(1) < H)
        {
            _libScratch = new int[W, H];
            _libStamp = new int[W, H];
            _libToken = 0;
        }

        var result = _libScratch;
        for (int x = 0; x < W; x++)
            for (int y = 0; y < H; y++)
                result[x, y] = 0;

        var group = new System.Collections.Generic.List<Vector2Int>();
        var seen = new System.Collections.Generic.HashSet<int>();
        var stack = new System.Collections.Generic.Stack<Vector2Int>();
        var done = new bool[W, H];

        for (int sx = 0; sx < W; sx++)
        {
            for (int sy = 0; sy < H; sy++)
            {
                if (done[sx, sy]) continue;
                var color = s.stones[sx, sy];
                if (color == SimStoneColor.None) continue;

                group.Clear();
                seen.Clear();
                stack.Clear();
                stack.Push(new Vector2Int(sx, sy));
                done[sx, sy] = true;

                while (stack.Count > 0)
                {
                    var p = stack.Pop();
                    group.Add(p);

                    void Visit(int nx, int ny)
                    {
                        if (nx < 0 || ny < 0 || nx >= W || ny >= H) return;
                        var c = s.stones[nx, ny];
                        if (c == SimStoneColor.None) { seen.Add(ny * W + nx); return; }
                        if (c != color || done[nx, ny]) return;
                        done[nx, ny] = true;
                        stack.Push(new Vector2Int(nx, ny));
                    }

                    Visit(p.x + 1, p.y);
                    Visit(p.x - 1, p.y);
                    Visit(p.x, p.y + 1);
                    Visit(p.x, p.y - 1);
                }

                foreach (var p in group) result[p.x, p.y] = seen.Count;
            }
        }

        return result;
    }

    // ---------------------------------------------------------- move indices

    /// <summary>Number of logits in the from x to chess policy head.</summary>
    public static int ChessPolicySize(SimState s)
    {
        int squares = s.boardWidth * s.boardHeight;
        return squares * squares;
    }

    /// <summary>Number of logits in the intersection head, shared by all three stone decisions.</summary>
    public static int IntersectionPolicySize(SimState s) =>
        s.intersectionWidth * s.intersectionHeight;

    public static int SquareIndex(SimState s, Vector2Int sq)
    {
        MapSquare(s, sq.x, sq.y, out int cx, out int cy);
        return cy * s.boardWidth + cx;
    }

    public static int IntersectionIndex(SimState s, Vector2Int pt)
    {
        MapIntersection(s, pt.x, pt.y, out int cx, out int cy);
        return cy * s.intersectionWidth + cx;
    }

    // The flip is its own inverse, so decoding reuses the same mapping. Needed at inference
    // time to turn a policy index back into a move.
    public static Vector2Int SquareFromIndex(SimState s, int idx)
    {
        int cx = idx % s.boardWidth;
        int cy = idx / s.boardWidth;
        return new Vector2Int(cx, NeedsFlip(s) ? (s.boardHeight - 1 - cy) : cy);
    }

    public static Vector2Int IntersectionFromIndex(SimState s, int idx)
    {
        int cx = idx % s.intersectionWidth;
        int cy = idx / s.intersectionWidth;
        return new Vector2Int(cx, NeedsFlip(s) ? (s.intersectionHeight - 1 - cy) : cy);
    }

    /// <summary>Policy index for a chess move, in the canonical frame.</summary>
    public static int ChessMoveIndex(SimState s, SimChessMove m)
    {
        int squares = s.boardWidth * s.boardHeight;
        return SquareIndex(s, m.from) * squares + SquareIndex(s, m.to);
    }

    /// <summary>0-3 for Queen / Rook / Bishop / Knight, -1 when the move is not a promotion.</summary>
    public static int PromotionIndex(SimChessMove m)
    {
        if (!m.promotion.HasValue) return -1;
        switch (m.promotion.Value)
        {
            case PieceType.Queen:  return 0;
            case PieceType.Rook:   return 1;
            case PieceType.Bishop: return 2;
            case PieceType.Knight: return 3;
            default:               return -1;
        }
    }

    /// <summary>
    /// Policy index for any decision. Chess moves index the chess head; territory removals,
    /// pawn bonus stones and main stones all index the shared intersection head.
    /// </summary>
    public static int PolicyIndex(SimState s, SimTurn t)
    {
        if (t.chessMove.HasValue) return ChessMoveIndex(s, t.chessMove.Value);
        if (t.mainStone.HasValue) return IntersectionIndex(s, t.mainStone.Value.intersection);
        if (t.bonusPawnStone.HasValue) return IntersectionIndex(s, t.bonusPawnStone.Value.intersection);
        if (t.territoryRemoval.HasValue) return IntersectionIndex(s, t.territoryRemoval.Value.intersection);
        return -1;
    }
}
