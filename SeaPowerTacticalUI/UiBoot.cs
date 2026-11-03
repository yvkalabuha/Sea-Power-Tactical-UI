using System;

namespace VoiceBotBridge.Ui
{
    /// <summary>
    /// T-105: one running copy per process. The slot lives in AppDomain data, so two copies of the DLL
    /// (one from each loader) see each other. The first copy wins. BCL only.
    /// </summary>
    internal static class UiBoot
    {
        public const string SlotKey = "ru.voicebot.seapower.ui/started";
        private static readonly object Lock = new object();

        private static string Line(string entry, string version, string location) { return entry + "|" + version + "|" + location; }

        /// <summary>true = this copy may start. false: other = "entry|version|location" of the running copy.</summary>
        public static bool TryClaim(string entry, string version, string location, out string other)
        {
            lock (Lock)
            {
                var cur = AppDomain.CurrentDomain.GetData(SlotKey) as string;
                if (!string.IsNullOrEmpty(cur)) { other = cur; return false; }
                AppDomain.CurrentDomain.SetData(SlotKey, Line(entry, version, location));
                other = null;
                return true;
            }
        }

        /// <summary>Frees the slot only if it holds this entry and location (a copy that never started must not free the winner's slot).</summary>
        public static void Release(string entry, string location)
        {
            lock (Lock)
            {
                var cur = AppDomain.CurrentDomain.GetData(SlotKey) as string;
                if (string.IsNullOrEmpty(cur)) return;
                var p = cur.Split('|');
                if (p.Length >= 3 && p[0] == entry && p[2] == (location ?? "")) AppDomain.CurrentDomain.SetData(SlotKey, null);
            }
        }

        public static string DuplicateMessage(string other, string entry, string location)
        {
            return "Sea Power Tactical UI: another copy is already running (" + other + "); this copy (" + entry + " " + location
                + ") stays off. Remove one of them.";
        }

        public static string SelfTest()
        {
            var saved = AppDomain.CurrentDomain.GetData(SlotKey);
            AppDomain.CurrentDomain.SetData(SlotKey, null);
            try
            {
                string other;
                if (!TryClaim("bepinex", "1.0.0", "/a", out other) || other != null) return "boot: the first claim must win";
                if (TryClaim("anchorchain", "1.0.0", "/b", out other)) return "boot: the second claim must lose";
                if (other != "bepinex|1.0.0|/a") return "boot: other must be the first line, got " + other;
                string msg = DuplicateMessage(other, "anchorchain", "/b");
                if (!msg.Contains("/a") || !msg.Contains("/b")) return "boot: message must hold both locations";
                Release("anchorchain", "/b");
                if (TryClaim("anchorchain", "1.0.0", "/b", out other)) return "boot: a foreign Release must not free the slot";
                Release("bepinex", "/a");
                if (AppDomain.CurrentDomain.GetData(SlotKey) != null) return "boot: Release of the owner must free the slot";
                if (!TryClaim("anchorchain", "1.0.0", "/b", out other)) return "boot: claim after Release must win";
                return null;
            }
            finally { AppDomain.CurrentDomain.SetData(SlotKey, saved); }
        }
    }
}
