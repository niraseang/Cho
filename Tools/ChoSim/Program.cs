using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ChoSim
{
    public static class Program
    {
        public static int Main(string[] rawArgs)
        {
            var args = new Args(rawArgs);
            string cmd = rawArgs.Length > 0 && !rawArgs[0].StartsWith("--") ? rawArgs[0] : "help";

            switch (cmd)
            {
                case "show":      Show(args); return 0;
                case "branching": Branching(args); return 0;
                case "bench":     Bench(args); return 0;
                case "match":     Match(args); return 0;
                case "profile":   Profile(args); return 0;
                default:          Help(); return 0;
            }
        }

        static void Help()
        {
            Console.WriteLine(@"ChoSim - headless harness for the Cho (chess+go) engine.

  show       [--plies N] [--seed S]
             Print a position (after N random decisions).

  branching  [--games G] [--turns T] [--seed S]
             Measure the real branching factor at each kind of decision node,
             and what a naive combined chess+stone turn would have cost.

  bench      [--depth D] [--plies N] [--time MS] [--seed S] [--agent legacy|search]
             Search from a position at depths 1..D. Reports nodes, nodes/sec, best move.

  profile    [--plies N] [--seed S] [--iters I]
             Time the hot functions in isolation, with allocation counts.

  match      [--a SPEC] [--b SPEC] [--games G] [--turns T] [--seed S] [--openings N]
             Self-play A/B. SPEC is kind:depth, kind in {search, legacy, random}.
             Colors are swapped every game. Example: --a legacy:4 --b search:4");
        }

        // ---------------------------------------------------------------- show

        static void Show(Args a)
        {
            int plies = a.Int("plies", 0);
            int seed = a.Int("seed", 1);

            var s = Positions.StartPosition();
            var rng = new RandomAgent(seed);
            for (int i = 0; i < plies && !s.gameOver; i++) Driver.Step(s, rng, out _);

            Console.WriteLine(Positions.Render(s));
        }

        // ----------------------------------------------------------- branching

        enum NodeKind { Chess, Territory, Bonus, Stone }

        sealed class Stat
        {
            public readonly List<int> Values = new List<int>();
            public void Add(int v) => Values.Add(v);
            public int Count => Values.Count;
            public double Mean => Values.Count == 0 ? 0 : Values.Average();
            public int Max => Values.Count == 0 ? 0 : Values.Max();
            public int Min => Values.Count == 0 ? 0 : Values.Min();
        }

        static NodeKind KindOf(SimState s)
        {
            if (s.phaseOne) return NodeKind.Chess;
            if (s.waitingForTerritoryClick) return NodeKind.Territory;
            if (s.waitingForPawnStoneChoice) return NodeKind.Bonus;
            return NodeKind.Stone;
        }

        static void Branching(Args a)
        {
            int games = a.Int("games", 20);
            int turns = a.Int("turns", 40);
            int seed = a.Int("seed", 1);

            SimRules.useMobilityEval = false;

            var stats = new Dictionary<NodeKind, Stat>();
            foreach (NodeKind k in Enum.GetValues(typeof(NodeKind))) stats[k] = new Stat();

            // Per-turn products, to show what a combined "chess move AND stone" node would cost.
            var turnProducts = new List<long>();
            var earlyProducts = new List<long>();

            for (int g = 0; g < games; g++)
            {
                var s = Positions.StartPosition();
                var rng = new RandomAgent(seed + g);

                int turnsPlayed = 0;
                long pendingChess = 0;
                var prev = s.currentPlayer;

                while (!s.gameOver && turnsPlayed < turns)
                {
                    var kind = KindOf(s);
                    var moves = SimRules.GenerateAllLegalFullTurns(s);
                    int n = moves?.Count ?? 0;
                    stats[kind].Add(n);

                    if (kind == NodeKind.Chess) pendingChess = n;
                    if (kind == NodeKind.Stone && pendingChess > 0)
                    {
                        long product = pendingChess * n;
                        turnProducts.Add(product);
                        if (turnsPlayed < 4) earlyProducts.Add(product);
                        pendingChess = 0;
                    }

                    Driver.Step(s, rng, out _);
                    if (s.currentPlayer != prev) { prev = s.currentPlayer; turnsPlayed++; }
                }
            }

            Console.WriteLine($"Branching factor over {games} random games, first {turns} turns each");
            Console.WriteLine();
            Console.WriteLine($"{"node type",-22}{"nodes",10}{"mean",10}{"min",8}{"max",8}");
            Console.WriteLine(new string('-', 58));
            foreach (NodeKind k in Enum.GetValues(typeof(NodeKind)))
            {
                var st = stats[k];
                Console.WriteLine($"{Label(k),-22}{st.Count,10}{st.Mean,10:F1}{st.Min,8}{st.Max,8}");
            }

            Console.WriteLine();
            if (turnProducts.Count > 0)
            {
                Console.WriteLine("If one node had to encode a whole turn (chess move x stone placement):");
                Console.WriteLine($"  mean combined turn branching : {turnProducts.Average(),10:F0}");
                Console.WriteLine($"  max  combined turn branching : {turnProducts.Max(),10}");
                if (earlyProducts.Count > 0)
                    Console.WriteLine($"  opening (first 4 turns), mean: {earlyProducts.Average(),10:F0}");

                double meanCombined = turnProducts.Average();
                Console.WriteLine();
                Console.WriteLine("  full-width tree size by lookahead (no pruning):");
                for (int rounds = 1; rounds <= 3; rounds++)
                {
                    double combined = Math.Pow(meanCombined, rounds * 2);
                    double split = Math.Pow(stats[NodeKind.Chess].Mean, rounds * 2)
                                 * Math.Pow(stats[NodeKind.Stone].Mean, rounds * 2);
                    Console.WriteLine($"    {rounds} round(s) each side: combined-node {combined:E2}   split-node {split:E2}");
                }
                Console.WriteLine();
                Console.WriteLine("  (identical totals - splitting the turn does not shrink the tree by itself.");
                Console.WriteLine("   It pays off through alpha-beta cutoffs, transpositions and ordering,");
                Console.WriteLine("   which is why the stone node's width is the thing worth attacking.)");
            }
        }

        static string Label(NodeKind k)
        {
            switch (k)
            {
                case NodeKind.Chess:     return "chess move";
                case NodeKind.Territory: return "territory removal";
                case NodeKind.Bonus:     return "pawn bonus stone";
                default:                 return "main go stone";
            }
        }

        // ---------------------------------------------------------------- bench

        static void Bench(Args a)
        {
            int maxDepth = a.Int("depth", 5);
            int plies = a.Int("plies", 0);
            int timeMs = a.Int("time", 1_000_000);
            int seed = a.Int("seed", 1);

            var s = Positions.StartPosition();
            var rng = new RandomAgent(seed);
            for (int i = 0; i < plies && !s.gameOver; i++) Driver.Step(s, rng, out _);

            Console.WriteLine(Positions.Render(s));
            Console.WriteLine($"node type: {Label(KindOf(s))}   legal decisions: {SimRules.GenerateAllLegalFullTurns(s).Count}");
            Console.WriteLine();

            SimRules.useMobilityEval = a.Bool("mobility", true);
            SimRules.mobilityWeight = a.Int("mobilityWeight", 2);

            Console.WriteLine($"{"depth",6}{"ms",12}{"nodes",14}{"nodes/sec",14}   best");
            Console.WriteLine(new string('-', 70));

            for (int d = 1; d <= maxDepth; d++)
            {
                var sw = Stopwatch.StartNew();
                var best = SimSearch.FindBestTurn(s, d, timeMs);
                sw.Stop();

                long nodes = SimSearch.NodesSearched;
                double secs = Math.Max(sw.Elapsed.TotalSeconds, 1e-9);
                Console.WriteLine($"{d,6}{sw.ElapsedMilliseconds,12}{nodes,14:N0}{nodes / secs,14:N0}   {Positions.DescribeTurn(best)}");

                if (sw.ElapsedMilliseconds > 60_000)
                {
                    Console.WriteLine("  (stopping: last depth exceeded 60s)");
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- match

        static IAgent MakeAgent(string spec, int seed)
        {
            var parts = spec.Split(':');
            string kind = parts[0].ToLowerInvariant();
            int depth = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 4;

            var cfg = new AgentConfig { Name = spec, Depth = depth };

            switch (kind)
            {
                case "random": return new RandomAgent(seed, spec);
                case "legacy": return new LegacyAgent(cfg);
                case "search": return new SearchAgent(cfg);
                default: throw new ArgumentException($"unknown agent kind '{kind}'");
            }
        }

        static void Match(Args a)
        {
            string specA = a.Str("a", "legacy:4");
            string specB = a.Str("b", "search:4");
            int games = a.Int("games", 20);
            int turns = a.Int("turns", 120);
            int seed = a.Int("seed", 1);
            int openings = a.Int("openings", 6);

            Console.WriteLine($"match: A={specA}  B={specB}   {games} games, <={turns} turns, {openings} random opening decisions");
            Console.WriteLine();

            int aWins = 0, bWins = 0, draws = 0;
            var totalSw = Stopwatch.StartNew();

            for (int g = 0; g < games; g++)
            {
                bool aIsWhite = (g % 2) == 0;
                var agentA = MakeAgent(specA, seed + g);
                var agentB = MakeAgent(specB, seed + g + 10_000);

                var white = aIsWhite ? agentA : agentB;
                var black = aIsWhite ? agentB : agentA;

                var sw = Stopwatch.StartNew();
                var r = Driver.PlayGame(white, black, turns, openings, seed + g);
                sw.Stop();

                string winner;
                if (r.Outcome == GameOutcome.WhiteWins) { winner = aIsWhite ? "A" : "B"; if (aIsWhite) aWins++; else bWins++; }
                else if (r.Outcome == GameOutcome.BlackWins) { winner = aIsWhite ? "B" : "A"; if (aIsWhite) bWins++; else aWins++; }
                else { winner = "-"; draws++; }

                Console.WriteLine($"  game {g + 1,3}: A plays {(aIsWhite ? "White" : "Black")}  -> {winner,-2} " +
                                  $"({r.Reason}, {r.Turns} turns, {sw.Elapsed.TotalSeconds,5:F1}s)");
            }

            totalSw.Stop();
            Console.WriteLine();
            Console.WriteLine($"A ({specA}): {aWins}   B ({specB}): {bWins}   draws/unfinished: {draws}");

            int decisive = aWins + bWins;
            if (decisive > 0)
                Console.WriteLine($"A score: {(aWins + 0.5 * draws) / games:P1}   (decisive games: {decisive})");
            Console.WriteLine($"total: {totalSw.Elapsed.TotalSeconds:F1}s");
        }

        // -------------------------------------------------------------- profile

        static void Profile(Args a)
        {
            int plies = a.Int("plies", 40);
            int seed = a.Int("seed", 7);
            int iters = a.Int("iters", 20000);

            var s = Positions.StartPosition();
            var rng = new RandomAgent(seed);
            for (int i = 0; i < plies && !s.gameOver; i++) Driver.Step(s, rng, out _);

            // Every operation is warmed up BEFORE any of them is timed. Timing them
            // one-at-a-time lets tiered JIT promote shared code during the first
            // measurement, which made later operations look faster than earlier ones.
            var ops = new (string Label, Action Run)[]
            {
                ("Evaluate (mobility OFF)",   () => { SimRules.useMobilityEval = false; SimRules.Evaluate(s); }),
                ("Evaluate (mobility ON)",    () => { SimRules.useMobilityEval = true;  SimRules.Evaluate(s); }),
                ("Generate (search path, no superko)", () => SimRules.GenerateAllLegalFullTurns(s, false)),
                ("Generate (root path, superko)",      () => SimRules.GenerateAllLegalFullTurns(s, true)),
                ("SimState.DeepCopy",         () => s.DeepCopy()),
                ("SimZobrist.ComputeHash",    () => SimZobrist.ComputeHash(s)),
            };

            SimRules.mobilityWeight = 2;

            // Several warmup rounds so tiered JIT settles on optimized code for everything.
            for (int round = 0; round < 3; round++)
                foreach (var op in ops)
                    for (int i = 0; i < 5000; i++) op.Run();

            Console.WriteLine($"profiling a {Label(KindOf(s))} node, {iters:N0} iterations each");
            Console.WriteLine("(all operations warmed up before any is timed)");
            Console.WriteLine();
            Console.WriteLine($"{"operation",-42}{"us/call",12}{"bytes/call",14}");
            Console.WriteLine(new string('-', 68));

            foreach (var op in ops)
                Time(op.Label, iters, op.Run);

            SimRules.useMobilityEval = false;

            Console.WriteLine();
            Console.WriteLine("A search node pays ComputeHash + GenerateAllLegalFullTurns once,");
            Console.WriteLine("then DeepCopy + ApplyFullTurn for every child it visits.");
        }

        static void Time(string label, int iters, Action f)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long bytes0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iters; i++) f();
            sw.Stop();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - bytes0;

            double us = sw.Elapsed.TotalMilliseconds * 1000.0 / iters;
            Console.WriteLine($"{label,-42}{us,12:F2}{bytes / (double)iters,14:N0}");
        }

        // ----------------------------------------------------------------- args

        sealed class Args
        {
            readonly Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Args(string[] argv)
            {
                for (int i = 0; i < argv.Length; i++)
                {
                    if (!argv[i].StartsWith("--")) continue;
                    string key = argv[i].Substring(2);
                    string val = (i + 1 < argv.Length && !argv[i + 1].StartsWith("--")) ? argv[++i] : "true";
                    _map[key] = val;
                }
            }

            public string Str(string k, string d) => _map.TryGetValue(k, out var v) ? v : d;
            public int Int(string k, int d) => _map.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : d;
            public bool Bool(string k, bool d) => _map.TryGetValue(k, out var v) ? v != "false" && v != "0" : d;
        }
    }
}
