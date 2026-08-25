using System.Collections.Generic;
using UnityEngine;

public class RulesEngine : MonoBehaviour
{
    BoardManager board;

    void Awake()
    {
        board = Object.FindFirstObjectByType<BoardManager>();
    }

    // Determine if an intersection group (connected orthogonally) has liberties
    public bool HasLiberties(int ix, int iy)
    {
        var inter = board.GetIntersection(ix, iy);
        if (inter == null || inter.IsEmpty) return false;
        StoneColor color = inter.occupant.color;

        var visited = new bool[board.intersectionSize, board.intersectionSize];
        var stack = new Stack<Intersection>();
        stack.Push(inter);
        visited[ix, iy] = true;

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            // check liberties around
            var neighbors = GetOrthogonalNeighbors(cur.x, cur.y);
            foreach (var n in neighbors)
            {
                if (n == null) continue;
                if (n.IsEmpty) return true;
                if (!visited[n.x, n.y] && n.occupant != null && n.occupant.color == color)
                {
                    visited[n.x, n.y] = true;
                    stack.Push(n);
                }
            }
        }

        return false;
    }

    // Count unique liberties for the group containing (ix,iy).
    // Used for simple-ko detection (group size==1 and liberties==1 after a single-stone capture).
    public int CountLiberties(int ix, int iy)
    {
        var inter = board.GetIntersection(ix, iy);
        if (inter == null || inter.IsEmpty) return 0;

        StoneColor color = inter.occupant.color;

        var visited = new bool[board.intersectionSize, board.intersectionSize];
        var liberties = new HashSet<Vector2Int>();
        var stack = new Stack<Intersection>();
        stack.Push(inter);
        visited[ix, iy] = true;

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            var neighbors = GetOrthogonalNeighbors(cur.x, cur.y);
            foreach (var n in neighbors)
            {
                if (n == null) continue;
                if (n.IsEmpty)
                {
                    liberties.Add(new Vector2Int(n.x, n.y));
                    continue;
                }
                if (!visited[n.x, n.y] && n.occupant != null && n.occupant.color == color)
                {
                    visited[n.x, n.y] = true;
                    stack.Push(n);
                }
            }
        }

        return liberties.Count;
    }

    // capture groups with no liberties around a placed stone at ix,iy (check adjacent enemy groups)
    public List<Stone> ResolveCapturesAfterPlacement(int ix, int iy)
    {
        var captured = new List<Stone>();
        var neighbors = GetOrthogonalNeighbors(ix, iy);
        foreach (var n in neighbors)
        {
            if (n == null || n.IsEmpty) continue;
            if (n.occupant.color == board.GetIntersection(ix, iy).occupant.color) continue; // same color
            if (!HasLiberties(n.x, n.y))
            {
                captured.AddRange(GetGroupStones(n.x, n.y));
            }
        }
        return captured;
    }

    // get connected group stones
    public List<Stone> GetGroupStones(int ix, int iy)
    {
        var list = new List<Stone>();
        var inter = board.GetIntersection(ix, iy);
        if (inter == null || inter.IsEmpty) return list;
        StoneColor color = inter.occupant.color;

        var visited = new bool[board.intersectionSize, board.intersectionSize];
        var stack = new Stack<Intersection>();
        stack.Push(inter);
        visited[ix, iy] = true;

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            list.Add(cur.occupant);
            var neighbors = GetOrthogonalNeighbors(cur.x, cur.y);
            foreach (var n in neighbors)
            {
                if (n == null) continue;
                if (!visited[n.x, n.y] && n.occupant != null && n.occupant.color == color)
                {
                    visited[n.x, n.y] = true;
                    stack.Push(n);
                }
            }
        }

        return list;
    }

    Intersection[] GetOrthogonalNeighbors(int ix, int iy)
    {
        return new Intersection[] {
            board.GetIntersection(ix+1, iy),
            board.GetIntersection(ix-1, iy),
            board.GetIntersection(ix, iy+1),
            board.GetIntersection(ix, iy-1)
        };
    }

    // Territory: check if a given square is controlled by a color (>=3 surrounding intersections occupied by that color)
    public StoneColor? TerritoryOwnerOfSquare(int sx, int sy)
    {
        int countWhite = 0, countBlack = 0;
        var corners = new Vector2Int[] {
            new Vector2Int(sx, sy),
            new Vector2Int(sx+1, sy),
            new Vector2Int(sx, sy+1),
            new Vector2Int(sx+1, sy+1),
        };
        foreach (var c in corners)
        {
            var inter = board.GetIntersection(c.x, c.y);
            if (inter == null || inter.IsEmpty) continue;
            if (inter.occupant.color == StoneColor.White) countWhite++;
            else countBlack++;
        }
        if (countWhite >= 3) return StoneColor.White;
        if (countBlack >= 3) return StoneColor.Black;
        return null;
    }

    public bool IsSquareOwnTerritory(Piece piece, Vector2Int square)
    {
        var owner = TerritoryOwnerOfSquare(square.x, square.y);
        if (!owner.HasValue) return false;
        if (owner.Value == StoneColor.White && piece.color == PieceColor.White) return true;
        if (owner.Value == StoneColor.Black && piece.color == PieceColor.Black) return true;
        return false;
    }
}
