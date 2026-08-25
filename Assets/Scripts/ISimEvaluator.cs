using System.Collections.Generic;

/// <summary>
/// Supplies MCTS with move priors and a position value - the two things a policy/value
/// network provides.
///
/// This interface exists so SimMcts stays dependency-free. The ONNX Runtime implementation
/// lives in Tools/ChoSim and never enters the Unity assembly; Unity would supply its own
/// backing (Sentis) against the same interface.
/// </summary>
public interface ISimEvaluator
{
    /// <summary>
    /// Scores one position.
    ///
    /// Fills priorsOut[i] with the prior for moves[i]. Priors must be non-negative and are
    /// expected to sum to roughly 1 over the supplied moves, which are exactly the legal
    /// ones - so the caller has already applied the legality mask.
    ///
    /// Returns the value in [-1, 1] from state.currentPlayer's point of view, matching the
    /// convention SimMcts uses everywhere: +1 means the player to move at THIS node is winning.
    /// </summary>
    float Evaluate(SimState state, List<SimTurn> moves, float[] priorsOut);
}
