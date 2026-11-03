using System;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-288 / ADR-0115: the body of the "sub launch area" card (pure: no game, no Unity).

    internal sealed class SubDatumCardModel
    {
        public int Id; public double BrgDeg, RNm, CoreNm, SigmaDeg, AgeS; public string Src, Ammo, State;
        public string Group; public double GroupEtaS = -1; public bool ReplacesArea;   // null Group = no group
        public string Searching; public double UndoLeftS;                              // after a click
        public bool AutoStarted;                                                       // Auto=idle
    }

    internal sealed class SubDatumCardActions { public Action Search, OpenWindow, Show, Hide, Undo; }

    internal static class SubDatumCard
    {
        private static string T(string key, params object[] a) { return UiText.Fmt(key, a); }
        private static string I(double v) { return Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture); }

        public static string Title() { return T("datum.title"); }

        public static string BearingLine(SubDatumCardModel m)
        {
            double brg = ((m.BrgDeg % 360) + 360) % 360;
            return T("datum.brg", I(brg), I(m.RNm), I(m.CoreNm));
        }

        private static string Age(double s)
        {
            if (s < 10) return T("datum.age_now");
            if (s < 60) return T("datum.age_s", I(s));
            return T("datum.age_min", I(Math.Floor(s / 60)));
        }

        public static string AgeShort(double s)
        {
            if (s < 10) return T("datum.age_short_now");
            if (s < 60) return T("datum.age_short_s", I(s));
            return T("datum.age_short_min", I(Math.Floor(s / 60)));
        }

        public static string AccuracyLine(SubDatumCardModel m)
        {
            string key = m.Src == "fixes" ? "datum.acc_fixes" : m.Src == "los" ? "datum.acc_los" : "datum.acc_vel";
            return T("datum.acc", T(key, I(m.SigmaDeg)), string.IsNullOrEmpty(m.Ammo) ? "?" : m.Ammo, Age(m.AgeS));
        }

        /// <summary>The caption at P0 on the map.</summary>
        public static string MapLabel(SubDatumCardModel m)
        {
            return T("datum.map_label", string.IsNullOrEmpty(m.Ammo) ? "?" : m.Ammo, I(m.SigmaDeg), AgeShort(m.AgeS));
        }

        public static string SearchLabel(SubDatumCardModel m)
        {
            string g = m.Group ?? "";
            if (m.ReplacesArea) return T("datum.btn.search_replace", g);
            if (m.GroupEtaS >= 0 && !double.IsInfinity(m.GroupEtaS)) return T("datum.btn.search_eta", g, I(Math.Ceiling(m.GroupEtaS / 60)));
            return T("datum.btn.search", g);
        }

        private static bool IsFound(SubDatumCardModel m) { return m.State == "found"; }
        private static bool Closed(SubDatumCardModel m) { return m.State == "found" || m.State == "expired"; }

        public static void Draw(UiKit k, SubDatumCardModel m, SubDatumCardActions a)
        {
            if (m == null) return;
            bool closed = Closed(m);
            k.Text(Title(), closed ? UiTone.Dim : UiTone.Warn, false, 1);
            k.Text(BearingLine(m), closed ? UiTone.Dim : UiTone.Normal, true, 2);
            k.Text(AccuracyLine(m), UiTone.Dim, true, 2);
            string undoLabel = null;
            UiAction show = new UiAction("show", T("datum.btn.show"), T("datum.tip.show"));
            UiAction hide = new UiAction("hide", T("datum.btn.hide"), T("datum.tip.hide"));
            string r;
            if (closed)
            {
                string stateKey = IsFound(m) ? "datum.state_found" : "datum.state_expired";
                k.Text(T(stateKey), UiTone.Dim, true, 2);
                r = k.Actions(show, hide);
            }
            else if (m.Searching != null)
            {
                bool auto = m.AutoStarted;
                k.Text(T(auto ? "datum.auto" : "datum.searching", m.Searching), UiTone.Ok, true, 2);
                if (auto) undoLabel = T("datum.btn.cancel");
                else if (m.ReplacesArea && m.UndoLeftS > 0) undoLabel = T("datum.btn.undo", I(Math.Ceiling(m.UndoLeftS)));
                r = undoLabel != null ? k.Actions(new UiAction("undo", undoLabel, T("datum.tip.undo")), show, hide) : k.Actions(show, hide);
            }
            else if (m.Group != null)
            {
                r = k.Actions(new UiAction("search", SearchLabel(m), T("datum.tip.search")), new UiAction("window", T("datum.btn.window"), T("datum.tip.window")), show, hide);
            }
            else
            {
                k.Text(T("datum.no_group"), UiTone.Dim, true, 2);
                r = k.Actions(new UiAction("window", T("datum.btn.window"), T("datum.tip.window")), show, hide);
            }
            if (a == null || r == null) return;
            Action act = r == "search" ? a.Search : r == "window" ? a.OpenWindow : r == "show" ? a.Show : r == "hide" ? a.Hide : r == "undo" ? a.Undo : null;
            if (act != null) act();
        }
    }
}
