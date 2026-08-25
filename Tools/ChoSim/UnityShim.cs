// Minimal UnityEngine stand-ins so the Sim* game sources compile and run headless.
// Only the surface actually used by SimTypes/SimRules/SimSearch/SimZobrist is provided.
//
// This file lives outside Assets/, so Unity never compiles it and it can never
// collide with the real UnityEngine types.

using System;

namespace UnityEngine
{
    public struct Vector2Int : IEquatable<Vector2Int>
    {
        public int x;
        public int y;

        public Vector2Int(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        public static bool operator ==(Vector2Int a, Vector2Int b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(Vector2Int a, Vector2Int b) => !(a == b);

        public bool Equals(Vector2Int other) => x == other.x && y == other.y;
        public override bool Equals(object obj) => obj is Vector2Int v && Equals(v);

        // Unique for the coordinate ranges this game uses (0..8), and cheap.
        public override int GetHashCode() => (x * 397) ^ y;

        public override string ToString() => $"({x}, {y})";
    }

    public static class Mathf
    {
        public static int Abs(int v) => Math.Abs(v);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static float Abs(float v) => Math.Abs(v);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    public static class Debug
    {
        // Off by default: the harness runs millions of nodes and Unity logging is not the point.
        public static bool Enabled = false;

        public static void Log(object msg) { if (Enabled) Console.WriteLine("[Log] " + msg); }
        public static void LogWarning(object msg) { if (Enabled) Console.WriteLine("[Warn] " + msg); }
        public static void LogError(object msg) { Console.Error.WriteLine("[Error] " + msg); }
    }
}

// Mirrors Assets/Scripts/Piece.cs. That file also declares a MonoBehaviour, which would
// drag in the whole Unity object model, so only the enums are reproduced here.
// Order matters: SimZobrist indexes key tables by (int)PieceType.
public enum PieceColor { White, Black }
public enum PieceType { King, Queen, Rook, Bishop, Knight, Pawn }
