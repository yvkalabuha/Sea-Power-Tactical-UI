using System;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>Hang triage: the order/tactics lines ([Sar] / [Asw] / [Cap]) get the local wall time "HH:mm:ss.fff " in front (BepInEx lines have none).</summary>
    internal static class UiLogStamp
    {
        public static string Apply(string m, DateTime local)
        {
            if (m == null || m.Length < 5 || m[0] != '[' || m[4] != ']') return m;
            if (string.CompareOrdinal(m, 0, "[Sar]", 0, 5) != 0 && string.CompareOrdinal(m, 0, "[Asw]", 0, 5) != 0 && string.CompareOrdinal(m, 0, "[Cap]", 0, 5) != 0) return m;
            return local.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + m;
        }

        public static string SelfTest()
        {
            var t = new DateTime(2026, 10, 22, 23, 55, 14, 7);
            if (Apply("[Sar] order SAR-1 -> ok", t) != "23:55:14.007 [Sar] order SAR-1 -> ok") return "sar: " + Apply("[Sar] order SAR-1 -> ok", t);
            if (Apply("[Asw] x", t) != "23:55:14.007 [Asw] x" || Apply("[Cap] y", t) != "23:55:14.007 [Cap] y") return "asw/cap";
            if (Apply("rings: budget cut", t) != "rings: budget cut" || Apply("[Smart] z", t) != "[Smart] z") return "other lines must stay";
            if (Apply(null, t) != null || Apply("[Sa", t) != "[Sa") return "short";
            return null;
        }
    }
}
