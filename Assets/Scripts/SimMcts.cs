using System;
using System.Collections.Generic;

/// <summary>
/// Monte Carlo tree search over decision nodes, shaped for a policy/value network but usable
/// today with uniform priors and SimRules.Evaluate as the leaf value.
///
/// The one thing this game changes about textbook MCTS: a turn spans several decision nodes, so
/// a child often has the SAME player to move as its parent. Values must only be negated across
/// an actual handover. Every sign in here is derived from Node.toMove rather than from depth
/// parity, because depth parity is wrong in this game.
/// </summary>
public static class SimMcts
{
    public sealed class Config
    {
        public int simulations = 200;

        /// <summary>Exploration constant in the PUCT term.</summary>
        public float cpuct = 1.5f;

        /// <summary>
        /// Divisor mapping SimRules.Evaluate's centipawn-ish score onto tanh's useful range.
        /// 400 puts "up a piece or so" around +/-0.6 rather than saturating.
        /// </summary>
        public float evalScale = 400f;

        /// <summary>Root exploration noise. Weight 0 disables it (use for evaluation matches).</summary>
        public float dirichletAlpha = 0.5f;   // ~10/branching for this game
        public float dirichletWeight = 0f;

        /// <summary>0 picks the most-visited child; higher samples proportional to visits^(1/T).</summary>
        public float temperature = 0f;

        public int seed = 0;
    }

    sealed class Node
    {
        public SimTurn move;              // the decision that led here, from the parent
        public Node parent;
        public PieceColor toMove;         // whose decision this node represents
        public bool terminal;

        public Node[] children;           // null until expanded
        public float[] prior;

        public int visits;
        public double valueSum;           // always from THIS node's toMove perspective

        public bool Expanded => children != null;
        public double MeanValue => visits == 0 ? 0.0 : valueSum / visits;
    }

    /// <summary>Nodes expanded during the most recent Search call.</summary>
    public static long NodesExpanded;

    /// <summary>Visit counts for the root's children after the last Search, for policy targets.</summary>
    public static int[] LastRootVisits;
    public static SimTurn[] LastRootMoves;

    public static SimTurn Search(SimState root, Config cfg)
    {
        if (root == null) return default;
        cfg = cfg ?? new Config();

        NodesExpanded = 0;
        LastRootVisits = null;
        LastRootMoves = null;

        var rng = new Random(cfg.seed);

        // The root is generated with superko applied, so the move finally played is always legal.
        var rootMoves = SimRules.GenerateAllLegalFullTurns(root, applySuperko: true);
        if (rootMoves == null || rootMoves.Count == 0) return default;
        if (rootMoves.Count == 1)
        {
            // Still record the (trivial) visit distribution, so self-play export never sees a
            // gap for forced decisions.
            LastRootMoves = new[] { rootMoves[0] };
            LastRootVisits = new[] { 1 };
            return rootMoves[0];
        }

        var rootNode = new Node { toMove = root.currentPlayer };
        ExpandWith(rootNode, rootMoves);
        if (cfg.dirichletWeight > 0f) AddDirichletNoise(rootNode, cfg, rng);

        for (int i = 0; i < cfg.simulations; i++)
        {
            var state = root.DeepCopy();
            var node = rootNode;

            // --- selection -------------------------------------------------
            while (node.Expanded && !node.terminal)
            {
                int idx = SelectChild(node, cfg);
                var child = node.children[idx];

                SimRules.ApplyFullTurn(state, child.move);
                node = child;
            }

            // --- expansion + evaluation ------------------------------------
            double value = EvaluateLeaf(node, state, cfg);

            // --- backup ----------------------------------------------------
            Backup(node, value);
        }

        return PickRootMove(rootNode, cfg, rng);
    }

    // ------------------------------------------------------------------ tree

    /// <summary>
    /// A decision only hands the turn over when it is the main stone; chess moves, territory
    /// removals and pawn bonus stones all leave the same player to move. This mirrors
    /// ApplyFullTurn exactly, and lets a child's perspective be known without applying the move.
    /// </summary>
    static bool EndsTurn(SimTurn t) => t.mainStone.HasValue;

    static PieceColor Other(PieceColor c) =>
        c == PieceColor.White ? PieceColor.Black : PieceColor.White;

    static void ExpandWith(Node node, List<SimTurn> moves)
    {
        node.children = new Node[moves.Count];
        node.prior = new float[moves.Count];

        float uniform = 1f / moves.Count;
        for (int i = 0; i < moves.Count; i++)
        {
            node.children[i] = new Node
            {
                move = moves[i],
                parent = node,
                toMove = EndsTurn(moves[i]) ? Other(node.toMove) : node.toMove
            };
            node.prior[i] = uniform;   // replaced by the policy head later
        }

        NodesExpanded++;
    }

