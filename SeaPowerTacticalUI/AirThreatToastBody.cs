using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>T-175 / ADR-0075 item 7: the body of the "Air threat" card (pure, drawn with the kit; no game, no Unity).</summary>
    internal static class AirThreatToastBody
    {
        private static string T(string key, params object[] a) { return UiText.Fmt(key, a); }

        public static string MmSs(double s)
        {
            if (double.IsInfinity(s) || double.IsNaN(s) || s < 0) return "-";
            int t = (int)Math.Round(s);
            return (t / 60).ToString(CultureInfo.InvariantCulture) + ":" + (t % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        public static string Title(AirThreatCard c) { return T(c != null && c.Resumed ? "airguard.threat.title_resumed" : "airguard.threat.title"); }

        /// <summary>"MiG-21bis x2, hostile" (the class is "unidentified" when it is not known).</summary>
        public static string RaidLine(AirThreatCard c)
        {
            string cls = string.IsNullOrEmpty(c.Cls) ? T("airguard.cls.unknown") : c.Cls;
            string side = T(c.Side == "unknown" ? "airguard.side.unknown" : "airguard.side.hostile");
            return T("airguard.threat.raid", cls, Math.Max(1, c.Count), side);
        }

        public static string MotionLine(AirThreatCard c)
        {
            return T("airguard.threat.motion", Math.Round(c.CourseDeg).ToString("0", CultureInfo.InvariantCulture), Math.Round(c.SpeedKn).ToString("0", CultureInfo.InvariantCulture));
        }

        /// <summary>"ASW-3: 36 miles, ETA about 2:10 - retreat under Yorktown".</summary>
        public static string GroupLine(AirThreatCardLine l)
        {
            string nm = Math.Round(l.Nm).ToString("0", CultureInfo.InvariantCulture);
            string eta = MmSs(l.EtaS);
            switch (l.Action)
            {
                case "cover": return T("airguard.line.cover", l.Group, nm, eta, l.ShipName ?? "-");
                case "land": return T("airguard.line.land", l.Group, nm, eta, l.ShipName ?? "-", l.Aboard);
                case "no_cover": return T("airguard.line.no_cover", l.Group, nm, eta);
                case "scramble": return T("airguard.line.alert_scramble", l.Group, Math.Max(1, l.Count), string.IsNullOrEmpty(l.TypeLabel) ? "-" : l.TypeLabel);   // T-179
                case "not_ready": return T("airguard.line.alert_not_ready", l.Group);
                case "preparing": return T("airguard.line.alert_preparing", l.Group, Math.Max(1, l.Count), string.IsNullOrEmpty(l.TypeLabel) ? "-" : l.TypeLabel);   // T-204
                default: return T("airguard.line.warn", l.Group, nm, eta);
            }
        }

        private static UiTone ToneOf(AirThreatCardLine l)
        {
            switch (l.Action)
            {
                case "no_cover": return UiTone.Error;
                case "not_ready": return UiTone.Error;
                case "scramble": return UiTone.Warn;
                case "preparing": return UiTone.Warn;
                case "warn": return UiTone.Normal;
                default: return UiTone.Warn;
            }
        }

        public static void Draw(UiKit k, AirThreatCard c, Action show, Action resume)
        {
            if (c == null) return;
            k.Text(Title(c), c.Resumed ? UiTone.Ok : UiTone.Warn, false, 1);
            if (c.Resumed)
            {
                string g = c.Groups.Count > 0 ? c.Groups[0].Group : "";
                k.Text(T("airguard.threat.resumed", g), UiTone.Ok, true, 2);
                return;
            }
            k.Text(RaidLine(c), UiTone.Warn, true, 2);
            k.Text(MotionLine(c), UiTone.Dim, true, 1);
            foreach (var l in c.Groups) k.Text(GroupLine(l), ToneOf(l), true, 2);
            if (!string.IsNullOrEmpty(c.TcLine)) k.Text(c.TcLine, UiTone.Dim, true, 2);
            string r = k.Actions(new UiAction("show", T("airguard.btn.show"), T("airguard.tip.show")), new UiAction("resume", T("airguard.btn.resume"), T("airguard.tip.resume")));
            if (r == "show" && show != null) show();
            else if (r == "resume" && resume != null) resume();
        }
    }
}
