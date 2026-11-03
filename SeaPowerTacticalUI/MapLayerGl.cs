using System;
using UnityEngine;

namespace VoiceBotBridge
{
    /// <summary>
    /// GL.Begin/GL.End that cannot be left open (T-107 A3). An exception between Begin and End leaves GL "inside Begin" and the game dies natively,
    /// so End always runs in a finally. Needs Unity; not part of the offline stand.
    /// </summary>
    internal static class GlSafe
    {
        public static void Begin(int mode, Action draw)
        {
            GL.Begin(mode);
            try { draw(); }
            finally { GL.End(); }
        }

        public static void Lines(Action draw) { Begin(GL.LINES, draw); }
        public static void Tris(Action draw) { Begin(GL.TRIANGLES, draw); }
        public static void Quads(Action draw) { Begin(GL.QUADS, draw); }
    }
}
