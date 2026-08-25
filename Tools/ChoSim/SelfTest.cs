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

            Console.WriteLine();
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
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
