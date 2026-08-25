using System.Collections.Generic;
using UnityEngine;

// Minimal helper for chess moves. Full chess rules (en passant, castling) need more state tracking.
public static class ChessMovesHelper
{
    public static List<Vector2Int> GetLegalMovesForPiece(Piece piece, BoardManager board)
    {
        var moves = new List<Vector2Int>();
        // simple pawn forward move as placeholder
        if (piece.type == PieceType.Pawn)
        {
            int dir = piece.color == PieceColor.White ? 1 : -1;
            var fwd = new Vector2Int(piece.square.x, piece.square.y + dir);
            if (fwd.x >= 0 && fwd.x < board.boardSize && fwd.y >= 0 && fwd.y < board.boardSize && board.GetSquarePiece(fwd) == null)
                moves.Add(fwd);
        }
        // other pieces omitted for now
        return moves;
    }
}
