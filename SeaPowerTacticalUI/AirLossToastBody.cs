using System;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>T-175 / ADR-0075 item 7: the body of the "Aircraft lost" card (pure).</summary>
    internal static class AirLossToastBody
    {
        private static string T(string key, params object[] a) { return UiText.Fmt(key, a); }

        private static string Dec(double v, string fmt)
        {
            string s = v.ToString(fmt, CultureInfo.InvariantCulture);
            return UiText.Lang == "ru" ? s.Replace('.', ',') : s;
        }

        /// <summary>"12,8N 43,28E".</summary>
        public static string PosText(double lat, double lon)
        {
            return Dec(Math.Abs(lat), "0.0") + (lat >= 0 ? "N" : "S") + " " + Dec(Math.Abs(lon), "0.00") + (lon >= 0 ? "E" : "W");
        }

        public static string CauseText(AirLossInfo i)
        {
            switch (i.Cause)
            {
                case "shot_down":
                    if (!string.IsNullOrEmpty(i.Weapon) && !string.IsNullOrEmpty(i.ByClass)) return T("airguard.loss.cause.shot_down_by", i.Weapon, i.ByClass);
                    if (!string.IsNullOrEmpty(i.Weapon)) return T("airguard.loss.cause.shot_down_weapon", i.Weapon);
                    return T("airguard.loss.cause.shot_down");
                case "crash": return T("airguard.loss.cause.crash");
                case "fuel": return T("airguard.loss.cause.fuel");
                default: return T("airguard.loss.cause.unknown");
            }
        }

        /// <summary>"Lost P-3C "Mad Foxes" (ASW-3): shot down by AA-2 (MiG-21bis), 12.8N 43.28E".</summary>
        public static string Text(AirLossInfo i)
        {
            string grp = string.IsNullOrEmpty(i.Group) ? "" : " (" + i.Group + ")";
            return T("airguard.loss.text", string.IsNullOrEmpty(i.Type) ? "?" : i.Type, i.Name ?? "", grp, CauseText(i), PosText(i.Lat, i.Lon));
        }

        public static string Title() { return T("airguard.loss.title"); }

        public static void Draw(UiKit k, AirLossInfo i)
        {
            if (i == null) return;
            k.Text(Title(), UiTone.Error, false, 1);
            k.Text(Text(i), UiTone.Normal, true, 3);
            if (i.Aboard > 0) k.Text(T("airguard.loss.aboard", i.Aboard), UiTone.Warn, true, 1);
        }
    }
}
