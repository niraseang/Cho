using System;
using System.Text;
using UnityEngine;

namespace ChoSim
{
    public enum Variant
    {
        /// <summary>8x8 squares, full chess army. The shipping game.</summary>
        Standard,

        /// <summary>
        /// 5x6 squares (6x7 intersections), one of each non-pawn piece plus five pawns.
        /// Pawns move one square only, and there is no castling. Sized for neural-net
        /// experiments, where the point is to validate the pipeline quickly.
        /// </summary>
        Small
    }

    public static class Positions
    {
        public static SimState Create(Variant variant)
        {
            ApplyVariantRules(variant);
            return variant == Variant.Small ? SmallPosition() : StartPosition();
        }

        /// <summary>
        /// Variant rules live on SimRules as statics, so they must be re-applied whenever the
        /// variant changes within a process.
        /// </summary>
        public static void ApplyVariantRules(Variant variant)
        {
            bool small = variant == Variant.Small;
            SimRules.pawnsMayDoubleStep = !small;
            SimRules.castlingEnabled = !small;

            // R N B Q K puts the king on the last file, not the middle one.
            SimRules.kingStartFile = small ? 4 : -1;
        }

        public static SimState StartPosition()
        {
            var backRank = new[]
            {
                PieceType.Rook, PieceType.Knight, PieceType.Bishop, PieceType.Queen,
                PieceType.King, PieceType.Bishop, PieceType.Knight, PieceType.Rook
            };
            return Build(8, 8, backRank);
        }

        public static SimState SmallPosition()
        {
            // One of each non-pawn piece fills a five-wide back rank exactly.
            // Same arrangement as Gardner's Minichess.
            var backRank = new[]
            {
                PieceType.Rook, PieceType.Knight, PieceType.Bishop,
                PieceType.Queen, PieceType.King
            };
            return Build(5, 6, backRank);
        }

        static SimState Build(int width, int height, PieceType[] backRank)
        {
            if (backRank.Length != width)
                throw new ArgumentException($"back rank has {backRank.Length} pieces for a {width}-wide board");

            var s = new SimState(width, height);

            for (int x = 0; x < width; x++)
            {
                Put(s, PieceColor.White, backRank[x], x, 0);
                Put(s, PieceColor.White, PieceType.Pawn, x, 1);
                Put(s, PieceColor.Black, PieceType.Pawn, x, height - 2);
                Put(s, PieceColor.Black, backRank[x], x, height - 1);

                if (backRank[x] == PieceType.King)
                {
                    s.whiteKingSquare = new Vector2Int(x, 0);
                    s.blackKingSquare = new Vector2Int(x, height - 1);
                }
            }

            s.currentPlayer = PieceColor.White;
            s.phaseOne = true;

            // The live game's opening black-stone rule is not modelled by SimRules, so the
            // harness starts past it to match what the search actually reasons about.
            s.blackInitialStonePending = false;

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
        /// Renders squares and intersections together. Intersections ('+' empty, 'O' white,
        /// '@' black) sit on square corners; pieces (uppercase = White) sit between them.
        /// </summary>
        public static string Render(SimState s)
        {
            var sb = new StringBuilder();

            for (int iy = s.intersectionHeight - 1; iy >= 0; iy--)
            {
                sb.Append(iy.ToString().PadLeft(2)).Append(' ');
                for (int ix = 0; ix < s.intersectionWidth; ix++)
                {
                    sb.Append(StoneGlyph(s.stones[ix, iy]));
                    if (ix < s.intersectionWidth - 1) sb.Append("---");
                }
                sb.AppendLine();

                int rank = iy - 1;
                if (rank >= 0)
                {
                    sb.Append("   ");
                    for (int x = 0; x < s.boardWidth; x++)
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
            for (int ix = 0; ix < s.intersectionWidth; ix++)
            {
                sb.Append(ix);
                if (ix < s.intersectionWidth - 1) sb.Append("   ");
            }
            sb.AppendLine("   (intersection x)");

            sb.Append("  ").Append(s.boardWidth).Append('x').Append(s.boardHeight)
              .Append(" squares, ").Append(s.intersectionWidth).Append('x').Append(s.intersectionHeight)
              .Append(" intersections");
            sb.AppendLine();

            sb.Append("  turn=").Append(s.currentPlayer)
              .Append(" phase=").Append(s.phaseOne ? "1-chess" : "2-go");
            if (s.waitingForTerritoryClick) sb.Append(" [territory-removal]");
            if (s.waitingForPawnStoneChoice) sb.Append(" [pawn-bonus-stone]");
            if (s.goKoPoint.HasValue) sb.Append(" ko=").Append(s.goKoPoint.Value);
            if (s.noProgressTurns > 0) sb.Append(" noProgress=").Append(s.noProgressTurns);
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
