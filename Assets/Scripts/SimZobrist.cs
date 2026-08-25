using System;
using UnityEngine;

/// <summary>
/// Deterministic Zobrist hashing for SimState.
/// Used by the transposition table to identify repeated positions.
/// </summary>
public static class SimZobrist
{
    // Pieces: [color(2)][type(6)][square(64)][flags(4)]
    static readonly ulong[,,,] PieceKeys;

    // Stones on intersections: [color(2)][intersection(81)] where intersection = ix + 9*iy
    static readonly ulong[,] StoneKeys;

    // Ko point on intersections: [82] (0 = none, 1..81 = ix + 9*iy + 1)
    static readonly ulong[] KoPointKeys;

    static readonly ulong SideToMoveKey;

    // Castling rights keys (KS/QS for each color)
    static readonly ulong WhiteCastleKingSideKey;
    static readonly ulong WhiteCastleQueenSideKey;
    static readonly ulong BlackCastleKingSideKey;
    static readonly ulong BlackCastleQueenSideKey;

    // En-passant square key (64). Only set when en passant is available.
    static readonly ulong[] EnPassantSquareKeys;

    // Last moved chess square (65 = none + 64 squares). Affects forced phase-2 actions.
    static readonly ulong[] LastMovedSquareKeys;

    // Misc game-state keys (include only what may affect rules/search)
    static readonly ulong PhaseOneKey;
    static readonly ulong BlackInitialStonePendingKey;
    static readonly ulong WaitingForTerritoryClickKey;
    static readonly ulong WaitingForPawnStoneChoiceKey;
    static readonly ulong GameOverKey;
    static readonly ulong WhiteWinnerKey;
    static readonly ulong BlackWinnerKey;

    static SimZobrist()
    {
        // Fixed seed so hashes are stable across sessions (useful for repro/debugging).
        var rng = new System.Random(1337);

        PieceKeys = new ulong[2, 6, 64, 4];
        for (int c = 0; c < 2; c++)
            for (int t = 0; t < 6; t++)
                for (int sq = 0; sq < 64; sq++)
                    for (int f = 0; f < 4; f++)
                        PieceKeys[c, t, sq, f] = NextUlong(rng);

        StoneKeys = new ulong[2, 81];
        for (int c = 0; c < 2; c++)
            for (int i = 0; i < 81; i++)
                StoneKeys[c, i] = NextUlong(rng);

        KoPointKeys = new ulong[82];
        for (int i = 0; i < KoPointKeys.Length; i++)
            KoPointKeys[i] = NextUlong(rng);

        SideToMoveKey = NextUlong(rng);

        WhiteCastleKingSideKey = NextUlong(rng);
        WhiteCastleQueenSideKey = NextUlong(rng);
        BlackCastleKingSideKey = NextUlong(rng);
        BlackCastleQueenSideKey = NextUlong(rng);

        EnPassantSquareKeys = new ulong[64];
        for (int i = 0; i < 64; i++)
            EnPassantSquareKeys[i] = NextUlong(rng);

        LastMovedSquareKeys = new ulong[65];
        for (int i = 0; i < LastMovedSquareKeys.Length; i++)
            LastMovedSquareKeys[i] = NextUlong(rng);

        PhaseOneKey = NextUlong(rng);
        BlackInitialStonePendingKey = NextUlong(rng);
        WaitingForTerritoryClickKey = NextUlong(rng);
        WaitingForPawnStoneChoiceKey = NextUlong(rng);
        GameOverKey = NextUlong(rng);
        WhiteWinnerKey = NextUlong(rng);
        BlackWinnerKey = NextUlong(rng);
    }

