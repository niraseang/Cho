using UnityEngine;

public enum PieceColor { White, Black }
public enum PieceType { King, Queen, Rook, Bishop, Knight, Pawn }

public class Piece : MonoBehaviour
{
    public PieceColor color;
    public PieceType type;
    public Vector2Int square; // 0..7

    // NEW: flags for special chess moves
    public bool hasMoved = false;
    public bool justDoubleStepped = false;

    BoardManager boardManager;

    void Awake()
    {
        if (boardManager == null)
        {
            boardManager = Object.FindFirstObjectByType<BoardManager>();
        }
    }

    public void Init(PieceColor c, PieceType t, Vector2Int sq)
    {
        color = c;
        type = t;
        square = sq;
        transform.position = boardManager.SquareToWorld(sq.x, sq.y);
    }
}
