// T-154 A3: one arbiter for MouseControlState.setMouseIsOverUIWindow. The game flag is shared, the owners are many (frames, drawing on the map,
// Overview, ASW): the flag is the OR of the owners and the game is called only on an edge of that OR. Pure (no Unity).
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class UiMouseBlock
    {
        private static readonly HashSet<string> Owners = new HashSet<string>();
        private static bool _applied;

        /// <summary>GameApi: setMouseIsOverUIWindow(on). Called only when the OR of the owners changes.</summary>
        public static Action<bool> Game;

        /// <summary>T-130: the owners that hold the flag, for the log line of the block watchdog.</summary>
        public static string OwnersText { get { return string.Join(",", new List<string>(Owners).ToArray()); } }

        private static readonly HashSet<string> Armed = new HashSet<string>();

        /// <summary>T-130: a draw mode is armed by the user (the block is on before the press and never re-asserted): the block watchdog must not release it.</summary>
        public static void Arm(string owner, bool on) { if (on) Armed.Add(owner); else Armed.Remove(owner); }
        internal static void ResetArmed() { Armed.Clear(); }
        public static bool AnyArmed { get { return Armed.Count > 0; } }

        /// <summary>The flag is on in the game: an owner holds it and no game menu suppresses it (the block watchdog reads this).</summary>
        public static bool Any { get { return Owners.Count > 0 && !_suppressed; } }

        private static bool _suppressed;

        /// <summary>A game menu is open (pause / options / AAR): the game flag goes off, the owners are kept and come back when it closes.</summary>
        public static bool Suppressed
        {
            get { return _suppressed; }
            set { if (_suppressed == value) return; _suppressed = value; Apply(); }
        }

        public static void Set(string owner, bool on)
        {
            if (string.IsNullOrEmpty(owner)) return;
            if (on) Owners.Add(owner); else Owners.Remove(owner);
            Apply();
        }

        public static bool IsOn(string owner) { return owner != null && Owners.Contains(owner); }

        /// <summary>Mission exit / Dispose: every owner is dropped, the flag goes off.</summary>
        public static void ReleaseAll()
        {
            Owners.Clear();
            Apply();
        }

        private static void Apply()
        {
            bool any = Owners.Count > 0 && !_suppressed;
            if (any == _applied) return;
            _applied = any;
            try { if (Game != null) Game(any); } catch { }
        }

        /// <summary>Test hook: drop all state.</summary>
        internal static void Reset() { Armed.Clear(); Owners.Clear(); _applied = false; _suppressed = false; Game = null; }

        public static string SelfTest()
        {
            var calls = new List<bool>();
            Reset();
            Game = b => calls.Add(b);
            Set("cap", true); Set("cap.draw", true);
            if (calls.Count != 1 || !calls[0]) return "two owners on: one call expected, got " + calls.Count;
            Set("cap", false);
            if (calls.Count != 1 || !Any) return "one owner left must keep the flag";
            Set("cap.draw", true);
            if (calls.Count != 1) return "repeat on must not call";
            Set("cap.draw", false);
            if (calls.Count != 2 || calls[1] || Any) return "last owner off must release";
            Set("asw", false);
            if (calls.Count != 2) return "off without owner must not call";
            Set("a", true); Set("b", true);
            ReleaseAll();
            if (calls.Count != 4 || calls[3] || Any) return "ReleaseAll";
            ReleaseAll();
            if (calls.Count != 4) return "ReleaseAll twice";
            // a game menu lifts the flag and keeps the owners; closing it gives the flag back
            Set("cap", true);
            if (calls.Count != 5 || !calls[4]) return "owner on before the menu";
            Suppressed = true;
            if (calls.Count != 6 || calls[5] || Any || !IsOn("cap")) return "menu must lift the flag and keep the owner";
            Set("asw", true);
            if (calls.Count != 6 || Any) return "no flag while the menu is open";
            Suppressed = true;
            if (calls.Count != 6) return "repeat suppress must not call";
            Suppressed = false;
            if (calls.Count != 7 || !calls[6] || !Any) return "menu closed must give the flag back";
            ReleaseAll();
            Suppressed = true; Suppressed = false;
            if (calls.Count != 8 || calls[7]) return "suppress without owners must not call";
            Reset();
            return null;
        }
    }
}
