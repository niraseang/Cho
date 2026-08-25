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

public class SimState
{
    // Dimensions (copy from BoardManager)
    public int boardSize;        // usually 8
    public int intersectionSize; // usually 9

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

    // Terminal state info
    public bool gameOver;
    public PieceColor? winner; // null = not decided or draw

    public SimState(int boardSize, int intersectionSize)
    {
        this.boardSize = boardSize;
        this.intersectionSize = intersectionSize;

        squares = new SimPiece?[boardSize, boardSize];
        stones = new SimStoneColor[intersectionSize, intersectionSize];

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
        var copy = new SimState(boardSize, intersectionSize)
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
            gameOver = this.gameOver,
            winner = this.winner
        };

        // Copy pending pawn corner options
        copy.pendingPawnCornerOptions.Clear();
        copy.pendingPawnCornerOptions.AddRange(this.pendingPawnCornerOptions);

        // Copy squares
        for (int x = 0; x < boardSize; x++)
        {
            for (int y = 0; y < boardSize; y++)
            {
                copy.squares[x, y] = this.squares[x, y];
            }
        }

        // Copy stones
        for (int ix = 0; ix < intersectionSize; ix++)
        {
            for (int iy = 0; iy < intersectionSize; iy++)
            {
                copy.stones[ix, iy] = this.stones[ix, iy];
            }
        }

        return copy;
    }
}
