using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using UnityEngine;

namespace ChoSim
{
    /// <summary>
    /// Runs the exported network for SimMcts.
    ///
    /// Lives here rather than in Assets/Scripts so the Unity assembly never takes an ONNX
    /// Runtime dependency - SimMcts talks to ISimEvaluator, and Unity would back that with
    /// Sentis instead.
    /// </summary>
    public sealed class OnnxEvaluator : ISimEvaluator, IDisposable
    {
        readonly InferenceSession _session;
        readonly int _planes, _height, _width;
        float[] _input;

        public long Evaluations { get; private set; }

        public OnnxEvaluator(string modelPath, int planes, int height, int width)
        {
            var opts = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
            _session = new InferenceSession(modelPath, opts);
            _planes = planes;
            _height = height;
            _width = width;
            _input = new float[planes * height * width];
        }

        public float Evaluate(SimState state, List<SimTurn> moves, float[] priorsOut)
        {
            var raw = Run(state);

            // Priors are a softmax over exactly the legal moves, matching how the policy loss
            // was computed during training (masked log_softmax over the legal set).
            bool chessNode = state.phaseOne;
            var logits = chessNode ? raw.chess : raw.inter;
            int headSize = logits.Length;

            double max = double.NegativeInfinity;
            var picked = new double[moves.Count];

            for (int i = 0; i < moves.Count; i++)
            {
                int idx = SimFeatures.PolicyIndex(state, moves[i]);
                picked[i] = (idx >= 0 && idx < headSize) ? logits[idx] : double.NegativeInfinity;
                if (picked[i] > max) max = picked[i];
            }

            if (double.IsNegativeInfinity(max))
            {
                // Nothing indexable; let SimMcts fall back to uniform.
                for (int i = 0; i < priorsOut.Length; i++) priorsOut[i] = 0f;
                return raw.value;
            }

            double sum = 0;
            for (int i = 0; i < moves.Count; i++)
            {
                double e = double.IsNegativeInfinity(picked[i]) ? 0.0 : Math.Exp(picked[i] - max);
                picked[i] = e;
                sum += e;
            }

            for (int i = 0; i < moves.Count; i++)
                priorsOut[i] = (float)(sum > 0 ? picked[i] / sum : 1.0 / moves.Count);

            return raw.value;
        }

        public (float[] chess, float[] promo, float[] inter, float value) Run(SimState state)
        {
            SimFeatures.Encode(state, _input);
            Evaluations++;

            var tensor = new DenseTensor<float>(_input, new[] { 1, _planes, _height, _width });
            using var results = _session.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("planes", tensor)
            });

            float[] Get(string name) =>
                results.First(r => r.Name == name).AsEnumerable<float>().ToArray();

            var chess = Get("chess");
            var promo = Get("promo");
            var inter = Get("inter");
            var value = Get("value")[0];

            return (chess, promo, inter, value);
        }

        public void Dispose() => _session?.Dispose();

        /// <summary>
        /// Compares this side's outputs against the ones PyTorch produced for the same
        /// positions. Covers the whole chain at once - the Python port of the encoder, the
        /// ONNX export, and the runtime - so no link in it has to be taken on trust.
        /// </summary>
        public static int Parity(string parityPath, string modelPath, Variant variant, double tol)
        {
            Positions.ApplyVariantRules(variant);

            using var fs = System.IO.File.OpenRead(parityPath);
            using var r = new System.IO.BinaryReader(fs);

            string magic = System.Text.Encoding.ASCII.GetString(r.ReadBytes(6));
            if (magic != "CHOPR1")
            {
                Console.Error.WriteLine($"bad magic: {magic}");
                return 1;
            }

            int count = r.ReadInt32();

            // Dimensions come from the first position, so this works for any board size.
            long headerEnd = fs.Position;
            var probe = PositionCodec.Decode(r.ReadBytes(r.ReadUInt16()));
            fs.Position = headerEnd;

            using var ev = new OnnxEvaluator(modelPath, SimFeatures.PlaneCount,
                                             probe.intersectionHeight, probe.intersectionWidth);

            double worst = 0;
            string worstWhere = "";
            int bad = 0;

            for (int i = 0; i < count; i++)
            {
                var state = PositionCodec.Decode(r.ReadBytes(r.ReadUInt16()));

                float[] Expected()
                {
                    int n = r.ReadInt32();
                    var a = new float[n];
                    for (int k = 0; k < n; k++) a[k] = r.ReadSingle();
                    return a;
                }

                var eChess = Expected();
                var ePromo = Expected();
                var eInter = Expected();
                float eValue = r.ReadSingle();

                var got = ev.Run(state);

                void Compare(string name, float[] a, float[] b)
                {
                    if (a.Length != b.Length) { bad++; return; }
                    for (int k = 0; k < a.Length; k++)
                    {
                        double d = Math.Abs(a[k] - b[k]);
                        if (d > worst) { worst = d; worstWhere = $"{name}[{k}] of position {i}"; }
                    }
                }

                Compare("chess", eChess, got.chess);
                Compare("promo", ePromo, got.promo);
                Compare("inter", eInter, got.inter);

                double dv = Math.Abs(eValue - got.value);
                if (dv > worst) { worst = dv; worstWhere = $"value of position {i}"; }
            }

            Console.WriteLine($"{count} positions | worst absolute difference {worst:E2} at {worstWhere}");
            if (bad > 0) Console.WriteLine($"  {bad} outputs had the wrong shape");

            bool ok = bad == 0 && worst <= tol;
            Console.WriteLine(ok
                ? $"PASS  C# matches PyTorch within {tol:E1}"
                : $"FAIL  exceeds tolerance {tol:E1}");
            return ok ? 0 : 1;
        }
    }
}
