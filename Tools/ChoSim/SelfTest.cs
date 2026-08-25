using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChoSim
{
    /// <summary>
    /// Assertions for the rules changed recently, none of which had any coverage.
    /// Each test drives the real SimRules through its public surface.
    /// </summary>
    public static class SelfTest
    {
        static int _passed, _failed;

        public static int Run()
        {
            _passed = _failed = 0;

            NoProgressDrawFires();
            DrawnPositionEvaluatesToZero();
            SuperkoBlocksARepeat();
            SuicideIsLegalAndRemovesTheGroup();
            SuicideCompletesSurroundBeforeDying();
            MctsFindsWinBySameSidePath();
            MctsFindsWinAcrossHandover();
            MctsBeatsRandom();
            FeaturesMirrorInvariance();
            FeaturesCornerGeometry();
            FeaturesPlaneCounts();
            FeaturesMoveIndexRoundTrip();
            PositionCodecRoundTrip();
            QueenChainsOrthogonally();

            Console.WriteLine();
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ MCTS

        /// <summary>
        /// Winning with a chess move: the capture happens on a node with the SAME player to
        /// move as the root, so the value must NOT be negated on the way back up.
        /// </summary>
        static void MctsFindsWinBySameSidePath()
        {
            Console.WriteLine("MCTS: win without a handover");

            var s = new SimState(5, 6);
            s.currentPlayer = PieceColor.White;
            s.phaseOne = true;
            s.blackInitialStonePending = false;
            s.positionHistory = new SimPositionHistory();

            s.squares[0, 0] = new SimPiece { color = PieceColor.White, type = PieceType.Rook };
            s.squares[4, 0] = new SimPiece { color = PieceColor.White, type = PieceType.King };
            s.squares[0, 5] = new SimPiece { color = PieceColor.Black, type = PieceType.King };
            s.whiteKingSquare = new Vector2Int(4, 0);
            s.blackKingSquare = new Vector2Int(0, 5);

            var move = SimMcts.Search(s, new SimMcts.Config { simulations = 300, seed = 1 });

            bool takesKing = move.chessMove.HasValue
                          && move.chessMove.Value.from == new Vector2Int(0, 0)
                          && move.chessMove.Value.to == new Vector2Int(0, 5);

            Check("plays the rook capture", takesKing, Positions.DescribeTurn(move));
        }

        /// <summary>
        /// Winning with a stone: the main stone hands the turn over, so the terminal value is
        /// scored from the LOSER's perspective and must be negated back to the root. If the
        /// handover sign were wrong, the root would read this winning move as a loss and avoid it.
        /// </summary>
        static void MctsFindsWinAcrossHandover()
        {
            Console.WriteLine("MCTS: win across a handover");

            var s = new SimState(5, 6);
            s.currentPlayer = PieceColor.White;
            s.phaseOne = false;               // main-stone decision
            s.blackInitialStonePending = false;
            s.positionHistory = new SimPositionHistory();

            s.squares[0, 0] = new SimPiece { color = PieceColor.White, type = PieceType.King };
            s.squares[2, 2] = new SimPiece { color = PieceColor.Black, type = PieceType.King };
            s.whiteKingSquare = new Vector2Int(0, 0);
            s.blackKingSquare = new Vector2Int(2, 2);

            // Three of the four corners of square (2,2); (3,3) completes the surround.
            s.stones[2, 2] = SimStoneColor.White;
            s.stones[3, 2] = SimStoneColor.White;
            s.stones[2, 3] = SimStoneColor.White;

            var move = SimMcts.Search(s, new SimMcts.Config { simulations = 300, seed = 1 });

            bool completes = move.mainStone.HasValue
                          && move.mainStone.Value.intersection == new Vector2Int(3, 3);

            Check("plays the surrounding stone", completes, Positions.DescribeTurn(move));

            // And confirm the position really is winning, so the test cannot pass vacuously.
            var after = s.DeepCopy();
            SimRules.ApplyFullTurn(after, new SimTurn
            {
                mainStone = new SimStonePlacement
                {
                    intersection = new Vector2Int(3, 3),
                    color = SimStoneColor.White
                }
            });
            Check("that stone does win", after.gameOver && after.winner == PieceColor.White,
                  $"gameOver={after.gameOver} winner={(after.winner.HasValue ? after.winner.ToString() : "null")}");
        }

        static void MctsBeatsRandom()
        {
            Console.WriteLine("MCTS: strength floor");

            int wins = 0, played = 0;
            for (int g = 0; g < 4; g++)
            {
                var mcts = new MctsAgent(new AgentConfig { Name = "mcts", Depth = 120 }, seed: g);
                var rand = new RandomAgent(1000 + g);

                bool mctsIsWhite = (g % 2) == 0;
                var r = Driver.PlayGame(mctsIsWhite ? mcts : rand,
                                        mctsIsWhite ? rand : mcts,
                                        maxTurns: 120, randomOpeningDecisions: 0, seed: g,
                                        variant: Variant.Small);
                played++;
                if ((r.Outcome == GameOutcome.WhiteWins && mctsIsWhite) ||
                    (r.Outcome == GameOutcome.BlackWins && !mctsIsWhite)) wins++;
            }

            Check("wins at least 3 of 4 against random", wins >= 3, $"won {wins}/{played}");
        }

        // -------------------------------------------------------- features

        /// <summary>
        /// Builds the colour-swapped, vertically-flipped twin of a position. Written here
        /// independently of SimFeatures' own flip so the invariance test cannot be circular.
        /// </summary>
        static SimState Mirror(SimState s)
        {
            var m = new SimState(s.boardWidth, s.boardHeight);

            for (int x = 0; x < s.boardWidth; x++)
                for (int y = 0; y < s.boardHeight; y++)
                {
                    var sp = s.squares[x, y];
                    if (!sp.HasValue) continue;
                    var p = sp.Value;
                    p.color = p.color == PieceColor.White ? PieceColor.Black : PieceColor.White;
                    m.squares[x, s.boardHeight - 1 - y] = p;
                }

            for (int ix = 0; ix < s.intersectionWidth; ix++)
                for (int iy = 0; iy < s.intersectionHeight; iy++)
                {
                    var c = s.stones[ix, iy];
                    if (c == SimStoneColor.None) continue;
                    m.stones[ix, s.intersectionHeight - 1 - iy] =
                        c == SimStoneColor.White ? SimStoneColor.Black : SimStoneColor.White;
                }

            m.currentPlayer = s.currentPlayer == PieceColor.White ? PieceColor.Black : PieceColor.White;
            m.phaseOne = s.phaseOne;
            m.waitingForTerritoryClick = s.waitingForTerritoryClick;
            m.waitingForPawnStoneChoice = s.waitingForPawnStoneChoice;
            m.noProgressTurns = s.noProgressTurns;

            if (s.goKoPoint.HasValue)
                m.goKoPoint = new Vector2Int(s.goKoPoint.Value.x,
                                             s.intersectionHeight - 1 - s.goKoPoint.Value.y);
            if (s.lastMovedSquare.HasValue)
                m.lastMovedSquare = new Vector2Int(s.lastMovedSquare.Value.x,
                                                   s.boardHeight - 1 - s.lastMovedSquare.Value.y);
            return m;
        }

        static SimState SamplePosition(int plies, int seed)
        {
            var s = Positions.Create(Variant.Small);
            var rng = new RandomAgent(seed);
            for (int i = 0; i < plies && !s.gameOver; i++) Driver.Step(s, rng, out _);
            return s;
        }

        static void FeaturesMirrorInvariance()
        {
            Console.WriteLine("features: canonicalisation");

            int mismatches = 0, tested = 0;
            for (int seed = 1; seed <= 6; seed++)
            {
                var a = SamplePosition(9 + seed, seed);
                var b = Mirror(a);

                var pa = new float[SimFeatures.TensorSize(a)];
                var pb = new float[SimFeatures.TensorSize(b)];
                SimFeatures.Encode(a, pa);
                SimFeatures.Encode(b, pb);

                tested++;
                for (int i = 0; i < pa.Length; i++)
                    if (Math.Abs(pa[i] - pb[i]) > 1e-6f) { mismatches++; break; }
            }

            Check("a position and its mirror encode identically", mismatches == 0,
                  $"{mismatches}/{tested} positions differed");
        }

        static void FeaturesCornerGeometry()
        {
            Console.WriteLine("features: 2x2 corner geometry");

            // Black to move, so the flip is exercised rather than bypassed.
            var s = new SimState(5, 6);
            s.currentPlayer = PieceColor.Black;
            s.phaseOne = true;
            s.squares[2, 2] = new SimPiece { color = PieceColor.White, type = PieceType.Queen };
            s.stones[2, 2] = SimStoneColor.Black;
            s.stones[3, 2] = SimStoneColor.Black;
            s.stones[2, 3] = SimStoneColor.Black;
            s.stones[3, 3] = SimStoneColor.Black;

            int W = s.intersectionWidth, H = s.intersectionHeight, stride = H * W;
            var planes = new float[SimFeatures.TensorSize(s)];
            SimFeatures.Encode(s, planes);

            float At(int plane, int cx, int cy) => planes[plane * stride + cy * W + cx];

            SimFeatures.MapSquare(s, 2, 2, out int qx, out int qy);

            // The queen belongs to the opponent (Black is to move).
            Check("piece lands on its canonical cell",
                  At(SimFeatures.OppPieces + (int)PieceType.Queen, qx, qy) == 1f);

            // Its four corners are exactly the 2x2 block anchored at that same cell.
            bool block = At(SimFeatures.OwnStones, qx,     qy)     == 1f
                      && At(SimFeatures.OwnStones, qx + 1, qy)     == 1f
                      && At(SimFeatures.OwnStones, qx,     qy + 1) == 1f
                      && At(SimFeatures.OwnStones, qx + 1, qy + 1) == 1f;
            Check("its four corners form the 2x2 block above it", block);

            // Four corners means the surround is complete, so no partial-pressure plane fires.
            bool noPartial = At(SimFeatures.SurroundPress + 0, qx, qy) == 0f
                          && At(SimFeatures.SurroundPress + 1, qx, qy) == 0f
                          && At(SimFeatures.SurroundPress + 2, qx, qy) == 0f;
            Check("a complete surround sets no partial-pressure plane", noPartial);
            Check("the square reads as opponent territory",
                  At(SimFeatures.OwnTerritory, qx, qy) == 1f);
        }

        static void FeaturesPlaneCounts()
        {
            Console.WriteLine("features: plane totals match the board");

            var s = SamplePosition(15, 4);
            int W = s.intersectionWidth, H = s.intersectionHeight, stride = H * W;
            var planes = new float[SimFeatures.TensorSize(s)];
            SimFeatures.Encode(s, planes);

            float Sum(int plane)
            {
                float t = 0;
                for (int i = 0; i < stride; i++) t += planes[plane * stride + i];
                return t;
            }

            int ownPieces = 0, oppPieces = 0, ownStones = 0, oppStones = 0;
            var ownStoneColor = s.currentPlayer == PieceColor.White ? SimStoneColor.White : SimStoneColor.Black;

            for (int x = 0; x < s.boardWidth; x++)
                for (int y = 0; y < s.boardHeight; y++)
                {
                    var sp = s.squares[x, y];
                    if (!sp.HasValue) continue;
                    if (sp.Value.color == s.currentPlayer) ownPieces++; else oppPieces++;
                }

            for (int ix = 0; ix < W; ix++)
                for (int iy = 0; iy < H; iy++)
                {
                    var c = s.stones[ix, iy];
                    if (c == SimStoneColor.None) continue;
                    if (c == ownStoneColor) ownStones++; else oppStones++;
                }

            float ownPieceSum = 0, oppPieceSum = 0;
            for (int t = 0; t < 6; t++)
            {
                ownPieceSum += Sum(SimFeatures.OwnPieces + t);
                oppPieceSum += Sum(SimFeatures.OppPieces + t);
            }

            Check("own piece planes total the own pieces", ownPieceSum == ownPieces, $"{ownPieceSum} vs {ownPieces}");
            Check("opponent piece planes total theirs", oppPieceSum == oppPieces, $"{oppPieceSum} vs {oppPieces}");
            Check("own stone plane totals the own stones", Sum(SimFeatures.OwnStones) == ownStones, $"{Sum(SimFeatures.OwnStones)} vs {ownStones}");
            Check("opponent stone plane totals theirs", Sum(SimFeatures.OppStones) == oppStones, $"{Sum(SimFeatures.OppStones)} vs {oppStones}");

            // Every stone belongs to exactly one liberty bucket.
            float ownLib = Sum(SimFeatures.OwnLiberties) + Sum(SimFeatures.OwnLiberties + 1) + Sum(SimFeatures.OwnLiberties + 2);
            Check("every own stone lands in one liberty bucket", ownLib == ownStones, $"{ownLib} vs {ownStones}");

            // Exactly one phase plane is on.
            float phases = Sum(SimFeatures.PhaseChess) + Sum(SimFeatures.PhaseTerritory)
                         + Sum(SimFeatures.PhaseBonus) + Sum(SimFeatures.PhaseMainStone);
            Check("exactly one phase plane is set", Math.Abs(phases - stride) < 1e-4f, $"{phases} vs {stride}");
        }

        static void FeaturesMoveIndexRoundTrip()
        {
            Console.WriteLine("features: policy index round-trip");

            int checkedMoves = 0, bad = 0;

            for (int seed = 1; seed <= 8; seed++)
            {
                var s = SamplePosition(7 + seed, seed);
                if (s.gameOver) continue;

                var moves = SimRules.GenerateAllLegalFullTurns(s);
                foreach (var m in moves)
                {
                    int idx = SimFeatures.PolicyIndex(s, m);
                    if (idx < 0) { bad++; continue; }
                    checkedMoves++;

                    if (m.chessMove.HasValue)
                    {
                        int squares = s.boardWidth * s.boardHeight;
                        var from = SimFeatures.SquareFromIndex(s, idx / squares);
                        var to = SimFeatures.SquareFromIndex(s, idx % squares);
                        if (from != m.chessMove.Value.from || to != m.chessMove.Value.to) bad++;
                        if (idx >= SimFeatures.ChessPolicySize(s)) bad++;
                    }
                    else
                    {
                        var pt = m.mainStone.HasValue ? m.mainStone.Value.intersection
                               : m.bonusPawnStone.HasValue ? m.bonusPawnStone.Value.intersection
                               : m.territoryRemoval.Value.intersection;
                        if (SimFeatures.IntersectionFromIndex(s, idx) != pt) bad++;
                        if (idx >= SimFeatures.IntersectionPolicySize(s)) bad++;
                    }
                }
            }

            Check("every legal move round-trips through its index", bad == 0 && checkedMoves > 100,
                  $"{bad} bad out of {checkedMoves} moves");
        }

        static void PositionCodecRoundTrip()
        {
            Console.WriteLine("codec: position round-trip");

            int tested = 0, fieldMismatch = 0, moveMismatch = 0, planeMismatch = 0;

            for (int seed = 1; seed <= 10; seed++)
            {
                var a = SamplePosition(6 + seed * 2, seed);
                var b = PositionCodec.Decode(PositionCodec.Encode(a));
                tested++;

                bool same = a.boardWidth == b.boardWidth && a.boardHeight == b.boardHeight
                         && a.currentPlayer == b.currentPlayer
                         && a.phaseOne == b.phaseOne
                         && a.waitingForTerritoryClick == b.waitingForTerritoryClick
                         && a.waitingForPawnStoneChoice == b.waitingForPawnStoneChoice
                         && a.goKoPoint == b.goKoPoint
                         && a.lastMovedSquare == b.lastMovedSquare
                         && a.noProgressTurns == b.noProgressTurns
                         && a.whiteCanCastleKingSide == b.whiteCanCastleKingSide
                         && a.whiteCanCastleQueenSide == b.whiteCanCastleQueenSide
                         && a.blackCanCastleKingSide == b.blackCanCastleKingSide
                         && a.blackCanCastleQueenSide == b.blackCanCastleQueenSide;

                for (int x = 0; x < a.boardWidth && same; x++)
                    for (int y = 0; y < a.boardHeight && same; y++)
                    {
                        var pa = a.squares[x, y];
                        var pb = b.squares[x, y];
                        if (pa.HasValue != pb.HasValue) { same = false; break; }
                        if (!pa.HasValue) continue;
                        if (pa.Value.color != pb.Value.color || pa.Value.type != pb.Value.type
                            || pa.Value.hasMoved != pb.Value.hasMoved
                            || pa.Value.justDoubleStepped != pb.Value.justDoubleStepped) same = false;
                    }

                for (int ix = 0; ix < a.intersectionWidth && same; ix++)
                    for (int iy = 0; iy < a.intersectionHeight && same; iy++)
                        if (a.stones[ix, iy] != b.stones[ix, iy]) { same = false; break; }

                if (!same) { fieldMismatch++; continue; }

                // Everything the generator reads must survive, or the policy targets would be
                // attached to a different move list than the trainer reconstructs.
                var ma = SimRules.GenerateAllLegalFullTurns(a, applySuperko: false);
                var mb = SimRules.GenerateAllLegalFullTurns(b, applySuperko: false);
                if (ma.Count != mb.Count) { moveMismatch++; continue; }

                var ta = new float[SimFeatures.TensorSize(a)];
                var tb = new float[SimFeatures.TensorSize(b)];
                SimFeatures.Encode(a, ta);
                SimFeatures.Encode(b, tb);
                for (int i = 0; i < ta.Length; i++)
                    if (Math.Abs(ta[i] - tb[i]) > 1e-6f) { planeMismatch++; break; }
            }

            Check("every field survives encode/decode", fieldMismatch == 0, $"{fieldMismatch}/{tested}");
            Check("legal moves are identical after a round-trip", moveMismatch == 0, $"{moveMismatch}/{tested}");
            Check("planes are identical after a round-trip", planeMismatch == 0, $"{planeMismatch}/{tested}");
        }

        /// <summary>
        /// A queen chaining through her own territory must keep all eight directions.
        /// The destination here is reachable only by turning a corner mid-chain, so it is
        /// absent unless the orthogonal slides are present.
        /// </summary>
        static void QueenChainsOrthogonally()
        {
            Console.WriteLine("queen chains in all eight directions");

            var s = new SimState(5, 6);
            s.currentPlayer = PieceColor.White;
            s.phaseOne = true;
            s.blackInitialStonePending = false;

            s.squares[0, 0] = new SimPiece { color = PieceColor.White, type = PieceType.Queen };
            s.squares[4, 0] = new SimPiece { color = PieceColor.White, type = PieceType.King };
            s.squares[4, 5] = new SimPiece { color = PieceColor.Black, type = PieceType.King };
            s.whiteKingSquare = new Vector2Int(4, 0);
            s.blackKingSquare = new Vector2Int(4, 5);

            // Own territory over squares (0,1), (1,1) and (2,1): every corner of each.
            foreach (var pt in new[]
                     {
                         new Vector2Int(0,1), new Vector2Int(1,1), new Vector2Int(0,2), new Vector2Int(1,2),
                         new Vector2Int(2,1), new Vector2Int(2,2), new Vector2Int(3,1), new Vector2Int(3,2)
                     })
                s.stones[pt.x, pt.y] = SimStoneColor.White;

            var moves = SimRules.GenerateAllLegalFullTurns(s);

            bool Has(int fx, int fy, int tx, int ty)
            {
                foreach (var m in moves)
                    if (m.chessMove.HasValue
                        && m.chessMove.Value.from == new Vector2Int(fx, fy)
                        && m.chessMove.Value.to == new Vector2Int(tx, ty)) return true;
                return false;
            }

            // (2,1) is not on any queen line from (0,0), so it can only come from a chain that
            // steps up to (0,1) and then slides sideways.
            Check("reaches a square only a cornering chain can reach", Has(0, 0, 2, 1));

            // Sanity: the ordinary first hop is still there.
            Check("still offers the plain first hop", Has(0, 0, 0, 1));
        }

        static void Check(string name, bool ok, string detail = "")
        {
            if (ok) { _passed++; Console.WriteLine($"  PASS  {name}"); }
            else    { _failed++; Console.WriteLine($"  FAIL  {name}{(detail.Length > 0 ? "  -- " + detail : "")}"); }
        }

        /// <summary>Plays decisions until the turn hands over. Always takes the first legal
        /// decision, so it is deterministic.</summary>
        static void PlayOneTurn(SimState s)
        {
            var start = s.currentPlayer;
            for (int guard = 0; guard < 12 && s.currentPlayer == start && !s.gameOver; guard++)
            {
                var moves = SimRules.GenerateAllLegalFullTurns(s);
                if (moves == null || moves.Count == 0) break;
                SimRules.ApplyFullTurn(s, moves[0]);
            }
        }

        // ---------------------------------------------------------------- 1

        static void NoProgressDrawFires()
        {
            Console.WriteLine("no-progress draw");

            int savedLimit = SimRules.noProgressTurnLimit;
            try
            {
                SimRules.noProgressTurnLimit = 4;

                // Learn what White's counts look like after one turn...
                var probe = Positions.Create(Variant.Small);
                PlayOneTurn(probe);
                var afterWhite = SimRules.CountProgressMaterial(probe);

                // ...then start again, claiming those were already the counts last time round.
                // The next White turn therefore registers as "no progress".
                var s = Positions.Create(Variant.Small);
                s.progressAfterWhiteTurn = afterWhite;
                s.noProgressTurns = SimRules.noProgressTurnLimit - 1;

                PlayOneTurn(s);

                Check("draw declared at the limit", s.gameOver && !s.winner.HasValue,
                      $"gameOver={s.gameOver} winner={(s.winner.HasValue ? s.winner.ToString() : "null")} counter={s.noProgressTurns}");
                Check("counter reached the limit", s.noProgressTurns >= SimRules.noProgressTurnLimit,
                      $"counter={s.noProgressTurns}");

                // And the counter must reset when something actually changes.
                var t = Positions.Create(Variant.Small);
                t.noProgressTurns = 3;
                PlayOneTurn(t);   // opening turn places a stone, so counts move
                Check("counter resets on progress", t.noProgressTurns == 0, $"counter={t.noProgressTurns}");
            }
            finally { SimRules.noProgressTurnLimit = savedLimit; }
        }

        // ---------------------------------------------------------------- 2

        static void DrawnPositionEvaluatesToZero()
        {
            Console.WriteLine("drawn position scores 0");

            var s = Positions.Create(Variant.Small);
            int beforeScore = SimRules.Evaluate(s);

            s.gameOver = true;
            s.winner = null;

            Check("Evaluate returns 0 for a draw", SimRules.Evaluate(s) == 0, $"got {SimRules.Evaluate(s)}");
            Check("non-drawn position is unaffected", beforeScore == SimRules.Evaluate(Positions.Create(Variant.Small)));
        }

        // ---------------------------------------------------------------- 3

        static void SuperkoBlocksARepeat()
        {
            Console.WriteLine("superko restriction");

            var s = Positions.Create(Variant.Small);

            // Superko only governs the main stone, so step past the chess move and any
            // territory-removal or pawn-bonus sub-decisions until we are actually on one.
            bool atMainStone = false;
            for (int guard = 0; guard < 40 && !s.gameOver; guard++)
            {
                if (!s.phaseOne && !s.waitingForTerritoryClick && !s.waitingForPawnStoneChoice)
                {
                    atMainStone = true;
                    break;
                }
                var mv = SimRules.GenerateAllLegalFullTurns(s);
                if (mv == null || mv.Count == 0) break;
                SimRules.ApplyFullTurn(s, mv[0]);
            }

            // Assert this explicitly: without it the checks below compare null mainStone
            // fields and pass vacuously.
            Check("reached a main-stone node", atMainStone);
            if (!atMainStone) return;

            var before = SimRules.GenerateAllLegalFullTurns(s, applySuperko: true);
            if (before == null || before.Count < 2) { Check("main-stone node offers choices", false); return; }
            Check("candidates are main stones", before[0].mainStone.HasValue);

            // Poison exactly one candidate by recording the position it would produce.
            var victim = before[0];
            var after = s.DeepCopy();
            after.positionHistory = null;
            SimRules.ApplyFullTurn(after, victim);
            s.positionHistory.Push(SimZobrist.ComputeBoardHash(after));

            var now = SimRules.GenerateAllLegalFullTurns(s, applySuperko: true);

            bool stillThere = false;
            foreach (var t in now)
                if (t.mainStone.HasValue && victim.mainStone.HasValue &&
                    t.mainStone.Value.intersection == victim.mainStone.Value.intersection)
                    stillThere = true;

            Check("repeating placement is filtered out", !stillThere);
            Check("exactly one candidate removed", now.Count == before.Count - 1,
                  $"before={before.Count} after={now.Count}");

            // With superko off the same generator must still offer it.
            var unfiltered = SimRules.GenerateAllLegalFullTurns(s, applySuperko: false);
            Check("still legal when superko is off", unfiltered.Count == before.Count,
                  $"before={before.Count} unfiltered={unfiltered.Count}");
        }

        // ---------------------------------------------------------------- 4

        static SimState EmptyGoPosition()
        {
            var s = new SimState(5, 6);
            s.currentPlayer = PieceColor.Black;
            s.phaseOne = false;             // straight to the main-stone decision
            s.blackInitialStonePending = false;
            s.positionHistory = new SimPositionHistory();
            return s;
        }

        static void ApplyMainStone(SimState s, int ix, int iy, SimStoneColor c)
        {
            SimRules.ApplyFullTurn(s, new SimTurn
            {
                mainStone = new SimStonePlacement { intersection = new Vector2Int(ix, iy), color = c }
            });
        }

        static void SuicideIsLegalAndRemovesTheGroup()
        {
            Console.WriteLine("suicide is legal");

            var s = EmptyGoPosition();

            // Box in (1,1) with white stones, each of which keeps liberties elsewhere.
            s.stones[1, 0] = SimStoneColor.White;
            s.stones[0, 1] = SimStoneColor.White;
            s.stones[2, 1] = SimStoneColor.White;
            s.stones[1, 2] = SimStoneColor.White;

            var moves = SimRules.GenerateAllLegalFullTurns(s);
            bool offered = false;
            foreach (var m in moves)
                if (m.mainStone.HasValue && m.mainStone.Value.intersection == new Vector2Int(1, 1))
                    offered = true;

            Check("suicidal point is offered as legal", offered);

            ApplyMainStone(s, 1, 1, SimStoneColor.Black);
            Check("the stone dies immediately", s.stones[1, 1] == SimStoneColor.None,
                  $"got {s.stones[1, 1]}");
            Check("surrounding stones survive", s.stones[1, 0] == SimStoneColor.White);
        }

        // ---------------------------------------------------------------- 5

        static void SuicideCompletesSurroundBeforeDying()
        {
            Console.WriteLine("suicide completes a surround on the way out");

            var s = EmptyGoPosition();

            // A white piece on square (0,0), whose corners are (0,0) (1,0) (0,1) (1,1).
            s.squares[0, 0] = new SimPiece { color = PieceColor.White, type = PieceType.Queen };

            // Black holds three of them.
            s.stones[0, 0] = SimStoneColor.Black;
            s.stones[1, 0] = SimStoneColor.Black;
            s.stones[0, 1] = SimStoneColor.Black;

            // White stones seal every liberty of that black group, so Black playing the
            // fourth corner (1,1) is suicide. Each white stone keeps a liberty of its own,
            // so nothing of White's is captured.
            s.stones[2, 0] = SimStoneColor.White;
            s.stones[2, 1] = SimStoneColor.White;
            s.stones[1, 2] = SimStoneColor.White;
            s.stones[0, 2] = SimStoneColor.White;

            ApplyMainStone(s, 1, 1, SimStoneColor.Black);

            Check("the surrounded piece is captured", !s.squares[0, 0].HasValue,
                  s.squares[0, 0].HasValue ? $"piece still there: {s.squares[0, 0].Value.type}" : "");
            Check("the suicidal group is then removed",
                  s.stones[1, 1] == SimStoneColor.None && s.stones[0, 0] == SimStoneColor.None &&
                  s.stones[1, 0] == SimStoneColor.None && s.stones[0, 1] == SimStoneColor.None,
                  $"({s.stones[0,0]},{s.stones[1,0]},{s.stones[0,1]},{s.stones[1,1]})");
            Check("White's stones are untouched", s.stones[2, 0] == SimStoneColor.White);
        }
    }
}
