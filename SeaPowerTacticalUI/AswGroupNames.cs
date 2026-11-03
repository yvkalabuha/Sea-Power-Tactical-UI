using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-150 (B2): names of ASW / SAR groups. An automatic name is "<prefix>-<number>"; a name the player typed is never touched.
    internal static class AswGroupNames
    {
        public const int MaxLen = 24;

        public static string Auto(string prefix, int no) { return prefix + "-" + no.ToString(CultureInfo.InvariantCulture); }

        /// <summary>The number of "&lt;prefix&gt;-&lt;digits&gt;", 0 = not of that form (prefix must be one of the known ones when a list is given).</summary>
        public static int Number(string name, IEnumerable<string> prefixes)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int i = name.LastIndexOf('-');
            if (i <= 0 || i >= name.Length - 1) return 0;
            int n;
            if (!int.TryParse(name.Substring(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out n)) return 0;
            if (prefixes == null) return n;
            string pre = name.Substring(0, i);
            foreach (string p in prefixes) if (string.Equals(p, pre, StringComparison.OrdinalIgnoreCase)) return n;
            return 0;
        }

        public static bool IsAuto(string name, IEnumerable<string> prefixes) { return Number(name, prefixes) > 0; }

        /// <summary>The name after the order changed: an automatic name keeps its number and takes the prefix of the order (sar -> SAR-N, other -> ASW-N); a manual one stays.</summary>
        public static string ForOrder(string name, bool auto, string order, string aswPrefix, string sarPrefix, string strikePrefix = null)
        {
            if (!auto) return name;
            int n = Number(name, null);
            if (n <= 0) return name;
            return Auto(order == "sar" ? sarPrefix : order == "strike" && strikePrefix != null ? strikePrefix : aswPrefix, n);
        }

        /// <summary>null = ok | "bad_name" (empty, over 24 characters, control characters) | "name_taken".</summary>
        public static string Validate(string name, Func<string, bool> taken)
        {
            if (name == null) return "bad_name";
            string t = name.Trim();
            if (t.Length == 0 || t.Length > MaxLen) return "bad_name";
            foreach (char c in t) if (char.IsControl(c)) return "bad_name";
            if (taken != null && taken(t)) return "name_taken";
            return null;
        }

        public static string SelfTest()
        {
            if (Auto("ПЛО", 3) != "ПЛО-3") return "auto";
            if (ForOrder("ПЛО-3", true, "sar", "ПЛО", "SAR") != "SAR-3") return "to sar";
            if (ForOrder("SAR-3", true, "course", "ПЛО", "SAR") != "ПЛО-3") return "to asw";
            if (ForOrder("ПЛО-3", true, "strike", "ПЛО", "SAR", "УДАР") != "УДАР-3") return "to strike";
            if (ForOrder("УДАР-3", true, "course", "ПЛО", "SAR", "УДАР") != "ПЛО-3") return "strike to asw";
            if (ForOrder("Конвой", false, "sar", "ПЛО", "SAR") != "Конвой") return "manual stays";
            if (ForOrder("Конвой", true, "sar", "ПЛО", "SAR") != "Конвой") return "no number stays";
            var pre = new[] { "ПЛО", "ASW", "SAR" };
            if (!IsAuto("ASW-12", pre)) return "is auto";
            if (IsAuto("ASW-1a", pre)) return "is auto 1a";
            if (IsAuto("Конвой-2", pre)) return "is auto other prefix";
            if (IsAuto("ASW-", pre) || IsAuto("-5", pre)) return "is auto malformed";
            if (Validate("", s => false) != "bad_name") return "empty";
            if (Validate("   ", s => false) != "bad_name") return "blank";
            if (Validate(new string('x', 25), s => false) != "bad_name") return "long";
            if (Validate("a\tb", s => false) != "bad_name") return "control";
            if (Validate("Конвой", s => s == "Конвой") != "name_taken") return "taken";
            if (Validate("Конвой", s => false) != null) return "ok";
            return null;
        }
    }
}
