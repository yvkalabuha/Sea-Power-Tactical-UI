// T-173 (ADR-0074): the "Strike" mode of the window (window-strike), the card "Submarine detected" (toast-dedupe, toast-layout), ru and en, scale 1 and 1.33.
using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    internal static class AswT173Tests
    {
        private static string T(string key) { return UiText.Get(key); }

        public static string Run()
        {
            var sb = new StringBuilder();
            foreach (string lang in new[] { "ru", "en" })
                using (UiText.Push(lang))
                    foreach (double s in new[] { 1.0, 1.33 })
                    {
                        string r = Modes(s) ?? Draft(s) ?? Targets(s) ?? ToastLayout(s);
                        if (r != null) sb.Append(lang + " s=" + s + ": " + r + "\n");
                    }
            string d = AswToastFeed.SelfTest() ?? AswStrikePanel.SelfTest() ?? ToastDedupe();
            if (d != null) sb.Append(d + "\n");
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static AswRow Row(int uid, string name, string state, int torps = 2)
        {
            return new AswRow { Uid = uid, Name = name, Type = "SH-60B", State = state, ShipUid = 700, Base = "Yorktown", FuelFrac = 0.8, Torpedoes = torps, Loadout = "ASWKiller", StrikeTask = "idle" };
        }

        private static AswGroupView Strike(string order)
        {
            var g = new AswGroupView { Name = "STRIKE-1", Order = order, Depth = "alternate", Status = order == "none" ? "none" : "searching", RefUid = 500, RefName = "Yorktown", Targets = "hostile_unknown", StrikeRadiusNm = 0 };
            g.Members.Add(1); g.Members.Add(2);
            g.MemberRows.Add(Row(1, "SH-60B 601", "air")); g.MemberRows.Add(Row(2, "SH-60B 602", "deck_prep"));
            g.MemberRows[0].StrikeTask = "target"; g.MemberRows[0].StrikeArg = "Sub #21";
            g.MemberRows[1].StrikeTask = "deck"; g.MemberRows[1].StrikeArg = "5:00";
            g.CourseTargets.Add(new AswTargetOption { Uid = 500, Name = "Yorktown" });
            return g;
        }

        private static AswView View(string order)
        {
            var v = new AswView();
            v.Groups.Add(Strike(order));
            v.CourseTargets.Add(new AswTargetOption { Uid = 500, Name = "Yorktown" });
            v.CourseTargets.Add(new AswTargetOption { Uid = 501, Name = "Houston" });
            v.AvailableStrike.Add(Row(30, "SH-60B 701", "deck_ready"));
            v.ExcludedStrike.Add(new AswExcludedRow { Row = Row(41, "SH-3H 699", "air", 0), Reason = "no_torpedo" });
            if (order == "strike")
            {
                var g = v.Groups[0];
                g.StrikeTargets.Add(new AswStrikeTargetView { ContactId = 21, Track = "Sub #21", Side = "hostile", Status = "asw.strike.st.in_water", StatusArg = "1", Shooter = "SH-60B 601", ShooterUid = 1, Torpedoes = 1, Hits = 0 });
                g.StrikeTargets.Add(new AswStrikeTargetView { ContactId = 22, Track = "Sub #22", Side = "unknown", Status = "asw.strike.st.waiting", Hits = -1 });
            }
            return v;
        }

        // four mode buttons in one row inside the window width; the strike mode opens for a strike group
        private static string Modes(double s)
        {
            var w = new Win(s, 520f, View("none"));
            w.Frame(); w.B.SelectedGroup = "STRIKE-1"; w.Frame(); w.Frame();
            float right = 0, y = -1;
            for (int i = 0; i < 4; i++)
            {
                var it = w.P.Find("mode." + i);
                if (it == null) return "mode button " + i + " missing";
                if (y < 0) y = it.R.Y; else if (Math.Abs(it.R.Y - y) > 0.01f) return "the mode buttons wrap";
                right = Math.Max(right, it.R.X + it.R.W);
            }
            if (right > 520f * (float)s + 1f && right > 520f) return "the mode row is wider than the window: " + right;
            if (AswWindowModel.OrderModes.Length != 4 || AswWindowModel.OrderModes[3] != "strike") return "OrderModes";
            int n = w.Sent.Count;
            w.Mode(3);
            if (w.B.Mode != "strike" || w.Sent.Count != n) return "the mode click: " + w.B.Mode + " sent " + w.Cmds();
            var running = new Win(s, 520f, View("strike"));
            running.Frame(); running.B.SelectedGroup = "STRIKE-1"; running.Frame(); running.Frame();
            if (running.B.Mode != "strike") return "a strike group does not open the strike mode";
            if (running.P.FindText(T("asw.col.task")) == null) return "no Task column";
            return null;
        }

        // the draft: nothing is sent until "Start strike"; the order carries the fields
        private static string Draft(double s)
        {
            var w = new Win(s, 520f, View("none"));
            w.Frame(); w.B.SelectedGroup = "STRIKE-1"; w.Frame(); w.Mode(3);
            if (!w.Has("start_strike")) return "no start button";
            w.Pick("radius", 4); w.Click("targets.1"); w.Click("airborne.1");   // T-178 A6: the radius is a list (50 is its 5th item), the airborne count segments
            if (w.Sent.Count != 0) return "the draft sent something: " + w.Cmds();
            w.Click("start_strike");
            if (w.Sent.Count != 1 || w.Sent[0] != "asw_order") return "start sent " + w.Cmds();
            var a = w.Args[0];
            if ((string)a["group"] != "STRIKE-1" || (string)a["order"] != "strike" || (double)a["radius_nm"] != 50 || (string)a["targets"] != "hostile" || (double)a["airborne"] != 1) return "order args";
            // T-178 A6: nothing is typed in the strike mode, so no invalid number can be entered (list radius, segments airborne, ship pick)
            var w2 = new Win(s, 520f, View("none"));
            w2.Frame(); w2.B.SelectedGroup = "STRIKE-1"; w2.Frame(); w2.Mode(3);
            if (w2.P.Of("Field").Count > 0) return "a text field in the strike mode: " + w2.P.Of("Field")[0].Id;
            return null;
        }

        // the targets table and the buttons of the ticked rows; the stop button
        private static string Targets(double s)
        {
            var w = new Win(s, 520f, View("strike"));
            w.Frame(); w.B.SelectedGroup = "STRIKE-1"; w.Frame(); w.Frame();
            if (!w.Has("strike_targets.chk.21")) return "no target row";
            w.Click("strike_targets.chk.21"); w.Click("skip");
            if (w.Sent.Count != 1 || w.Sent[0] != "asw_strike_skip" || (double)w.Args[0]["contact"] != 21) return "skip sent " + w.Cmds();
            w.Click("strike_targets.chk.22"); w.Click("resume");
            if (w.Sent.Count != 2 || w.Sent[1] != "asw_strike_resume" || (double)w.Args[1]["contact"] != 22) return "resume sent " + w.Cmds();
            if (!w.Has("apply")) return "no apply button of a running strike group";
            if (w.P.Find("apply").Enabled) return "apply is enabled without a change";
            w.Click("stop");
            if (w.Sent[w.Sent.Count - 1] != "asw_stop") return "stop " + w.Cmds();
            if (w.P.FindText(AswStrikePanel.StatusText(w.V.Groups[0].StrikeTargets[0])) == null) return "no status text";
            if (w.P.FindText(T("asw.excl.no_torpedo")) == null && w.P.FindText("SH-3H 699") == null) { /* the refusal list is a tip: nothing to see */ }
            return null;
        }

        // ---- the card ----

        private static AswSubAlertView Alert(string line, string episode = "detected", string[] args = null, string name = "Sub #7")
        {
            return new AswSubAlertView { ContactId = 7, Track = name, Side = "unknown", Class = "Kilo", Source = "live", RefName = "Yorktown", RefUid = 500, BrgDeg = 85, DistNm = 10.24, Serial = 1, Episode = episode, Line = line, LineArgs = args ?? new string[0] };
        }

        private static string ToastLayout(double s)
        {
            var lines = new[]
            {
                Alert("strike", "detected", new[] { "STRIKE-1", "SH-60B 601", "1:30" }),
                Alert("hold_ownsub", "hold_own_sub", new[] { "SSN Houston-very-long-name-of-a-submarine", "3.2" }),
                Alert("hold_neutral", "hold_neutral"), Alert("hold_air", "hold_air", new[] { "MiG-21 #12, 18 nm" }),
                Alert("none"), Alert("out_of_zone", "detected", new[] { "STRIKE-1", "42", "30" }), Alert("hostile_only"), Alert("no_armed"), Alert("disabled"),
                Alert("killed", "killed"), Alert("lost", "lost"), Alert("young", "detected", new[] { "60" })
            };
            foreach (var a in lines)
            {
                var w = new Win(s, 360f, new AswView());
                string line = AswToastBody.StatusLine(a);
                if (string.IsNullOrEmpty(line) || line.Contains("asw.toast.")) return "no text for " + a.Line;
                w.P.NextFrame(); w.K.Begin(360f);
                AswToastBody.Draw(w.K, a, null);
                w.K.End();
                foreach (var r in w.P.Items) if (r.R.X + r.R.W > 360f * (float)s + 1f && r.R.X + r.R.W > 361f) return a.Line + ": past the width " + (r.R.X + r.R.W);
                if (w.P.FindText(AswToastBody.Title()) == null) return a.Line + ": no title";
                if (!w.Has("show.7")) return a.Line + ": no show link";
            }
            var sum = new AswSubAlertView { ContactId = -1, Episode = "summary", Line = "summary", LineArgs = new[] { "3" }, Serial = 1 };
            if (!AswToastBody.StatusLine(sum).Contains("3")) return "summary text";
            // a click on "Show" asks the map
            var f = new Win(s, 360f, new AswView());
            AswSubAlertView got = null;
            f.P.NextFrame(); f.K.Begin(360f); AswToastBody.Draw(f.K, lines[0], x => got = x); f.K.End();
            f.P.Click("show.7"); f.P.NextFrame(); f.K.Begin(360f); AswToastBody.Draw(f.K, lines[0], x => got = x); f.K.End();
            if (got == null || got.ContactId != 7) return "the link did not focus";
            return null;
        }

        private static string ToastDedupe()
        {
            var m = new UiToastStackModel();
            var feed = new AswToastFeed();
            var p = new AswStrikeParams();
            var v = new AswView();
            for (int i = 1; i <= 4; i++) v.SubAlerts.Add(new AswSubAlertView { ContactId = i, Track = "Sub " + i, Serial = 1, Episode = "detected", Line = "none" });
            feed.Apply(v, m, p, 0);
            feed.Apply(v, m, p, 1); feed.Apply(v, m, p, 2);
            if (m.Visible().Count != 3 || m.Folded != 1) return "dedupe: " + m.Visible().Count + "/" + m.Folded;
            return null;
        }
    }
}
