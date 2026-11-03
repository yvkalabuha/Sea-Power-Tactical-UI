// T-142 K5: the "by course" parts of the ASW window: the target (a ship or a formation = the course of its leader), the axis / replan status line,
// the loadout per vehicle type. Pure C#. The target and the loadouts are part of the order draft (ADR-0049 Q3): they go out only with "Start" / "Apply"
// (asw_loadout first, then the order). Sockets of AswWindowBody.cs.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>Pure helpers of the target picker and the loadout rows.</summary>
    internal static class AswCourseModel
    {
        public static string TargetLabel(AswTargetOption o)
        {
            if (o == null) return UiText.Get("asw.tgt.selected");
            return o.Kind == "formation" ? UiText.Fmt("asw.tgt.formation", o.Name) : o.Name;
        }

        /// <summary>Index of the option with this uid and kind, -1 = none.</summary>
        public static int IndexOf(IList<AswTargetOption> list, int uid, string kind)
        {
            if (list == null || uid == 0) return -1;
            for (int i = 0; i < list.Count; i++) if (list[i].Uid == uid && (list[i].Kind ?? "ship") == (kind ?? "ship")) return i;
            return -1;
        }

        /// <summary>The next / previous option (wraps); an unknown current starts at the first (dir &gt; 0) or the last one. null = empty list.</summary>
        public static AswTargetOption Cycle(IList<AswTargetOption> list, int curIndex, int dir)
        {
            if (list == null || list.Count == 0 || dir == 0) return null;
            int i = curIndex < 0 ? (dir > 0 ? 0 : list.Count - 1) : ((curIndex + dir) % list.Count + list.Count) % list.Count;
            return list[i];
        }

        /// <summary>Next / previous entry of ["auto", names...] (wraps). An unknown current gives "auto".</summary>
        public static string CycleLoadout(string current, IList<string> names, int dir)
        {
            var all = new List<string> { "auto" };
            if (names != null) foreach (string n in names) if (!string.IsNullOrEmpty(n) && !string.Equals(n, "auto", StringComparison.OrdinalIgnoreCase) && !all.Contains(n)) all.Add(n);
            int i = all.FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return all[0];
            return all[((i + dir) % all.Count + all.Count) % all.Count];
        }

        public static string LoadoutText(string v) { return string.IsNullOrEmpty(v) || string.Equals(v, "auto", StringComparison.OrdinalIgnoreCase) ? UiText.Get("asw.lo.auto") : v; }

        /// <summary>The chosen loadout of a type in the draft, else the one the group has.</summary>
        public static string LoadoutNow(AswOrderDraft d, AswLoadoutView row)
        {
            string v;
            return d != null && d.Loadouts.TryGetValue(row.Type, out v) && !string.IsNullOrEmpty(v) ? v : row.Chosen ?? "auto";
        }

        public static string SelfTest()
        {
            var l = new List<AswTargetOption> { new AswTargetOption { Uid = 1, Name = "A" }, new AswTargetOption { Uid = 2, Name = "B", Kind = "formation" }, new AswTargetOption { Uid = 3, Name = "C" } };
            if (IndexOf(l, 2, "formation") != 1 || IndexOf(l, 2, "ship") != -1 || IndexOf(l, 0, "ship") != -1 || IndexOf(l, 3, null) != 2) return "index";
            if (Cycle(l, 1, 1).Uid != 3 || Cycle(l, 2, 1).Uid != 1 || Cycle(l, 0, -1).Uid != 3 || Cycle(l, -1, 1).Uid != 1 || Cycle(l, -1, -1).Uid != 3) return "cycle";
            if (Cycle(l, 0, 0) != null || Cycle(new List<AswTargetOption>(), 0, 1) != null || Cycle(null, 0, 1) != null) return "cycle empty";
            var names = new[] { "ASWHunter", "ASWKiller" };
            if (CycleLoadout("auto", names, 1) != "ASWHunter" || CycleLoadout("ASWKiller", names, 1) != "auto" || CycleLoadout("auto", names, -1) != "ASWKiller") return "cycle loadout";
            if (CycleLoadout("zzz", names, 1) != "auto" || CycleLoadout("auto", null, 1) != "auto") return "cycle loadout unknown";
            return null;
        }
    }

    internal sealed partial class AswWindowBody
    {
        private static string Num0(double v) { return Math.Round(v).ToString("0", CultureInfo.InvariantCulture); }

        private static bool CourseOrder(AswGroupView sel, AswOrderDraft d) { return sel != null && sel.Order == "course" && d != null && d.Mode == "course"; }

        /// <summary>The target of the draft: an explicit pick, else the group's own target of a running course order. 0 = the ship selected in the game.</summary>
        private static void TargetOfDraft(AswGroupView sel, AswOrderDraft d, out int uid, out string kind)
        {
            if (d.TargetUid != 0) { uid = d.TargetUid; kind = d.TargetKind ?? "ship"; return; }
            if (CourseOrder(sel, d)) { uid = sel.TargetUid; kind = sel.TargetKind ?? "ship"; return; }
            uid = 0; kind = "ship";
        }

        partial void CourseTargetRow(UiKit k, AswGroupView sel, AswOrderDraft d)
        {
            if (sel == null) return;
            int uid; string kind;
            TargetOfDraft(sel, d, out uid, out kind);
            // T-178 A2 (ADR-0077 s.4): the target is a ship pick - own ships and formations (-leader uid) sorted by distance, "On the map" picks a ship by a click
            var geo = new List<double[]>();
            var items = ShipItems(_lastView, sel, true, geo);
            int cur = kind == "formation" ? -uid : uid;
            if (cur != 0 && uid == sel.TargetUid && !string.IsNullOrEmpty(sel.TargetName) && !items.Exists(x => x.Uid == cur))
            {
                items.Add(new ShipItem { Uid = cur, Name = sel.TargetName, Icon = "ntds:Surface:Own", DistNm = double.MaxValue, Own = true });   // the ship of the order is out of the list (lost): still shown
                geo.Add(null);
            }
            k.Row(Tx("asw.lbl.target"));
            int now = ShipRow(k, "tgt", "tgt", items, geo, cur, Tx("asw.tip.target_pick"));
            if (now == cur || now == 0) return;
            d.TargetUid = Math.Abs(now); d.TargetKind = now < 0 ? "formation" : "ship";
        }

        /// <summary>"axis 145 deg, re-plan in 12 s" (a segment of the one-line group status); null = nothing to say.</summary>
        internal static string AxisText(AswGroupView sel)
        {
            if (sel == null || sel.Order != "course" || sel.AxisDeg < 0) return null;
            string t = Tx("asw.status.axis", Num0(sel.AxisDeg));
            if (sel.ReplanInS >= 0) t += " \u00b7 " + Tx("asw.status.replan_in", Num0(Math.Ceiling(sel.ReplanInS)));
            return t;
        }

        partial void AddCourseArgs(AswGroupView sel, AswOrderDraft d, Dictionary<string, object> cmd)
        {
            if (d == null || cmd == null || d.TargetUid == 0) return;
            cmd["target_uid"] = (double)d.TargetUid;
            cmd["target_kind"] = d.TargetKind ?? "ship";
        }

        partial void LoadoutSection(UiKit k, AswGroupView sel, AswOrderDraft d)
        {
            if (sel == null || d == null || sel.Order == "sar") return;
            var sums = new List<string>();
            foreach (var r in sel.LoadoutRows) sums.Add(Tx("asw.sum.loadout", r.Label ?? r.Type, AswCourseModel.LoadoutText(AswCourseModel.LoadoutNow(d, r))));
            if (!k.Section("loadout", Tx("asw.sec.loadout"), null, false, sums.Count == 0 ? null : string.Join(" \u00b7 ", sums))) return;
            if (sel.LoadoutRows.Count == 0) { k.Hint(Tx("asw.lo.none")); return; }
            var labels = new List<string>();
            foreach (var r in sel.LoadoutRows) labels.Add(r.Label ?? r.Type);
            k.LabelColumn(labels.ToArray());
            foreach (var r in sel.LoadoutRows)
            {
                string cur = AswCourseModel.LoadoutNow(d, r);
                k.Row(r.Label ?? r.Type);
                int dir = k.Cycle("lo." + r.Type, AswCourseModel.LoadoutText(cur), Tx("asw.tip.loadout"));
                if (dir != 0) d.Loadouts[r.Type] = AswCourseModel.CycleLoadout(cur, r.Names, dir);
            }
        }

        private static bool LoadoutChanged(AswGroupView sel, AswOrderDraft d, AswLoadoutView r)
        {
            string v;
            if (!d.Loadouts.TryGetValue(r.Type, out v) || string.IsNullOrEmpty(v)) return false;
            return !string.Equals(v, r.Chosen ?? "auto", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TargetChanged(AswGroupView sel, AswOrderDraft d)
        {
            return d.TargetUid != 0 && (sel == null || sel.Order != "course" || d.TargetUid != sel.TargetUid || (d.TargetKind ?? "ship") != (sel.TargetKind ?? "ship"));
        }

        partial void AddApplyCommands(AswGroupView sel, AswOrderDraft d, List<UiCmd> cmds)
        {
            if (sel == null || d == null) return;
            // the loadout goes first: the order's re-plan then takes it
            foreach (var r in sel.LoadoutRows)
                if (LoadoutChanged(sel, d, r))
                    cmds.Add(new UiCmd { Name = "asw_loadout", Args = new Dictionary<string, object> { { "group", sel.Name }, { "type", r.Type }, { "loadout", d.Loadouts[r.Type] } } });
            // another target of a running course order without other geometry edits: the order is given again (the panel sends only set_tactics / depth for it)
            if (CourseOrder(sel, d) && !d.GeoDirty && TargetChanged(sel, d))
                cmds.Add(new UiCmd { Name = "asw_search_course", Args = AswOrderPanel.CourseOrderArgs(sel, d) });
        }

        partial void DraftDirtyExtra(AswGroupView sel, AswOrderDraft d, ref bool dirty)
        {
            if (sel == null || d == null) return;
            if (TargetChanged(sel, d) && sel.Order == d.Mode) dirty = true;
            foreach (var r in sel.LoadoutRows) if (LoadoutChanged(sel, d, r)) dirty = true;
        }
    }
}
