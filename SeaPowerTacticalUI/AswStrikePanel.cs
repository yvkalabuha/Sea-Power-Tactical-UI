// T-173 / ADR-0074 item 2: the draft of the strike order ("Strike" mode of the ASW window): the guarded ship, the zone radius, the targets, the aircraft kept airborne.
// Pure model: no Unity, no game. Nothing is sent until "Start strike" / "Apply" (ADR-0049, decision Q3). Texts: UiText keys asw.lbl.* / asw.btn.strike_*.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal sealed class AswStrikeDraft
    {
        public int RefUid;                                   // 0 = the default (the ship of the first aircraft)
        public string RadiusText = "0", Targets = "hostile_unknown", AirborneText = "0";
        public string ForGroup, ForOrder;
        public bool Reload;                                  // set after a successful order: the next DraftFrom reloads the draft from the group
    }

    internal static class AswStrikePanel
    {
        private static string T(string key, params object[] args) { return UiText.Fmt(key, args); }

        private static string Fmt(double v) { return Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture); }

        private static double? Num(string s)
        {
            double d;
            return double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d) ? (double?)d : null;
        }

        public static AswStrikeDraft DraftFrom(AswGroupView g, AswStrikeDraft keep)
        {
            if (g == null) return keep ?? new AswStrikeDraft();
            if (keep != null && !keep.Reload && keep.ForGroup == g.Name && keep.ForOrder == g.Order) return keep;
            var d = new AswStrikeDraft { ForGroup = g.Name, ForOrder = g.Order };
            if (g.Order == "strike") { d.RefUid = g.RefUid; d.RadiusText = Fmt(g.StrikeRadiusNm); d.Targets = g.Targets ?? "hostile_unknown"; d.AirborneText = g.Airborne.ToString(CultureInfo.InvariantCulture); }
            else if (keep != null && keep.ForGroup == g.Name) { d.RefUid = keep.RefUid; d.RadiusText = keep.RadiusText; d.Targets = keep.Targets; d.AirborneText = keep.AirborneText; }
            return d;
        }

        /// <summary>T-178 A6: the radius of the zone is chosen from this list, miles (0 = no limit); the order accepts 0..150.</summary>
        public static readonly double[] RadiusList = { 0, 10, 20, 30, 50, 75, 100, 150 };

        public static double RadiusValue(string text) { double? d = Num(text); return d.HasValue && d.Value > 0 ? d.Value : 0; }

        public static double AirborneValue(string text) { double? d = Num(text); return d.HasValue ? d.Value : 0; }

        /// <summary>The two values of the "Targets" choice, in the order of the radio.</summary>
        public static readonly string[] TargetKinds = { "hostile_unknown", "hostile" };

        public static string[] TargetLabels() { return new[] { T("asw.targets.hostile_unknown"), T("asw.targets.hostile") }; }

        /// <summary>asw_order for the draft; err = bad_number.</summary>
        public static Dictionary<string, object> Command(AswGroupView g, AswStrikeDraft d, out string err)
        {
            err = null;
            if (g == null || d == null) { err = "no_group"; return null; }
            double? r = Num(d.RadiusText), a = Num(d.AirborneText);
            if (!r.HasValue || r.Value < 0 || r.Value > 150 || !a.HasValue || a.Value < 0 || a.Value > 4) { err = "bad_number"; return null; }
            var c = new Dictionary<string, object> { { "group", g.Name }, { "order", "strike" }, { "radius_nm", r.Value }, { "targets", d.Targets ?? "hostile_unknown" }, { "airborne", Math.Round(a.Value) } };
            if (d.RefUid != 0) c["ref_uid"] = (double)d.RefUid;
            return c;
        }

        public static bool SameAsOrder(AswGroupView g, AswStrikeDraft d)
        {
            if (g == null || g.Order != "strike" || d == null) return false;
            double? r = Num(d.RadiusText), a = Num(d.AirborneText);
            return (d.RefUid == 0 || d.RefUid == g.RefUid) && r.HasValue && Math.Abs(r.Value - g.StrikeRadiusNm) < 0.05 && (d.Targets ?? "hostile_unknown") == (g.Targets ?? "hostile_unknown") && a.HasValue && (int)Math.Round(a.Value) == g.Airborne;
        }

        public static UiButtonState Action(AswGroupView g, AswStrikeDraft d)
        {
            bool apply = g != null && g.Order == "strike";
            var s = new UiButtonState { Id = apply ? "apply" : "start_strike", Label = apply ? T("asw.btn.apply") : T("asw.btn.strike_start") };
            if (g == null) { s.Tip = T("asw.hint.no_group"); return s; }
            if (g.Members.Count == 0) { s.Tip = T("asw.hint.pick_first"); return s; }
            if (apply && SameAsOrder(g, d)) { s.Tip = T("asw.hint.same"); return s; }
            s.Enabled = true;
            if (!apply && g.Order != "none") s.Tip = T("asw.hint.replace");
            return s;
        }

        /// <summary>The status of one target of the table: "torpedo in water 2", "waiting", ...</summary>
        public static string StatusText(AswStrikeTargetView t)
        {
            return string.IsNullOrEmpty(t.StatusArg) ? UiText.Get(t.Status) : UiText.Fmt(t.Status, t.StatusArg);
        }

        public static string TaskText(AswRow r)
        {
            switch (r.StrikeTask)
            {
                case "target": return T("asw.strike.task.target", r.StrikeArg);
                case "deck": return T("asw.strike.task.deck", r.StrikeArg);
                default: return T("asw.strike.task.idle");
            }
        }

        public static string SelfTest()
        {
            var g = new AswGroupView { Name = "STRIKE-1", Order = "none" };
            g.Members.Add(1);
            var d = DraftFrom(g, null);
            string err;
            var c = Command(g, d, out err);
            if (err != null || (string)c["order"] != "strike" || (double)c["radius_nm"] != 0 || (string)c["targets"] != "hostile_unknown" || c.ContainsKey("ref_uid")) return "default command";
            if (!Action(g, d).Enabled || Action(g, d).Id != "start_strike") return "start state";
            d.RadiusText = "x";
            if (Command(g, d, out err) != null || err != "bad_number") return "bad number";
            d.RadiusText = "30"; d.RefUid = 7;
            c = Command(g, d, out err);
            if (err != null || (double)c["radius_nm"] != 30 || (double)c["ref_uid"] != 7) return "radius and ref";
            var o = new AswGroupView { Name = "STRIKE-1", Order = "strike", RefUid = 7, StrikeRadiusNm = 30, Targets = "hostile_unknown", Airborne = 0 };
            o.Members.Add(1);
            var d2 = DraftFrom(o, d);
            if (!SameAsOrder(o, d2) || Action(o, d2).Enabled || Action(o, d2).Id != "apply") return "same as order";
            d2.Targets = "hostile";
            if (SameAsOrder(o, d2) || !Action(o, d2).Enabled) return "dirty";
            if (Action(new AswGroupView { Name = "E", Order = "none" }, d).Enabled) return "empty group";
            return null;
        }
    }
}