    static int SelectChild(Node node, Config cfg)
    {
        double sqrtParent = Math.Sqrt(Math.Max(1, node.visits));

        int best = 0;
        double bestScore = double.NegativeInfinity;

        for (int i = 0; i < node.children.Length; i++)
        {
            var c = node.children[i];

            // A child's statistics are stored from its own perspective. Only reorient them
            // when the child belongs to the other player - this is the whole special case.
            double q = c.visits == 0
                ? 0.0
                : (c.toMove == node.toMove ? c.MeanValue : -c.MeanValue);

            double u = cfg.cpuct * node.prior[i] * sqrtParent / (1 + c.visits);
            double score = q + u;

            if (score > bestScore) { bestScore = score; best = i; }
        }

        return best;
    }

    static double EvaluateLeaf(Node node, SimState state, Config cfg)
    {
        if (state.gameOver)
        {
            node.terminal = true;
            if (!state.winner.HasValue) return 0.0;                 // draw
            return state.winner.Value == node.toMove ? 1.0 : -1.0;
        }

        var moves = SimRules.GenerateAllLegalFullTurns(state, SimRules.superkoInSearch);
        if (moves == null || moves.Count == 0)
        {
            // No legal decision. SimRules has no pass concept, so treat it as terminal-neutral
            // rather than inventing a rule the game does not have.
            node.terminal = true;
            return 0.0;
        }

        ExpandWith(node, moves);

        // Leaf value from the static evaluator, squashed into tanh's range. This is exactly the
        // slot the value head will take over.
        int score = SimRules.EvaluateForSideToMove(state);
        return Math.Tanh(score / (double)cfg.evalScale);
    }

    static void Backup(Node leaf, double value)
    {
        var perspective = leaf.toMove;

        for (var n = leaf; n != null; n = n.parent)
        {
            n.visits++;
            n.valueSum += (n.toMove == perspective) ? value : -value;
        }
    }

    // ------------------------------------------------------------- selection

    static SimTurn PickRootMove(Node root, Config cfg, Random rng)
    {
        int n = root.children.Length;

        LastRootMoves = new SimTurn[n];
        LastRootVisits = new int[n];
        for (int i = 0; i < n; i++)
        {
            LastRootMoves[i] = root.children[i].move;
            LastRootVisits[i] = root.children[i].visits;
        }

        if (cfg.temperature <= 0f)
        {
            int best = 0;
            for (int i = 1; i < n; i++)
                if (root.children[i].visits > root.children[best].visits) best = i;
            return root.children[best].move;
        }

        double invT = 1.0 / cfg.temperature;
        var weights = new double[n];
        double total = 0;
        for (int i = 0; i < n; i++)
        {
            weights[i] = Math.Pow(root.children[i].visits, invT);
            total += weights[i];
        }
        if (total <= 0) return root.children[0].move;

        double r = rng.NextDouble() * total;
        for (int i = 0; i < n; i++)
        {
            r -= weights[i];
            if (r <= 0) return root.children[i].move;
        }
        return root.children[n - 1].move;
    }

    // ----------------------------------------------------------------- noise

    static void AddDirichletNoise(Node root, Config cfg, Random rng)
    {
        int n = root.prior.Length;
        var noise = new double[n];
        double sum = 0;

        for (int i = 0; i < n; i++)
        {
            noise[i] = SampleGamma(rng, cfg.dirichletAlpha);
            sum += noise[i];
        }
        if (sum <= 0) return;

        for (int i = 0; i < n; i++)
            root.prior[i] = (float)((1 - cfg.dirichletWeight) * root.prior[i]
                                    + cfg.dirichletWeight * (noise[i] / sum));
    }

    // Marsaglia-Tsang. Only used for root noise, so clarity beats speed.
    static double SampleGamma(Random rng, double alpha)
    {
        if (alpha < 1.0)
        {
            double u = rng.NextDouble();
            return SampleGamma(rng, alpha + 1.0) * Math.Pow(u, 1.0 / alpha);
        }

        double d = alpha - 1.0 / 3.0;
        double c = 1.0 / Math.Sqrt(9.0 * d);

        while (true)
        {
            double x, v;
            do
            {
                x = NextGaussian(rng);
                v = 1.0 + c * x;
            } while (v <= 0);

            v = v * v * v;
            double u = rng.NextDouble();

            if (u < 1.0 - 0.0331 * x * x * x * x) return d * v;
            if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v))) return d * v;
        }
    }

    static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
