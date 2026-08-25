using System.Collections.Generic;
using UnityEngine; // for Vector2Int

public enum SimStoneColor
{
    None = 0,
    White = 1,
    Black = 2
}

public struct SimPiece
{
    public PieceColor color;
    public PieceType type;

    // For castling and general move rules
    public bool hasMoved;

    // For en passant
    public bool justDoubleStepped;
}

public struct SimChessMove
{
    public Vector2Int from;
    public Vector2Int to;

    // Only used for pawn promotions. Null if not a promotion.
    public PieceType? promotion;
}

public struct SimStonePlacement
{
    // Intersection coords: (0..intersectionSize-1, 0..intersectionSize-1)
    public Vector2Int intersection;
    public SimStoneColor color;
}

public struct SimTerritoryRemoval
{
    // Which intersection is clicked to remove territory/liberties
    public Vector2Int intersection;
}

public struct SimTurn
{
    // Special-case: initial black Go stone before any chess move.
    public bool isInitialBlackStoneTurn;

    // Phase 1 chess move (null if none, e.g. initial special turn)
    public SimChessMove? chessMove;

    // Phase 2 components:

    // If waitingForTerritoryClick is true, the first Go click is this.
    public SimTerritoryRemoval? territoryRemoval;

    // Main Go stone placement of the turn (normal phase 2 action).
    public SimStonePlacement? mainStone;

    // Optional bonus pawn stone placement after the main stone.
    public SimStonePlacement? bonusPawnStone;
}

/// <summary>
/// Board positions already seen this game, for the superko restriction.
///
/// Deliberately a reference type that SimState.DeepCopy shares rather than clones: the search
/// walks one shared history, pushing on the way down and popping on the way back up. Cloning it
/// per node would cost more than the rest of a node put together.
/// </summary>
public class SimPositionHistory
{
    readonly Dictionary<ulong, int> _counts = new Dictionary<ulong, int>(256);

    public void Push(ulong hash)
    {
        _counts.TryGetValue(hash, out int n);
        _counts[hash] = n + 1;
    }

    public void Pop(ulong hash)
    {
        if (!_counts.TryGetValue(hash, out int n)) return;
        if (n <= 1) _counts.Remove(hash);
        else _counts[hash] = n - 1;
    }

    public bool Contains(ulong hash) => _counts.ContainsKey(hash);

    public void Clear() => _counts.Clear();

    public SimPositionHistory Clone()
    {
        var c = new SimPositionHistory();
        foreach (var kv in _counts) c._counts[kv.Key] = kv.Value;
        return c;
    }
}

/// <summary>
/// Material snapshot used by the no-progress rule. Compared against the same player's
/// previous turn end - comparing against whichever player moved last does not work,
/// because a boundary shuffle leaves the counts different on alternating turns even
/// though nothing is changing over a full cycle.
/// </summary>
public struct SimProgressCounts
{
    public bool valid;
    public int whiteStones, blackStones, whitePieces, blackPieces;

    public bool Matches(SimProgressCounts o) =>
        valid && o.valid &&
        whiteStones == o.whiteStones && blackStones == o.blackStones &&
        whitePieces == o.whitePieces && blackPieces == o.blackPieces;
}

public class SimState
{
    // Dimensions. Width is the x axis (files), height is the y axis (ranks).
    // Intersections are always one more than squares on each axis, since they are the corners.
    // SimZobrist indexes with a fixed stride, so width must be <= 8 squares / <= 9 intersections.
    public int boardWidth;
    public int boardHeight;
    public int intersectionWidth;
    public int intersectionHeight;

    // Chess board: null where empty
    public SimPiece?[,] squares; // [boardSize, boardSize]

    // Go stones: None/White/Black
    public SimStoneColor[,] stones; // [intersectionSize, intersectionSize]

    // Simple-ko: intersection forbidden for the next normal Go main-stone placement.
    // This is NOT applied/updated by bonus pawn stone placements.
    public Vector2Int? goKoPoint;

    // Whose turn it is
    public PieceColor currentPlayer;

    // true = phase 1 (chess), false = phase 2 (Go / territory / bonus)
    public bool phaseOne;

    // Special starting rule
    public bool blackInitialStonePending;

    // Hybrid-flow flags
    public bool waitingForTerritoryClick;
    public bool waitingForPawnStoneChoice;

