// T-199 A (ADR-0084 context): one arbiter for the Esc key. The game reads Esc through UnityEngine.Input (InputHandler.OnKeyAction_Cancel), so Event.Use() of the mod
// cannot stop the pause menu. The owners (an open list of a window, a pick / draw mode, a key recording) say "I hold a transient element";
// the Harmony prefix of the game's Cancel skips it while Swallow() is true. Pure (no Unity).
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class UiEscGuard
    {
        /// <summary>The Esc that closes the element (frame F) is still swallowed in F and F+1 (the owner may release in OnGUI of the same frame, before or after the game's Update).</summary>
        public const int GraceFrames = 1;

        private static readonly HashSet<string> On = new HashSet<string>();
        private static readonly Dictionary<string, int> Released = new Dictionary<string, int>();

        public static void Set(string owner, bool on, int frame)
        {
            if (string.IsNullOrEmpty(owner)) return;
            if (on) { On.Add(owner); Released.Remove(owner); }
            else if (On.Remove(owner)) Released[owner] = frame;
        }

        public static bool Swallow(int frame)
        {
            if (On.Count > 0) return true;
            if (Released.Count == 0) return false;
            List<string> old = null;
            bool hit = false;
            foreach (var kv in Released)
            {
                if (kv.Value >= frame - GraceFrames) hit = true;
                else (old ?? (old = new List<string>())).Add(kv.Key);
            }
            if (old != null) foreach (string o in old) Released.Remove(o);
            return hit;
        }

        /// <summary>The owners for the journal line ("cap.list,asw.mode").</summary>
        public static string OwnersText
        {
            get
            {
                var all = new List<string>(On);
                foreach (var k in Released.Keys) if (!On.Contains(k)) all.Add(k);
                return string.Join(",", all.ToArray());
            }
        }

        /// <summary>Mission exit / Dispose / a game menu: nothing is held and no grace window.</summary>
        internal static void ReleaseAll() { On.Clear(); Released.Clear(); }

        internal static void Reset() { ReleaseAll(); }

        public static string SelfTest()
        {
            Reset();
            if (Swallow(1)) return "empty must not swallow";
            Set("cap.list", true, 10);
            if (!Swallow(10) || !Swallow(500)) return "owner on swallows";
            Set("cap.list", false, 11);
            if (!Swallow(11) || !Swallow(12)) return "grace frames after release";
            if (Swallow(13)) return "swallowed after the grace";
            Set("a", true, 20); Set("b", true, 20);
            Set("a", false, 21);
            if (!Swallow(40)) return "the second owner keeps it";
            Set("b", false, 41);
            if (!Swallow(42) || Swallow(43)) return "two owners released";
            Set("", true, 50); Set(null, true, 50);
            if (Swallow(50)) return "empty owner is ignored";
            Set("never", false, 60);
            if (Swallow(60)) return "release without owner records nothing";
            Set("x", true, 70); Set("y", true, 70); Set("y", false, 71);
            if (OwnersText != "x,y" && OwnersText != "y,x") return "OwnersText: " + OwnersText;
            ReleaseAll();
            if (Swallow(71) || Swallow(72) || OwnersText != "") return "ReleaseAll clears the grace window";
            Set("z", true, 80); Set("z", false, 81); Set("z", true, 82); Set("z", false, 100);
            if (Swallow(103)) return "re-release extends nothing beyond the last frame";
            Reset();
            return null;
        }
    }
}