    public static ulong ComputeHash(SimState state)
    {
        if (state == null) return 0;

        ulong h = 0;

        // Pieces
        for (int x = 0; x < state.boardWidth; x++)
        {
            for (int y = 0; y < state.boardHeight; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;

                var p = sp.Value;
                int colorIndex = (p.color == PieceColor.White) ? 0 : 1;
                int typeIndex = PieceTypeToIndex(p.type);
                if (typeIndex < 0) continue;

                int sq = (y * 8) + x;
                int flags = (p.hasMoved ? 1 : 0) | (p.justDoubleStepped ? 2 : 0);

                h ^= PieceKeys[colorIndex, typeIndex, sq, flags];
            }
        }

        // Stones (assumes intersectionSize is 9; if not, still hashes a prefix deterministically)
        int maxIx = Math.Min(state.intersectionWidth, 9);
        int maxIy = Math.Min(state.intersectionHeight, 9);
        for (int ix = 0; ix < maxIx; ix++)
        {
            for (int iy = 0; iy < maxIy; iy++)
            {
                var c = state.stones[ix, iy];
                if (c == SimStoneColor.None) continue;

                int colorIndex = (c == SimStoneColor.White) ? 0 : 1;
                int idx = ix + 9 * iy;
                h ^= StoneKeys[colorIndex, idx];
            }
        }

        // Ko point
        if (state.goKoPoint.HasValue)
        {
            var kp = state.goKoPoint.Value;
            if (kp.x >= 0 && kp.y >= 0 && kp.x < 9 && kp.y < 9)
            {
                int idx = kp.x + 9 * kp.y + 1;
                h ^= KoPointKeys[idx];
            }
        }
        else
        {
            h ^= KoPointKeys[0];
        }

        // Side to move
        if (state.currentPlayer == PieceColor.Black)
        {
            h ^= SideToMoveKey;
        }

        // Castling rights
        if (state.whiteCanCastleKingSide) h ^= WhiteCastleKingSideKey;
        if (state.whiteCanCastleQueenSide) h ^= WhiteCastleQueenSideKey;
        if (state.blackCanCastleKingSide) h ^= BlackCastleKingSideKey;
        if (state.blackCanCastleQueenSide) h ^= BlackCastleQueenSideKey;

        // En passant
        if (state.enPassantPawnSquare.HasValue)
        {
            var sq = state.enPassantPawnSquare.Value;
            if (sq.x >= 0 && sq.x < 8 && sq.y >= 0 && sq.y < 8)
            {
                int idx = (sq.y * 8) + sq.x;
                h ^= EnPassantSquareKeys[idx];
            }
        }

        // Last moved square
        if (state.lastMovedSquare.HasValue)
        {
            var sq = state.lastMovedSquare.Value;
            if (sq.x >= 0 && sq.x < 8 && sq.y >= 0 && sq.y < 8)
            {
                int idx = (sq.y * 8) + sq.x + 1;
                h ^= LastMovedSquareKeys[idx];
            }
            else
            {
                h ^= LastMovedSquareKeys[0];
            }
        }
        else
        {
            h ^= LastMovedSquareKeys[0];
        }

        // Misc flags
        if (state.phaseOne) h ^= PhaseOneKey;
        if (state.blackInitialStonePending) h ^= BlackInitialStonePendingKey;
        if (state.waitingForTerritoryClick) h ^= WaitingForTerritoryClickKey;
        if (state.waitingForPawnStoneChoice) h ^= WaitingForPawnStoneChoiceKey;
        if (state.gameOver) h ^= GameOverKey;
        if (state.winner.HasValue)
        {
            if (state.winner.Value == PieceColor.White) h ^= WhiteWinnerKey;
            else h ^= BlackWinnerKey;
        }

        return h;
    }

    static int PieceTypeToIndex(PieceType t)
    {
        switch (t)
        {
            case PieceType.King: return 0;
            case PieceType.Queen: return 1;
            case PieceType.Rook: return 2;
            case PieceType.Bishop: return 3;
            case PieceType.Knight: return 4;
            case PieceType.Pawn: return 5;
            default: return -1;
        }
    }

    static ulong NextUlong(System.Random rng)
    {
        // Combine two 32-bit values into one 64-bit value.
        unchecked
        {
            uint a = (uint)rng.Next(int.MinValue, int.MaxValue);
            uint b = (uint)rng.Next(int.MinValue, int.MaxValue);
            return ((ulong)a << 32) ^ b;
        }
    }

    /// <summary>
    /// Hash of the *board* only: piece identity and placement, stone placement, and side to move.
    /// Deliberately excludes ko point, castling rights, en passant, phase flags and last-moved
    /// square, so two positions with the same pieces and stones compare equal.
    ///
    /// This is the superko key. Excluding those extras makes the rule stricter (more positions
    /// count as "the same"), which is the point: it catches more cycles.
    /// </summary>
    public static ulong ComputeBoardHash(SimState state)
    {
        if (state == null) return 0UL;

        ulong h = 0UL;

        for (int x = 0; x < state.boardWidth; x++)
        {
            for (int y = 0; y < state.boardHeight; y++)
            {
                var sp = state.squares[x, y];
                if (!sp.HasValue) continue;

                var p = sp.Value;
                int colorIndex = (p.color == PieceColor.White) ? 0 : 1;
                int typeIndex = PieceTypeToIndex(p.type);
                if (typeIndex < 0) continue;

                int sq = (y * 8) + x;

                // Flag slot 0 always: hasMoved / justDoubleStepped are not part of the board.
                h ^= PieceKeys[colorIndex, typeIndex, sq, 0];
            }
        }

        int maxIx = Math.Min(state.intersectionWidth, 9);
        int maxIy = Math.Min(state.intersectionHeight, 9);
        for (int ix = 0; ix < maxIx; ix++)
        {
            for (int iy = 0; iy < maxIy; iy++)
            {
                var c = state.stones[ix, iy];
                if (c == SimStoneColor.None) continue;

                int colorIndex = (c == SimStoneColor.White) ? 0 : 1;
                h ^= StoneKeys[colorIndex, ix + 9 * iy];
            }
        }

        if (state.currentPlayer == PieceColor.Black) h ^= SideToMoveKey;

        return h;
    }

}
