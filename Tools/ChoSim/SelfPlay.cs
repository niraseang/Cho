using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace ChoSim
{
    /// <summary>
    /// Compact SimState serialisation for training data.
    ///
    /// Positions are stored, not planes. A 33x6x7 float tensor is 5.5 KB; the position it came
    /// from is under 90 bytes, and the trainer can re-encode planes on load. At ten million
    /// samples that is the difference between ~55 GB and ~2 GB.
    /// </summary>
    public static class PositionCodec
    {
        // Piece byte: 0 = empty, else 1 + (colour*6 + type)*4 + moveFlags. Max 48, fits a byte.
        const byte NoCoord = 0xFF;

        public static byte[] Encode(SimState s)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write((byte)s.boardWidth);
            w.Write((byte)s.boardHeight);

            byte flags = 0;
            if (s.currentPlayer == PieceColor.Black) flags |= 1;
            if (s.phaseOne) flags |= 2;
            if (s.waitingForTerritoryClick) flags |= 4;
            if (s.waitingForPawnStoneChoice) flags |= 8;
            w.Write(flags);

            byte castling = 0;
            if (s.whiteCanCastleKingSide) castling |= 1;
            if (s.whiteCanCastleQueenSide) castling |= 2;
            if (s.blackCanCastleKingSide) castling |= 4;
            if (s.blackCanCastleQueenSide) castling |= 8;
            w.Write(castling);

            for (int x = 0; x < s.boardWidth; x++)
                for (int y = 0; y < s.boardHeight; y++)
                {
                    var sp = s.squares[x, y];
                    if (!sp.HasValue) { w.Write((byte)0); continue; }
                    var p = sp.Value;
                    int colour = p.color == PieceColor.White ? 0 : 1;
                    int move = (p.hasMoved ? 1 : 0) | (p.justDoubleStepped ? 2 : 0);
                    w.Write((byte)(1 + (colour * 6 + (int)p.type) * 4 + move));
                }

            for (int ix = 0; ix < s.intersectionWidth; ix++)
                for (int iy = 0; iy < s.intersectionHeight; iy++)
                    w.Write((byte)s.stones[ix, iy]);

            WriteCoord(w, s.goKoPoint);
            WriteCoord(w, s.lastMovedSquare);
            w.Write((byte)Math.Min(255, s.noProgressTurns));

            w.Flush();
            return ms.ToArray();
        }

        public static SimState Decode(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var r = new BinaryReader(ms);

            int bw = r.ReadByte();
            int bh = r.ReadByte();
            var s = new SimState(bw, bh);

            byte flags = r.ReadByte();
            s.currentPlayer = (flags & 1) != 0 ? PieceColor.Black : PieceColor.White;
            s.phaseOne = (flags & 2) != 0;
            s.waitingForTerritoryClick = (flags & 4) != 0;
            s.waitingForPawnStoneChoice = (flags & 8) != 0;
            s.blackInitialStonePending = false;

            byte castling = r.ReadByte();
            s.whiteCanCastleKingSide = (castling & 1) != 0;
            s.whiteCanCastleQueenSide = (castling & 2) != 0;
            s.blackCanCastleKingSide = (castling & 4) != 0;
            s.blackCanCastleQueenSide = (castling & 8) != 0;

            for (int x = 0; x < bw; x++)
                for (int y = 0; y < bh; y++)
                {
                    byte b = r.ReadByte();
                    if (b == 0) { s.squares[x, y] = null; continue; }

                    int v = b - 1;
                    int move = v & 3;
                    int idx = v >> 2;

                    var piece = new SimPiece
                    {
                        color = (idx / 6) == 0 ? PieceColor.White : PieceColor.Black,
                        type = (PieceType)(idx % 6),
                        hasMoved = (move & 1) != 0,
                        justDoubleStepped = (move & 2) != 0
                    };
                    s.squares[x, y] = piece;

                    if (piece.type == PieceType.King)
                    {
                        if (piece.color == PieceColor.White) s.whiteKingSquare = new Vector2Int(x, y);
                        else s.blackKingSquare = new Vector2Int(x, y);
                    }
                }

            for (int ix = 0; ix < s.intersectionWidth; ix++)
                for (int iy = 0; iy < s.intersectionHeight; iy++)
                    s.stones[ix, iy] = (SimStoneColor)r.ReadByte();

            s.goKoPoint = ReadCoord(r);
            s.lastMovedSquare = ReadCoord(r);
            s.noProgressTurns = r.ReadByte();

            return s;
        }

        static void WriteCoord(BinaryWriter w, Vector2Int? c)
        {
            if (c.HasValue) { w.Write((byte)c.Value.x); w.Write((byte)c.Value.y); }
            else { w.Write(NoCoord); w.Write(NoCoord); }
        }

        static Vector2Int? ReadCoord(BinaryReader r)
        {
            byte x = r.ReadByte(), y = r.ReadByte();
            if (x == NoCoord && y == NoCoord) return null;
            return new Vector2Int(x, y);
        }
    }

    /// <summary>One training example: a position, the search's visit distribution, and the
    /// eventual game result from that position's mover's point of view.</summary>
    sealed class Sample
    {
        public byte[] Position;
        public int[] PolicyIndex;
        public int[] PolicyVisits;
        public PieceColor Mover;
        public float Value;
    }

    public static class SelfPlay
    {
        const string Magic = "CHOSP2";

        public static int Run(int games, int sims, Variant variant, string outPath,
                              int seed, int openingTemperatureDecisions, bool quiet,
                              string modelPath = null, int workers = 1, int batchSize = 32)
        {
            if (workers > 1 && !string.IsNullOrEmpty(modelPath))
            {
                return RunParallel(games, sims, variant, outPath, seed,
                                   openingTemperatureDecisions, quiet, modelPath, workers, batchSize);
            }

            Positions.ApplyVariantRules(variant);

            // Without a network here, every generation would train on data from the same
            // uniform-prior searcher and nothing would compound. This is what makes a campaign
            // a campaign rather than the same run repeated.
            ISimEvaluator evaluator = null;
            if (!string.IsNullOrEmpty(modelPath))
            {
                evaluator = NnAgent.Get(modelPath, Positions.Create(variant));
                Console.WriteLine($"self-play guided by {modelPath}");
            }

            var all = new List<Sample>();
            int decisive = 0;

            for (int g = 0; g < games; g++)
            {
                var samples = new List<Sample>();
                var s = Positions.Create(variant);
                int decisions = 0;

                while (!s.gameOver && decisions < 1200)
                {
                    var legal = SimRules.GenerateAllLegalFullTurns(s);
                    if (legal == null || legal.Count == 0) break;

                    var cfg = new SimMcts.Config
                    {
                        simulations = sims,
                        seed = seed * 7919 + g * 131 + decisions,
                        // Exploration early, best-move later: the standard way to keep openings
                        // varied without weakening the rest of the game.
                        temperature = decisions < openingTemperatureDecisions ? 1f : 0f,
                        dirichletWeight = 0.25f,
                        dirichletAlpha = 0.5f,
                        evaluator = evaluator
                    };

                    var move = SimMcts.Search(s, cfg);

                    if (SimMcts.LastRootMoves != null && SimMcts.LastRootVisits != null)
                    {
                        int n = SimMcts.LastRootMoves.Length;
                        var idx = new int[n];
                        var vis = new int[n];
                        for (int i = 0; i < n; i++)
                        {
                            idx[i] = SimFeatures.PolicyIndex(s, SimMcts.LastRootMoves[i]);
                            vis[i] = SimMcts.LastRootVisits[i];
                        }

                        samples.Add(new Sample
                        {
                            Position = PositionCodec.Encode(s),
                            PolicyIndex = idx,
                            PolicyVisits = vis,
                            Mover = s.currentPlayer
                        });
                    }

                    var before = s.currentPlayer;
                    SimRules.ApplyFullTurn(s, move);

                    // Record positions as they are reached, or superko sees only the opening and
                    // never restricts anything.
                    if (s.currentPlayer != before)
                        s.positionHistory?.Push(SimZobrist.ComputeBoardHash(s));

                    decisions++;
                }

                float whiteResult = 0f;
                if (s.gameOver && s.winner.HasValue)
                {
                    whiteResult = s.winner.Value == PieceColor.White ? 1f : -1f;
                    decisive++;
                }

                foreach (var sample in samples)
                    sample.Value = sample.Mover == PieceColor.White ? whiteResult : -whiteResult;

                all.AddRange(samples);

                if (!quiet)
                    Console.WriteLine($"  game {g + 1,4}: {decisions,4} decisions, {samples.Count,4} samples, " +
                                      $"result {(s.gameOver && s.winner.HasValue ? s.winner.ToString() : "draw")}");
            }

            Write(outPath, all, variant);

            Console.WriteLine();
            Console.WriteLine($"{all.Count:N0} samples from {games} games ({decisive} decisive) -> {outPath}");
            Console.WriteLine($"{new FileInfo(outPath).Length / 1024.0:F0} KB " +
                              $"({new FileInfo(outPath).Length / (double)Math.Max(1, all.Count):F0} bytes/sample)");
            return all.Count;
        }

        /// <summary>
        /// Many games at once, sharing one batched evaluator.
        ///
        /// The parallelism exists to keep the batch full, not to use more cores: workers spend
        /// most of their time blocked inside Evaluate waiting for their row, so oversubscribing
        /// threads relative to cores is deliberate.
        /// </summary>
        static int RunParallel(int games, int sims, Variant variant, string outPath, int seed,
                               int openingTemperatureDecisions, bool quiet, string modelPath,
                               int workers, int batchSize)
        {
            Positions.ApplyVariantRules(variant);

            var shape = Positions.Create(variant);
            using var evaluator = new BatchedEvaluator(modelPath, SimFeatures.PlaneCount,
                                                       shape.intersectionHeight,
                                                       shape.intersectionWidth,
                                                       maxBatch: batchSize);

            Console.WriteLine($"self-play guided by {modelPath} " +
                              $"({workers} workers, batches up to {batchSize})");

            // Indexed by game so the output file is identical regardless of completion order.
            var perGame = new List<Sample>[games];
            int decisive = 0;
            int next = -1;

            void Worker()
            {
                while (true)
                {
                    int g = Interlocked.Increment(ref next);
                    if (g >= games) return;

                    var samples = PlayOne(g, sims, variant, seed, openingTemperatureDecisions,
                                          evaluator, out bool wasDecisive);
                    perGame[g] = samples;
                    if (wasDecisive) Interlocked.Increment(ref decisive);
                }
            }

            var threads = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                threads[i] = new Thread(Worker) { IsBackground = true };
                threads[i].Start();
            }
            foreach (var t in threads) t.Join();

            var all = new List<Sample>();
            foreach (var g in perGame) if (g != null) all.AddRange(g);

            Write(outPath, all, variant);

            Console.WriteLine();
            Console.WriteLine($"{all.Count:N0} samples from {games} games ({decisive} decisive) -> {outPath}");
            Console.WriteLine($"batches: {evaluator.Batches:N0}, mean size {evaluator.MeanBatchSize:F1}, " +
                              $"{evaluator.Evaluations:N0} evaluations");
            return all.Count;
        }

        /// <summary>One self-play game. Thread-safe: SimRules and SimMcts keep their scratch
        /// state in [ThreadStatic] fields, so concurrent games do not interfere.</summary>
        static List<Sample> PlayOne(int g, int sims, Variant variant, int seed,
                                    int openingTemperatureDecisions, ISimEvaluator evaluator,
                                    out bool decisive)
        {
            var samples = new List<Sample>();
            var s = Positions.Create(variant);
            int decisions = 0;

            while (!s.gameOver && decisions < 1200)
            {
                var legal = SimRules.GenerateAllLegalFullTurns(s);
                if (legal == null || legal.Count == 0) break;

                var cfg = new SimMcts.Config
                {
                    simulations = sims,
                    seed = seed * 7919 + g * 131 + decisions,
                    temperature = decisions < openingTemperatureDecisions ? 1f : 0f,
                    dirichletWeight = 0.25f,
                    dirichletAlpha = 0.5f,
                    evaluator = evaluator
                };

                var move = SimMcts.Search(s, cfg);

                if (SimMcts.LastRootMoves != null && SimMcts.LastRootVisits != null)
                {
                    int n = SimMcts.LastRootMoves.Length;
                    var idx = new int[n];
                    var vis = new int[n];
                    for (int i = 0; i < n; i++)
                    {
                        idx[i] = SimFeatures.PolicyIndex(s, SimMcts.LastRootMoves[i]);
                        vis[i] = SimMcts.LastRootVisits[i];
                    }

                    samples.Add(new Sample
                    {
                        Position = PositionCodec.Encode(s),
                        PolicyIndex = idx,
                        PolicyVisits = vis,
                        Mover = s.currentPlayer
                    });
                }

                var before = s.currentPlayer;
                SimRules.ApplyFullTurn(s, move);
                if (s.currentPlayer != before)
                    s.positionHistory?.Push(SimZobrist.ComputeBoardHash(s));

                decisions++;
            }

            float whiteResult = 0f;
            decisive = s.gameOver && s.winner.HasValue;
            if (decisive) whiteResult = s.winner.Value == PieceColor.White ? 1f : -1f;

            foreach (var sample in samples)
                sample.Value = sample.Mover == PieceColor.White ? whiteResult : -whiteResult;

            return samples;
        }

        static void Write(string path, List<Sample> samples, Variant variant)
        {
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);

            w.Write(System.Text.Encoding.ASCII.GetBytes(Magic));
            w.Write((byte)(variant == Variant.Small ? 1 : 0));
            w.Write(SimFeatures.PlaneCount);
            w.Write(SimRules.noProgressTurnLimit);
            w.Write(samples.Count);

            foreach (var s in samples)
            {
                w.Write((ushort)s.Position.Length);
                w.Write(s.Position);

                w.Write((ushort)s.PolicyIndex.Length);
                for (int i = 0; i < s.PolicyIndex.Length; i++)
                {
                    w.Write((ushort)s.PolicyIndex[i]);
                    w.Write((ushort)Math.Min(ushort.MaxValue, s.PolicyVisits[i]));
                }

                w.Write(s.Value);
            }
        }

        /// <summary>
        /// Writes positions together with the exact planes SimFeatures produced for them.
        /// The training pipeline re-implements the encoder in Python; this is what lets that
        /// port be verified against the original instead of assumed to match.
        /// </summary>
        public static int WriteGoldens(string path, int count, Variant variant, int seed)
        {
            Positions.ApplyVariantRules(variant);

            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);

            w.Write(System.Text.Encoding.ASCII.GetBytes("CHOGD1"));
            w.Write((byte)(variant == Variant.Small ? 1 : 0));
            w.Write(SimFeatures.PlaneCount);
            w.Write(SimRules.noProgressTurnLimit);
            w.Write(count);

            var rng = new Random(seed);
            int written = 0;

            while (written < count)
            {
                // Walk a random game and snapshot along the way, so the set spans openings,
                // midgames, every phase, and both sides to move.
                var s = Positions.Create(variant);
                var agent = new RandomAgent(rng.Next());

                for (int step = 0; step < 160 && written < count && !s.gameOver; step++)
                {
                    if (rng.NextDouble() < 0.35)
                    {
                        var pos = PositionCodec.Encode(s);
                        var tensor = new float[SimFeatures.TensorSize(s)];
                        SimFeatures.Encode(s, tensor);

                        w.Write((ushort)pos.Length);
                        w.Write(pos);
                        w.Write(tensor.Length);
                        foreach (var v in tensor) w.Write(v);
                        written++;
                    }
                    Driver.Step(s, agent, out _);
                }
            }

            Console.WriteLine($"{written} golden positions -> {path} " +
                              $"({new FileInfo(path).Length / 1024.0:F0} KB)");
            return 0;
        }

        /// <summary>Reads a file back and re-derives planes, so a corrupt writer cannot go unnoticed.</summary>
        public static int Inspect(string path)
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);

            string magic = System.Text.Encoding.ASCII.GetString(r.ReadBytes(Magic.Length));
            if (magic != Magic) { Console.Error.WriteLine($"bad magic: {magic}"); return 1; }

            int variant = r.ReadByte();
            int planes = r.ReadInt32();
            int noProgressLimit = r.ReadInt32();
            int count = r.ReadInt32();

            // Variant switches live as statics on SimRules, so a file written under one set of
            // rules must be read back under the same ones. Without this the inspector re-derives
            // moves with double-steps and castling that the writer never offered.
            Positions.ApplyVariantRules(variant == 1 ? Variant.Small : Variant.Standard);
            SimRules.noProgressTurnLimit = noProgressLimit;

            Console.WriteLine($"{path}");
            Console.WriteLine($"  variant {(variant == 1 ? "Small" : "Standard")}, {planes} planes, " +
                              $"noProgressLimit {noProgressLimit}, {count:N0} samples");

            int totalVisits = 0, maxBranch = 0, bad = 0;
            double valueSum = 0;
            int draws = 0;

            for (int i = 0; i < count; i++)
            {
                int plen = r.ReadUInt16();
                var pos = r.ReadBytes(plen);

                int n = r.ReadUInt16();
                int sampleVisits = 0;
                var idx = new int[n];
                for (int k = 0; k < n; k++)
                {
                    idx[k] = r.ReadUInt16();
                    sampleVisits += r.ReadUInt16();
                }
                float value = r.ReadSingle();

                totalVisits += sampleVisits;
                if (n > maxBranch) maxBranch = n;
                valueSum += value;
                if (value == 0f) draws++;

                // Decode and re-encode: catches a codec that silently loses information.
                var state = PositionCodec.Decode(pos);
                var tensor = new float[SimFeatures.TensorSize(state)];
                SimFeatures.Encode(state, tensor);

                // The decoded position has no superko history, so its legal set can only be a
                // superset of what the search saw. Every recorded move must still appear in it,
                // and every recorded index must be in range for its head.
                var legal = SimRules.GenerateAllLegalFullTurns(state, applySuperko: false);
                var legalIdx = new HashSet<int>();
                foreach (var m in legal) legalIdx.Add(SimFeatures.PolicyIndex(state, m));

                int headSize = state.phaseOne
                    ? SimFeatures.ChessPolicySize(state)
                    : SimFeatures.IntersectionPolicySize(state);

                foreach (int k in idx)
                    if (k < 0 || k >= headSize || !legalIdx.Contains(k)) { bad++; break; }
            }

            Console.WriteLine($"  mean branching {(count > 0 ? totalVisits / (double)count : 0):F0} visits over " +
                              $"{(count > 0 ? maxBranch : 0)} max candidates");
            Console.WriteLine($"  mean value target {(count > 0 ? valueSum / count : 0):F3}, {draws:N0} drawn samples");
            Console.WriteLine($"  decoded positions with an out-of-range or illegal recorded move: {bad}");

            return bad == 0 ? 0 : 1;
        }
    }
}