    // Last moved chess square (for territory, en passant, etc.)
    public Vector2Int? lastMovedSquare;

    // Pawn bonus-corner options in Go coordinates
    public List<Vector2Int> pendingPawnCornerOptions;

    // En passant: pawn square that just double-stepped (if any)
    public Vector2Int? enPassantPawnSquare;

    // Castling rights
    public bool whiteCanCastleKingSide;
    public bool whiteCanCastleQueenSide;
    public bool blackCanCastleKingSide;
    public bool blackCanCastleQueenSide;

    // King locations (optional but convenient)
    public Vector2Int? whiteKingSquare;
    public Vector2Int? blackKingSquare;

    // Board positions already seen this game. Null disables the superko restriction,
    // which is what most unit-style uses of SimState want.
    public SimPositionHistory positionHistory;

    // No-progress rule: consecutive player turns with no capture and no net change to
    // either side's stone count. Reaching SimRules.noProgressTurnLimit is a draw.
    public int noProgressTurns;
    public SimProgressCounts progressAfterWhiteTurn;
    public SimProgressCounts progressAfterBlackTurn;

    // Terminal state info
    public bool gameOver;
    public PieceColor? winner; // null = not decided or draw

    public SimState(int boardWidth, int boardHeight)
    {
        this.boardWidth = boardWidth;
        this.boardHeight = boardHeight;
        this.intersectionWidth = boardWidth + 1;
        this.intersectionHeight = boardHeight + 1;

        squares = new SimPiece?[boardWidth, boardHeight];
        stones = new SimStoneColor[intersectionWidth, intersectionHeight];

        goKoPoint = null;

        currentPlayer = PieceColor.White;
        phaseOne = true;
        blackInitialStonePending = true;

        waitingForTerritoryClick = false;
        waitingForPawnStoneChoice = false;

        lastMovedSquare = null;
        pendingPawnCornerOptions = new List<Vector2Int>();

        enPassantPawnSquare = null;

        whiteCanCastleKingSide = true;
        whiteCanCastleQueenSide = true;
        blackCanCastleKingSide = true;
        blackCanCastleQueenSide = true;

        whiteKingSquare = null;
        blackKingSquare = null;

        gameOver = false;
        winner = null;
    }

    public SimState DeepCopy()
    {
        var copy = new SimState(boardWidth, boardHeight)
        {
            currentPlayer = this.currentPlayer,
            phaseOne = this.phaseOne,
            blackInitialStonePending = this.blackInitialStonePending,
            waitingForTerritoryClick = this.waitingForTerritoryClick,
            waitingForPawnStoneChoice = this.waitingForPawnStoneChoice,
            lastMovedSquare = this.lastMovedSquare,
            enPassantPawnSquare = this.enPassantPawnSquare,
            whiteCanCastleKingSide = this.whiteCanCastleKingSide,
            whiteCanCastleQueenSide = this.whiteCanCastleQueenSide,
            blackCanCastleKingSide = this.blackCanCastleKingSide,
            blackCanCastleQueenSide = this.blackCanCastleQueenSide,
            whiteKingSquare = this.whiteKingSquare,
            blackKingSquare = this.blackKingSquare,
            goKoPoint = this.goKoPoint,

            // Shared by reference on purpose - see SimPositionHistory.
            positionHistory = this.positionHistory,

            noProgressTurns = this.noProgressTurns,
            progressAfterWhiteTurn = this.progressAfterWhiteTurn,
            progressAfterBlackTurn = this.progressAfterBlackTurn,

            gameOver = this.gameOver,
            winner = this.winner
        };

        // Copy pending pawn corner options
        copy.pendingPawnCornerOptions.Clear();
        copy.pendingPawnCornerOptions.AddRange(this.pendingPawnCornerOptions);

        // Copy squares
        for (int x = 0; x < boardWidth; x++)
        {
            for (int y = 0; y < boardHeight; y++)
            {
                copy.squares[x, y] = this.squares[x, y];
            }
        }

        // Copy stones
        for (int ix = 0; ix < intersectionWidth; ix++)
        {
            for (int iy = 0; iy < intersectionHeight; iy++)
            {
                copy.stones[ix, iy] = this.stones[ix, iy];
            }
        }

        return copy;
    }
}
