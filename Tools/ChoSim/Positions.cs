using System;
using System.Text;
using UnityEngine;

namespace ChoSim
{
    /// <summary>
    /// Position construction and rendering for the headless harness.
    /// Mirrors BoardManager.SetupPieces (White on ranks 0-1, Black on ranks 6-7).
    /// </summary>
    public static class Positions
    {
        public const int BoardSize = 8;
        public const int IntersectionSize = 9;

        public static SimState StartPosition()
        {
            var s = new SimState(BoardSize, IntersectionSize);

            var backRank = new[]
            {
                PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
                PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook
            };

            for (int x = 0; x < BoardSize; x++)
            {
                Put(s, PieceColor.White, backRank[x], x, 0);
                Put(s, PieceColor.White, PieceType.Pawn, x, 1);
                Put(s, PieceColor.Black, backRank[x], x, 7);
                Put(s, PieceColor.Black, PieceType.Pawn, x, 6);
            }

            s.whiteKingSquare = new Vector2Int(4, 0);
            s.blackKingSquare = new Vector2Int(4, 7);

            s.currentPlayer = PieceColor.White;
            s.phaseOne = true;

            // The live game's opening black-stone rule is not modelled by SimRules
            // (ApplyFullTurn early-returns on isInitialBlackStoneTurn and the generator
            // never emits one), so the harness starts past it to match what the search
            // actually reasons about.
            s.blackInitialStonePending = false;

            // Superko needs somewhere to record positions. Seed it with the opening position so
            // a cycle back to the start is caught too.
            s.positionHistory = new SimPositionHistory();
            s.positionHistory.Push(SimZobrist.ComputeBoardHash(s));

            return s;
        }

        static void Put(SimState s, PieceColor color, PieceType type, int x, int y)
        {
            s.squares[x, y] = new SimPiece
            {
                color = color,
                type = type,
                hasMoved = false,
                justDoubleStepped = false
            };
        }

        public static char PieceGlyph(SimPiece p)
        {
            char c;
            switch (p.type)
            {
                case PieceType.King:   c = 'k'; break;
                case PieceType.Queen:  c = 'q'; break;
                case PieceType.Rook:   c = 'r'; break;
                case PieceType.Bishop: c = 'b'; break;
                case PieceType.Knight: c = 'n'; break;
                default:               c = 'p'; break;
            }
            return p.color == PieceColor.White ? char.ToUpperInvariant(c) : c;
        }

        public static char StoneGlyph(SimStoneColor c)
        {
            switch (c)
            {
                case SimStoneColor.White: return 'O';
                case SimStoneColor.Black: return '@';
                default: return '+';
            }
        }

        /// <summary>
        /// Renders chess squares and Go intersections together.
        /// Intersections ('+' empty, 'O' white stone, '@' black stone) sit on square corners;
        /// pieces (uppercase = White) sit between them.
        /// </summary>
        public static string Render(SimState s)
        {
            var sb = new StringBuilder();

            for (int iy = s.intersectionSize - 1; iy >= 0; iy--)
            {
                // Intersection row iy
                sb.Append(iy.ToString().PadLeft(2)).Append(' ');
                for (int ix = 0; ix < s.intersectionSize; ix++)
                {
                    sb.Append(StoneGlyph(s.stones[ix, iy]));
                    if (ix < s.intersectionSize - 1) sb.Append("---");
                }
                sb.AppendLine();

                // Piece row for square rank (iy-1), drawn between intersection rows
                int rank = iy - 1;
                if (rank >= 0)
                {
                    sb.Append("   ");
                    for (int x = 0; x < s.boardSize; x++)
                    {
                        var sp = s.squares[x, rank];
                        sb.Append('|').Append(' ')
                          .Append(sp.HasValue ? PieceGlyph(sp.Value) : '.')
                          .Append(' ');
                    }
                    sb.Append('|').Append("   rank ").Append(rank);
                    sb.AppendLine();
                }
            }

            sb.Append("   ");
            for (int ix = 0; ix < s.intersectionSize; ix++)
            {
                sb.Append(ix);
                if (ix < s.intersectionSize - 1) sb.Append("   ");
            }
            sb.AppendLine("   (intersection x)");

            sb.Append("  turn=").Append(s.currentPlayer)
              .Append(" phase=").Append(s.phaseOne ? "1-chess" : "2-go");
            if (s.waitingForTerritoryClick) sb.Append(" [territory-removal]");
            if (s.waitingForPawnStoneChoice) sb.Append(" [pawn-bonus-stone]");
            if (s.goKoPoint.HasValue) sb.Append(" ko=").Append(s.goKoPoint.Value);
            if (s.gameOver) sb.Append(" GAME OVER winner=").Append(s.winner.HasValue ? s.winner.Value.ToString() : "draw");
            sb.AppendLine();

            return sb.ToString();
        }

        public static string DescribeTurn(SimTurn t)
        {
            if (t.chessMove.HasValue)
            {
                var m = t.chessMove.Value;
                string promo = m.promotion.HasValue ? "=" + m.promotion.Value : "";
                return $"chess {Sq(m.from)}-{Sq(m.to)}{promo}";
            }
            if (t.territoryRemoval.HasValue) return $"remove {t.territoryRemoval.Value.intersection}";
            if (t.bonusPawnStone.HasValue) return $"bonus {t.bonusPawnStone.Value.intersection}";
            if (t.mainStone.HasValue) return $"stone {t.mainStone.Value.intersection}";
            return "(none)";
        }

        static string Sq(Vector2Int v) => $"{(char)('a' + v.x)}{v.y + 1}";
    }
}
