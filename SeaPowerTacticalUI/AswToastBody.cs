using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>T-173 / ADR-0074 item 1: the body of the "Submarine detected" card (pure, drawn with the kit; no game, no Unity) and the feed that decides
    /// when the shared card stack (UiToastStackModel, T-175) shows or refreshes a card.</summary>
    internal static class AswToastBody
    {
        private static string T(string key, params object[] a) { return UiText.Fmt(key, a); }

        private static string Num(double v) { return Math.Round(v).ToString("0", CultureInfo.InvariantCulture); }

        public static string Title() { return T("asw.toast.title"); }

        /// <summary>"Sub #7 · hostile · Kilo · sonar" (class and source only when known).</summary>
        public static string IdLine(AswSubAlertView a)
        {
            var parts = new List<string>();
            parts.Add(a.Track ?? "");
            if (a.Side == "hostile") parts.Add(T("asw.toast.side_hostile")); else if (a.Side == "unknown") parts.Add(T("asw.toast.side_unknown"));
            if (!string.IsNullOrEmpty(a.Class)) parts.Add(a.Class);
            if (!string.IsNullOrEmpty(a.Source)) { string k = "asw.toast.src_" + a.Source; string s = UiText.Get(k); parts.Add(s == k ? a.Source : s); }
            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>"Bearing 85°, 10.2 nm from Yorktown"; empty when there is no reference ship.</summary>
        public static string BearingLine(AswSubAlertView a)
        {
            if (string.IsNullOrEmpty(a.RefName)) return "";
            return T("asw.toast.brg", Num(a.BrgDeg), Math.Round(a.DistNm, 1).ToString("0.#", CultureInfo.InvariantCulture), a.RefName);
        }

        /// <summary>The one status line of the card (ADR-0074 item 1.4): the key asw.toast.&lt;Line&gt; with the arguments of the controller.</summary>
        public static string StatusLine(AswSubAlertView a)
        {
            if (string.IsNullOrEmpty(a.Line)) return "";
            object[] args = new object[a.LineArgs == null ? 0 : a.LineArgs.Length];
            for (int i = 0; i < args.Length; i++) args[i] = a.LineArgs[i];
            return UiText.Fmt("asw.toast." + a.Line, args);
        }

        public static UiTone Tone(AswSubAlertView a)
        {
            switch (a.Episode)
            {
                case "killed": return UiTone.Ok;
                case "hold_own_sub": case "hold_neutral": case "hold_air": return UiTone.Warn;
                case "lost": return UiTone.Dim;
                default: return a.Line == "strike" ? UiTone.Ok : a.Line == "none" || a.Line == "no_armed" || a.Line == "no_shooter" || a.Line == "disabled" ? UiTone.Warn : UiTone.Normal;
            }
        }

        public static void Draw(UiKit k, AswSubAlertView a, Action<AswSubAlertView> focus)
        {
            if (a == null) return;
            k.Text(Title(), UiTone.Warn, false, 1);
            if (a.Episode == "summary") { k.Text(StatusLine(a), UiTone.Normal, true, 2); return; }
            k.Text(IdLine(a), UiTone.Normal, true, 2, IdLine(a));
            string b = BearingLine(a);
            if (b.Length > 0) k.Text(b, UiTone.Dim, true, 1, b);
            string s = StatusLine(a);
            if (s.Length > 0) k.Text(s, Tone(a), true, 2, s);
            if (k.Actions(new UiAction("show." + a.ContactId, T("airguard.btn.show"), null)) != null && focus != null) focus(a);
        }
    }

    /// <summary>Decides what the stack does with the alerts of the controller view: one card per track; a new show only on a new serial (hold by an own sub, destroyed,
    /// lost); a hold by a neutral or an air threat and the changes of the line only refresh the open card; Toast = false shows nothing.</summary>
    internal sealed class AswToastFeed
    {
        private readonly Dictionary<int, int> _shown = new Dictionary<int, int>();

        public static string KeyOf(AswSubAlertView a) { return a.ContactId < 0 ? "asw:summary" : "asw:" + a.ContactId.ToString(CultureInfo.InvariantCulture); }

        public void Apply(AswView v, IUiToasts toasts, AswStrikeParams p, double now, Action<AswSubAlertView> focus = null)
        {
            if (toasts == null || v == null || p == null || !p.Toast) return;
            var alive = new HashSet<int>();
            foreach (var a in v.SubAlerts)
            {
                alive.Add(a.ContactId);
                var alert = a;
                Action<UiKit> body = k => AswToastBody.Draw(k, alert, focus);
                string key = KeyOf(a);
                int seen;
                if (!_shown.TryGetValue(a.ContactId, out seen)) { _shown[a.ContactId] = a.Serial; toasts.Show(key, "asw_sub", body, now, p.ToastS, false); }
                else if (a.Serial > seen)
                {
                    _shown[a.ContactId] = a.Serial;
                    bool again = a.Episode == "hold_own_sub" || a.Episode == "killed" || a.Episode == "lost";
                    if (again) toasts.Show(key, "asw_sub", body, now, p.ToastS, true); else toasts.Update(key, body);
                }
                else toasts.Update(key, body);
            }
            foreach (int id in new List<int>(_shown.Keys)) if (!alive.Contains(id)) _shown.Remove(id);
        }

        public static string SelfTest()
        {
            var m = new UiToastStackModel();
            var feed = new AswToastFeed();
            var p = new AswStrikeParams();
            var v = new AswView();
            v.SubAlerts.Add(new AswSubAlertView { ContactId = 5, Track = "Sub #5", Serial = 1, Episode = "detected", Line = "none" });
            feed.Apply(v, m, p, 0);
            if (!m.Has("asw:5")) return "no card";
            m.Close("asw:5");
            feed.Apply(v, m, p, 5);
            if (m.Has("asw:5")) return "a closed card came back without a new serial";
            v.SubAlerts[0].Serial = 2; v.SubAlerts[0].Episode = "hold_neutral";
            feed.Apply(v, m, p, 6);
            if (m.Has("asw:5")) return "hold_neutral showed the card again";
            v.SubAlerts[0].Serial = 3; v.SubAlerts[0].Episode = "hold_own_sub";
            feed.Apply(v, m, p, 7);
            if (!m.Has("asw:5")) return "hold_own_sub did not show the card again";
            m.Close("asw:5");
            v.SubAlerts[0].Serial = 4; v.SubAlerts[0].Episode = "killed";
            feed.Apply(v, m, p, 8);
            if (!m.Has("asw:5")) return "killed did not show the card again";
            var off = new UiToastStackModel();
            var p2 = new AswStrikeParams { Toast = false };
            new AswToastFeed().Apply(v, off, p2, 0);
            if (off.Any) return "Toast=false showed a card";
            var many = new UiToastStackModel();
            var v3 = new AswView();
            for (int i = 1; i <= 5; i++) v3.SubAlerts.Add(new AswSubAlertView { ContactId = i, Serial = 1, Episode = "detected" });
            new AswToastFeed().Apply(v3, many, p, 0);
            if (many.Visible().Count != 3 || many.Folded != 2) return "three visible and +2: " + many.Visible().Count + "/" + many.Folded;
            var s = new UiToastStackModel();
            var v4 = new AswView(); v4.SubAlerts.Add(new AswSubAlertView { ContactId = -1, Serial = 1, Episode = "summary", Line = "summary", LineArgs = new[] { "3" } });
            var f4 = new AswToastFeed();
            f4.Apply(v4, s, p, 0); f4.Apply(v4, s, p, 1);
            if (!s.Has("asw:summary") || s.Visible().Count != 1) return "summary card";
            return null;
        }
    }
}
