using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ChoSim
{
    /// <summary>
    /// Pools leaf evaluations from many concurrently-searching games into single batched ONNX
    /// calls.
    ///
    /// Batch-1 inference on this network is almost entirely overhead: a forward pass is ~30
    /// MFLOPs but takes ~166 us unbatched, which is well under 1% of what the hardware can do.
    /// The arithmetic is free; the calling is what costs. Batching amortises that across
    /// however many positions are in flight.
    ///
    /// Callers block inside Evaluate until their row of the batch comes back, so SimMcts needs
    /// no changes - it calls ISimEvaluator exactly as it does with the single-position
    /// evaluator. That is the whole reason the interface exists.
    /// </summary>
    public sealed class BatchedEvaluator : ISimEvaluator, IDisposable
    {
        sealed class Request
        {
            public SimState State;
            public List<SimTurn> Moves;
            public float[] Priors;
            public float[] Planes;      // encoded by the CALLER, see Evaluate
            public float Value;
            public readonly ManualResetEventSlim Ready = new ManualResetEventSlim(false);
        }

        // Encoding buffer per calling thread. Encoding on the batch thread serialised it behind
        // the one worker that also has to run inference; doing it here spreads it across the
        // game threads, which are otherwise just blocked.
        [ThreadStatic] static float[] _encodeScratch;

        readonly InferenceSession _session;
        readonly int _planes, _height, _width;
        readonly int _maxBatch;
        readonly int _waitMs;

        readonly BlockingCollection<Request> _queue = new BlockingCollection<Request>();
        readonly Thread _worker;
        volatile bool _shuttingDown;

        public long Evaluations { get; private set; }
        public long Batches { get; private set; }
        public double MeanBatchSize => Batches == 0 ? 0 : Evaluations / (double)Batches;

        public BatchedEvaluator(string modelPath, int planes, int height, int width,
                                int maxBatch = 32, int waitMs = 0)
        {
            // Intra-op threads are left to ONNX Runtime here: the parallelism comes from many
            // game threads feeding one batched call, not from splitting a tiny convolution.
            _session = new InferenceSession(modelPath, new SessionOptions());
            _planes = planes;
            _height = height;
            _width = width;
            _maxBatch = Math.Max(1, maxBatch);
            _waitMs = Math.Max(0, waitMs);

            _worker = new Thread(Loop) { IsBackground = true, Name = "cho-batch" };
            _worker.Start();
        }

        public float Evaluate(SimState state, List<SimTurn> moves, float[] priorsOut)
        {
            if (_shuttingDown) return 0f;

            int stride = _planes * _height * _width;
            if (_encodeScratch == null || _encodeScratch.Length != stride)
                _encodeScratch = new float[stride];

            SimFeatures.Encode(state, _encodeScratch);

            var req = new Request
            {
                State = state,
                Moves = moves,
                Priors = priorsOut,
                Planes = _encodeScratch     // safe: this thread blocks until the batch is done
            };

            _queue.Add(req);
            req.Ready.Wait();
            return req.Value;
        }

        void Loop()
        {
            var batch = new List<Request>(_maxBatch);

            while (!_shuttingDown)
            {
                batch.Clear();

                if (!_queue.TryTake(out var first, 50)) continue;
                batch.Add(first);

                // Drain whatever is already queued, without waiting for more.
                //
                // The queue pipelines by itself: while one batch is running inference, workers
                // pile up the next one. An explicit gather window on top of that is pure loss -
                // with a 2ms window and ~2ms of inference the batch thread spent half its life
                // waiting, capping throughput near 50% of what the model can do. Batch size
                // then self-regulates to however many arrive during one inference.
                while (batch.Count < _maxBatch && _queue.TryTake(out var next))
                    batch.Add(next);

                // Only relevant if a caller explicitly asks to linger for a bigger batch.
                if (_waitMs > 0)
                {
                    var deadline = Environment.TickCount64 + _waitMs;
                    while (batch.Count < _maxBatch)
                    {
                        int remaining = (int)(deadline - Environment.TickCount64);
                        if (remaining <= 0) break;
                        if (!_queue.TryTake(out var next, remaining)) break;
                        batch.Add(next);
                    }
                }

                try { RunBatch(batch); }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"batch failed: {e.Message}");
                    foreach (var r in batch)
                    {
                        r.Value = 0f;
                        for (int i = 0; i < r.Priors.Length; i++) r.Priors[i] = 0f;
                    }
                }
                finally
                {
                    foreach (var r in batch) r.Ready.Set();
                }
            }

            // Anything still queued at shutdown must be released, or its caller blocks forever.
            while (_queue.TryTake(out var leftover))
            {
                leftover.Value = 0f;
                for (int i = 0; i < leftover.Priors.Length; i++) leftover.Priors[i] = 0f;
                leftover.Ready.Set();
            }
        }

        void RunBatch(List<Request> batch)
        {
            int n = batch.Count;
            int stride = _planes * _height * _width;
            var input = new float[n * stride];

            // Planes are already encoded by each caller; this is just a gather.
            for (int i = 0; i < n; i++)
                Array.Copy(batch[i].Planes, 0, input, i * stride, stride);

            var tensor = new DenseTensor<float>(input, new[] { n, _planes, _height, _width });
            using var results = _session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("planes", tensor)
            });

            float[] Get(string name) => results.First(r => r.Name == name).AsEnumerable<float>().ToArray();

            var chess = Get("chess");
            var inter = Get("inter");
            var value = Get("value");

            int chessStride = chess.Length / n;
            int interStride = inter.Length / n;

            for (int i = 0; i < n; i++)
            {
                var req = batch[i];
                bool chessNode = req.State.phaseOne;

                var logits = chessNode ? chess : inter;
                int off = i * (chessNode ? chessStride : interStride);
                int size = chessNode ? chessStride : interStride;

                Softmax(req, logits, off, size);
                req.Value = value[i];
            }

            Evaluations += n;
            Batches++;
        }

        /// <summary>Softmax over exactly the legal moves, matching the masked log_softmax the
        /// policy loss was trained with.</summary>
        static void Softmax(Request req, float[] logits, int offset, int size)
        {
            var moves = req.Moves;
            var picked = new double[moves.Count];
            double max = double.NegativeInfinity;

            for (int k = 0; k < moves.Count; k++)
            {
                int idx = SimFeatures.PolicyIndex(req.State, moves[k]);
                picked[k] = (idx >= 0 && idx < size) ? logits[offset + idx] : double.NegativeInfinity;
                if (picked[k] > max) max = picked[k];
            }

            if (double.IsNegativeInfinity(max))
            {
                for (int k = 0; k < req.Priors.Length; k++) req.Priors[k] = 0f;
                return;
            }

            double sum = 0;
            for (int k = 0; k < moves.Count; k++)
            {
                double e = double.IsNegativeInfinity(picked[k]) ? 0.0 : Math.Exp(picked[k] - max);
                picked[k] = e;
                sum += e;
            }

            for (int k = 0; k < moves.Count; k++)
                req.Priors[k] = (float)(sum > 0 ? picked[k] / sum : 1.0 / moves.Count);
        }

        public void Dispose()
        {
            _shuttingDown = true;
            _queue.CompleteAdding();
            _worker.Join(2000);
            _session?.Dispose();
        }
    }
}
