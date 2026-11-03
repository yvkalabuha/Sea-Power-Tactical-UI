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

        /// <summary>T-314 (ADR-0121): protocol version of the call from SeaPowerTacticalUI.Loader.dll.</summary>
        public const int LoaderProtocol = 1;

        /// <summary>null = the loader speaks our protocol, else the reason text.</summary>
        public static string CheckLoaderProtocol(int protocol)
        {
            return protocol == LoaderProtocol ? null : "loader protocol " + protocol + " is not supported (mod expects " + LoaderProtocol + "); update the mod or the loader";
        }

        private static string NormDir(string d)
        {
            if (string.IsNullOrEmpty(d)) return "";
            return d.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }

        /// <summary>Same folder: '/' == '\', case-insensitive, trailing separators ignored; null == "".</summary>
        public static bool SameDir(string a, string b) { return NormDir(a) == NormDir(b); }

        /// <summary>true when {entry of other, entry} == {"loader", "anchorchain"} and SameDir(location of other, location): the two Workshop entries of one folder.</summary>
        public static bool IsQuietPair(string other, string entry, string location)
        {
            if (other == null) return false;
            var p = other.Split('|');
            if (p.Length < 3) return false;
            string oe = p[0];
            bool pair = (oe == "loader" && entry == "anchorchain") || (oe == "anchorchain" && entry == "loader");
            if (!pair) return false;
            return SameDir(string.Join("|", p, 2, p.Length - 2), location);
        }

        public static string DuplicateMessage(string other, string entry, string location)
        {
            if (IsQuietPair(other, entry, location))
            {
                var p = other.Split('|');
                return "Sea Power Tactical UI: already started by " + p[0] + " (" + string.Join("|", p, 2, p.Length - 2) + "); " + entry + " entry stays off";
            }
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
                if (CheckLoaderProtocol(LoaderProtocol) != null || CheckLoaderProtocol(2) == null) return "boot: loader protocol check";
                if (!SameDir("Z:\\a\\b\\", "z:/A/b") || SameDir("/a", "/b") || !SameDir(null, "")) return "boot: SameDir";
                if (!IsQuietPair("loader|1.0.0|/w", "anchorchain", "/w") || !IsQuietPair("anchorchain|1.0.0|/w", "loader", "/w/")) return "boot: quiet pair (both orders)";
                if (IsQuietPair("bepinex|1.0.0|/w", "anchorchain", "/w") || IsQuietPair("loader|1.0.0|/w", "bepinex", "/w")) return "boot: bepinex is never a quiet pair";
                if (IsQuietPair("loader|1.0.0|/w", "anchorchain", "/other")) return "boot: different folders are not a quiet pair";
                string quiet = DuplicateMessage("loader|1.0.0|/w", "anchorchain", "/w");
                if (quiet.Contains("Remove one of them") || !quiet.Contains("already started by loader")) return "boot: quiet pair message: " + quiet;
                if (!DuplicateMessage("bepinex|1.0.0|/a", "anchorchain", "/b").Contains("Remove one of them")) return "boot: the old message must stay for other pairs";
                return null;
            }
            finally { AppDomain.CurrentDomain.SetData(SlotKey, saved); }
        }
    }
}
