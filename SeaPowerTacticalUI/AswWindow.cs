using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>Map access for the ASW window and overlay without Unity types: affine (lat, lon) -> IMGUI pixels and the map rectangle.</summary>
    internal interface IAswMap
    {
        /// <summary>aff = {A,B,C,D,E,F}: x = A*lat + B*lon + C, y = D*lat + E*lon + F; rect = {x, y, w, h} of the map in IMGUI pixels.</summary>
        bool TryProjection(double refLat, double refLon, out double[] aff, out double[] rect);

        /// <summary>
        /// Mouse input: IMGUI pixel (y from the top) -> lat/lon through the map's LOCAL units, the space the layer draws in (T-142 W4);
        /// it does not depend on the screen projection used for the IMGUI fallback drawing.
        /// </summary>
        bool TryPixelToGeo(double px, double py, double refLat, double refLon, out double lat, out double lon);
    }

    /// <summary>T-095 / D1-D2: the pure part of the window (formatting, sorting, status line, drawing the area). Tested in aswtest.</summary>
    internal static class AswWindowModel
    {
        public static string MmSs(double s)
        {
            if (s < 0 || double.IsNaN(s) || double.IsInfinity(s)) return "--:--";
            int t = (int)Math.Round(s);
            return (t / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (t % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>T-180: the chip of a group in the retreat from an air threat (or with its launches held).</summary>
        public static string RetreatChipText(AswRetreatView r)
        {
            if (r == null) return "";
            string nm = r.Nm < 0 ? "?" : Math.Round(r.Nm).ToString("0", CultureInfo.InvariantCulture);
            if (r.Members == 0 && r.LaunchBlocked) return UiText.Fmt("asw.retreat.blocked", nm);
            if (r.ResumeWhen == "beyond") return UiText.Fmt("asw.retreat.chip.beyond", nm, Math.Round(r.ResumeNm).ToString("0", CultureInfo.InvariantCulture));
            return UiText.Fmt("asw.retreat.chip", nm);
        }

        public static string StateText(AswRow r)
        {
            if (r.RetreatTo != null) return r.RetreatTo.Length == 0 ? UiText.Get("asw.state.retreat.away") : UiText.Fmt("asw.state.retreat", r.RetreatTo);   // T-180
            if (AswMath.IsHangarRow(r.DeckKey) && r.HangarPrepS >= 0 && (r.State == "deck_ready" || r.State == "deck_service"))   // T-192: a hangar row is not "ready on deck"
                return r.HangarPrepS <= 1 ? UiText.Get("asw.state.hangar_ready") : UiText.Fmt("asw.state.hangar", UiText.Duration(r.HangarPrepS));
            switch (r.State)
            {
                case "air": case "deck_ready": case "deck_prep": case "deck_service": case "player": case "sar":
                case "game_patrol": case "deck_launching": case "deck_recover":
                {
                    string st = UiText.Fmt("asw.state." + r.State, UiText.Duration(r.ReadyInS));
                    return r.TakeoffInS >= 0 && (r.State == "deck_prep" || r.State == "deck_ready") ? st + " · " + UiText.Fmt("asw.state.takeoff", UiText.Duration(r.TakeoffInS)) : st;
                }
                default: return r.State ?? "";
            }
        }

        /// <summary>Window background alpha: opaque, so the map labels do not show through (customer 2026-10-20).</summary>
        public const float BackgroundAlpha = 1f;

        public static readonly string[] AllTactics = { "dip", "corridor", "barrier" };   // T-176: by course (the MAD snake is gone; rows / cross are the old names of corridor / barrier)

        /// <summary>Tactics of the "in the area" mode (T-142): buoys (rows), dipping sonar, MAD. "cross" belongs to the course mode only.</summary>
        public static readonly string[] AreaTactics = { "grid", "dip", "mad" };

        internal sealed class TacticOption { public string Tactic, Label, DisabledReason; public bool Enabled; }

        public static bool ShowCourseControls(string mode) { return mode == "course"; }
        public static bool ShowAreaControls(string mode) { return mode == "area"; }

        /// <summary>Label of a tactic in a mode: in the area mode "rows" are plain buoys.</summary>
        public static string TacticLabelFor(string mode, string t)
        {
            return mode == "area" && (t == "rows" || t == "grid") ? UiText.Get("asw.area.grid") : TacticLabel(t);
        }

        /// <summary>
        /// The tactic buttons of a mode. Course (dip, corridor, barrier) and area (grid, dip, MAD): buoys / dipping sonar / MAD, greyed by the group's composition
        /// (no group or no members yet: everything is enabled).
        /// </summary>
        public static List<TacticOption> TacticsFor(string mode, AswGroupView g)
        {
            var res = new List<TacticOption>();
            bool area = mode == "area";
            bool known = g != null && g.MemberRows.Count > 0;
            bool buoys = false, dip = false, mad = false;
            if (known) foreach (var r in g.MemberRows)
            {
                if (!r.LoadKnown) { buoys = dip = mad = true; break; }   // T-154: an unknown load is not a reason to grey a tactic
                if (r.Passive + r.ActiveBuoys > 0) buoys = true; if (r.Dipping) dip = true; if (r.Mad) mad = true;
            }
            foreach (string t in area ? AreaTactics : AllTactics)
            {
                var o = new TacticOption { Tactic = t, Label = TacticLabelFor(mode, t), Enabled = true };
                if (known)   // T-176: the chips of the course order are greyed by the composition too (a corridor / barrier needs buoys, the dip needs the sonar)
                {
                    if ((t == "grid" || t == "corridor" || t == "barrier") && !buoys) { o.Enabled = false; o.DisabledReason = UiText.Get("asw.tactic.no_buoys"); }
                    else if (t == "dip" && !dip) { o.Enabled = false; o.DisabledReason = UiText.Get("asw.tactic.no_dip"); }
                    else if (t == "mad" && !mad) { o.Enabled = false; o.DisabledReason = UiText.Get("asw.tactic.no_mad"); }
                }
                res.Add(o);
            }
            return res;
        }

        /// <summary>The selected tactic of the group in a mode; in the area mode "cross" is shown as "rows".</summary>
        public static string PrimaryTacticFor(string mode, AswGroupView g)
        {
            string t = PrimaryTactic(g);
            if (mode == "area" && (t == "cross" || t == "rows" || t == "corridor" || t == "barrier")) return "grid";
            if (mode == "course" && t == "rows") return "corridor";
            if (mode == "course" && t == "cross") return "barrier";
            return t;
        }

        /// <summary>First enabled tactic of the mode (for a new order when nothing is selected); null = none.</summary>
        public static string FirstEnabledTactic(string mode, AswGroupView g)
        {
            foreach (var o in TacticsFor(mode, g)) if (o.Enabled) return o.Tactic;
            return null;
        }

        public static string ExcludedText(string reason)
        {
            if (reason != null && (reason == "ext_sar" || reason == "game_sar_zone" || reason == "no_capacity" || reason.StartsWith("in_group:", StringComparison.Ordinal)))
                return SarWindow.ReasonText(reason);
            switch (reason)
            {
                case "no_asw_load": case "aew": case "player": case "sar": case "game_patrol": case "landed": case "lost": case "launching": case "recovering": case "no_torpedo": case "not_aircraft":
                    return UiText.Get("asw.excl." + reason);
                default: return reason ?? "";
            }
        }

        // ---------------------------------------------------------------- T-155: "Aircraft" / "Ship" links, why nothing is free

        /// <summary>True while the machine itself is a unit in the air (a click centres the map on it).</summary>
        private static bool Airborne(AswRow r)
        {
            return r != null && r.Uid > 0 && (r.State == "air" || r.State == "sar" || r.State == "game_patrol" || r.State == "player");
        }

        /// <summary>The unit the "Aircraft" link centres the map on: the machine in the air, else (on a deck) the ship it stands on; no ship known = the row's own uid.</summary>
        public static int AircraftFocusUid(AswRow r)
        {
            if (r == null) return 0;
            if (Airborne(r)) return r.Uid;
            return r.ShipUid != 0 ? r.ShipUid : r.Uid;
        }

        /// <summary>The unit the "Ship" link centres the map on; 0 = no link.</summary>
        public static int ShipFocusUid(AswRow r) { return r == null ? 0 : r.ShipUid; }

        /// <summary>The "Ship" cell: the ship name, "-" (em dash) when it is unknown or gone.</summary>
        public static string ShipText(AswRow r)
        {
            return r == null || r.ShipUid == 0 || string.IsNullOrEmpty(r.Base) ? "\u2014" : r.Base;
        }

        /// <summary>The reason of an excluded row as one short word group; every "in group X" counts as "in groups".</summary>
        private static string WhyKey(string reason) { return reason != null && reason.StartsWith("in_group:", StringComparison.Ordinal) ? "in_group" : reason ?? ""; }

        /// <summary>2026-10-22: the excluded rows as the selected group sees them: the members of the other groups stay ("in group STRIKE-4"),
        /// the group's own members (shown in its "Members" table) are dropped. null group = every row.</summary>
        public static List<AswExcludedRow> ExcludedSeenBy(IList<AswExcludedRow> excl, string group)
        {
            var res = new List<AswExcludedRow>();
            if (excl == null) return res;
            string own = string.IsNullOrEmpty(group) ? null : "in_group:" + group;
            foreach (var e in excl) if (e != null && (own == null || e.Reason != own)) res.Add(e);
            return res;
        }

        /// <summary>"Nothing is free: 2 in groups, 1 in the player's hands" (the line under an empty "Available"); null = nothing is excluded.</summary>
        public static string RefusalSummary(IList<AswExcludedRow> excl)
        {
            if (excl == null || excl.Count == 0) return null;
            var order = new List<string>(); var count = new Dictionary<string, int>();
            foreach (var e in excl)
            {
                string k = WhyKey(e.Reason);
                if (!count.ContainsKey(k)) { count[k] = 0; order.Add(k); }
                count[k]++;
            }
            var parts = new List<string>();
            foreach (string k in order)
            {
                string label = k == "in_group" ? UiText.Get("asw.why.in_group") : ExcludedText(k);
                parts.Add(count[k].ToString(CultureInfo.InvariantCulture) + " " + label);
            }
            return UiText.Fmt("asw.hint.no_free_why", string.Join(", ", parts));
        }

        /// <summary>The full list for the tip: "aircraft (ship) - reason", at most max lines, then "... N more".</summary>
        public static string RefusalList(IList<AswExcludedRow> excl, int max)
        {
            if (excl == null || excl.Count == 0) return null;
            var lines = new List<string>();
            for (int i = 0; i < excl.Count && i < max; i++) lines.Add(RefusalLine(excl[i]));
            if (excl.Count > max) lines.Add(UiText.Fmt("asw.why.more", (excl.Count - max).ToString(CultureInfo.InvariantCulture)));
            return string.Join("\n", lines);
        }

        /// <summary>"SH-3H (Yorktown) - in the player's hands".</summary>
        public static string RefusalLine(AswExcludedRow e)
        {
            string ship = e.Row == null || string.IsNullOrEmpty(e.Row.Base) ? "" : " (" + e.Row.Base + ")";
            return (e.Row == null ? "" : e.Row.Name) + ship + " \u2014 " + ExcludedText(e.Reason);
        }

        /// <summary>The strike group in one line: "STRIKE-1 · strike · Yorktown · targets 2 · torpedoes in the water 1".</summary>
        public static string StrikeLine(AswGroupView g, bool withName = true)
        {
            var sb = new StringBuilder();
            if (withName) sb.Append(g.Name).Append(" \u00b7 ");
            sb.Append(UiText.Get("asw.mode.strike"));
            if (!string.IsNullOrEmpty(g.RefName)) sb.Append(" \u00b7 ").Append(g.RefName);
            sb.Append(" \u00b7 ").Append(UiText.Get(g.Targets == "hostile" ? "asw.targets.hostile" : "asw.targets.hostile_unknown"));
            int n = 0; foreach (var t in g.StrikeTargets) if (!t.Skipped && !t.Status.EndsWith(".killed", StringComparison.Ordinal)) n++;
            if (g.StrikeTargets.Count > 0) sb.Append(" \u00b7 ").Append(UiText.Get("asw.sec.contacts")).Append(' ').Append(n.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static string OrderText(AswGroupView g)
        {
            if (g.Order == "sar") return SarWindow.OrderButtons()[2];
            if (g.Order == "strike") return UiText.Get("asw.mode.strike");
            return UiText.Get(g.Order == "course" ? "asw.order.course" : g.Order == "area" ? "asw.order.area" : "asw.order.none");
        }

        /// <summary>Top line of the window: "ПЛО-1 · по курсу Yorktown · ГАС+2 ряда · дыра 12 мин".</summary>
        public static string TaskLine(AswGroupView g)
        {
            if (g == null) return UiText.Get("asw.task.nogroup");
            if (g.Order == "sar") return SarTaskLine(g);
            if (g.Order == "strike") return StrikeLine(g);
            var sb = new StringBuilder(g.Name);
            sb.Append(" · ").Append(OrderText(g));
            if (g.Order != "none" && !string.IsNullOrEmpty(g.TargetName) && g.Order == "course") sb.Append(' ').Append(g.TargetName);
            else if (g.Order == "area" && !string.IsNullOrEmpty(g.TargetText)) sb.Append(' ').Append(g.TargetText);
            if (g.Tactics.Count > 0)
            {
                sb.Append(" · ");
                for (int i = 0; i < g.Tactics.Count; i++) { if (i > 0) sb.Append('+'); sb.Append(TacticLabelFor(g.Order, g.Tactics[i])); }
            }
            if (g.GapMin > 0) sb.Append(" · ").Append(UiText.Fmt("asw.task.gap", Math.Round(g.GapMin).ToString(CultureInfo.InvariantCulture)));
            return sb.ToString();
        }

        /// <summary>
        /// The tactic choice in the window is a switch: ONE primary tactic per group. Several tactics are possible through the protocol
        /// (different aircraft of one group: a dipping helicopter plus a buoy barrier by another), but the window keeps it simple: for
        /// several tactics make several groups. A group that holds several (from voice / protocol) shows the first as selected.
        /// </summary>
        public static string PrimaryTactic(AswGroupView g) { return g != null && g.Tactics.Count > 0 ? g.Tactics[0] : null; }

        /// <summary>The command for a click on the tactic radio button; null when the group is unknown or the tactic is not offered.</summary>
        public static Dictionary<string, object> SetTacticCommand(string group, string tactic)
        {
            if (group == null || (Array.IndexOf(AllTactics, tactic) < 0 && Array.IndexOf(AreaTactics, tactic) < 0)) return null;
            return new Dictionary<string, object> { { "group", group }, { "tactics", new List<object> { tactic } } };
        }

        public static Dictionary<string, object> DisbandCommand(string group)
        {
            return group == null ? null : new Dictionary<string, object> { { "op", "disband" }, { "group", group } };
        }

        public static string MemberTacticText(AswRow r)
        {
            return string.IsNullOrEmpty(r.Tactic) ? "—" : r.Tactic == "reserve" ? UiText.Get("asw.tactic.reserve") : TacticLabelFor(r.Order, r.Tactic);
        }

        /// <summary>T-155: the short tactic for the narrow table cell ("dip", "2+2", "barrier", "MAD", "reserve"); the full text stays in MemberTacticText.</summary>
        public static string MemberTacticCell(AswRow r)
        {
            if (string.IsNullOrEmpty(r.Tactic)) return "\u2014";
            if (r.Order == "area" && (r.Tactic == "rows" || r.Tactic == "grid")) return UiText.Get("asw.cell.grid");   // T-172: an area order lays buoys (the lattice), not the rows of a corridor
            switch (r.Tactic)
            {
                case "rows": return UiText.Get("asw.cell.corridor");
                case "cross": return UiText.Get("asw.cell.barrier");
                case "dip": case "corridor": case "barrier": case "grid": case "mad": case "reserve": return UiText.Get("asw.cell." + r.Tactic);
                default: return r.Tactic;
            }
        }

        /// <summary>T-155: the sensors for the narrow table cell.</summary>
        public static string SensorsCell(AswRow r)
        {
            return (r.Dipping ? UiText.Get("asw.cell.dip") : "") + (r.Dipping && r.Mad ? " " : "") + (r.Mad ? "MAD" : "");
        }

        public static string BuoysText(AswRow r)
        {
            return r.Passive.ToString(CultureInfo.InvariantCulture) + "/" + r.ActiveBuoys.ToString(CultureInfo.InvariantCulture) + "/" + r.Vlad.ToString(CultureInfo.InvariantCulture);
        }

        public static string FuelText(AswRow r)
        {
            return Math.Round(Math.Max(0, Math.Min(1, r.FuelFrac)) * 100).ToString(CultureInfo.InvariantCulture) + "% · " + UiText.Fmt("asw.fuel.minutes", Math.Round(r.MinutesOnStation).ToString(CultureInfo.InvariantCulture));
        }

        public static string SensorsText(AswRow r)
        {
            return (r.Dipping ? UiText.Get("asw.tactic.dip") : "") + (r.Dipping && r.Mad ? " " : "") + (r.Mad ? "MAD" : "");
        }

        public static string TacticLabel(string t)
        {
            switch (t)
            {
                case "rows": return UiText.Get("asw.tactic.corridor");
                case "cross": return UiText.Get("asw.tactic.barrier");
                case "dip": case "corridor": case "barrier": case "mad": return UiText.Get("asw.tactic." + t);
                default: return t;
            }
        }

        /// <summary>Sort key columns: name | type | state | fuel | group. Stable, unknown column = by name.</summary>
        public static void SortRows(List<AswRow> rows, string col, bool asc)
        {
            Comparison<AswRow> cmp;
            switch (col)
            {
                case "type": cmp = (a, b) => string.CompareOrdinal(a.Type, b.Type); break;
                case "state": cmp = (a, b) => StateRank(a).CompareTo(StateRank(b)); break;
                case "fuel": cmp = (a, b) => a.FuelFrac.CompareTo(b.FuelFrac); break;
                case "group": cmp = (a, b) => string.CompareOrdinal(a.Group ?? "", b.Group ?? ""); break;
                default: cmp = (a, b) => string.CompareOrdinal(a.Name, b.Name); break;
            }
            var idx = new List<KeyValuePair<int, AswRow>>();
            for (int i = 0; i < rows.Count; i++) idx.Add(new KeyValuePair<int, AswRow>(i, rows[i]));
            idx.Sort((x, y) => { int c = cmp(x.Value, y.Value); if (!asc) c = -c; return c != 0 ? c : x.Key.CompareTo(y.Key); });
            for (int i = 0; i < rows.Count; i++) rows[i] = idx[i].Value;
        }

        private static int StateRank(AswRow r)
        {
            switch (r.State)
            {
                case "air": return 0;
                case "deck_ready": return 1;
                case "deck_prep": return 2;
                case "deck_service": return 3;
                default: return 4;
            }
        }

        public static string RSourceText(string src)
        {
            if (string.IsNullOrEmpty(src)) return "—";
            if (src.StartsWith("clamped:", StringComparison.Ordinal)) return UiText.Fmt("asw.rsource.clamped", RSourceText(src.Substring(8)));
            switch (src)
            {
                case "game": case "ini": case "active": return UiText.Get("asw.rsource." + src);
                default: return src;
            }
        }

        public static string StatusLine(AswGroupView g)
        {
            var sb = new StringBuilder();
            sb.Append(g.Name).Append(": ");
            if (g.Order == "sar") return sb.Append(SarStatusLine(g)).ToString();
            if (g.Order == "strike") return sb.Append(StrikeLine(g, false)).ToString();
            if (g.Order == "none") return sb.Append(UiText.Get("asw.order.none")).ToString();
            sb.Append(UiText.Get(g.Order == "course" ? "asw.order.course" : "asw.order.area"));
            if (!string.IsNullOrEmpty(g.TargetName)) sb.Append(" · ").Append(g.TargetName);
            if (g.RNm > 0) sb.Append(" · ").Append(UiText.Fmt("asw.status.radius", g.RNm.ToString("0.0", CultureInfo.InvariantCulture), RSourceText(g.RSource)));
            sb.Append(" · ").Append(UiText.Fmt("asw.status.buoys", g.BuoysInWater));
            if (g.Order == "area" && g.CoveredFrac < 1) sb.Append(" · ").Append(UiText.Fmt("asw.status.covered", Math.Round(g.CoveredFrac * 100).ToString(CultureInfo.InvariantCulture)));
            if (g.NextBarrierInS >= 0) sb.Append(" · ").Append(UiText.Fmt("asw.status.barrier", MmSs(g.NextBarrierInS)));
            if (g.ReliefInS > 0) sb.Append(" · ").Append(UiText.Fmt("asw.status.gap", Math.Round(g.ReliefInS).ToString(CultureInfo.InvariantCulture)));
            if (g.Contacts.Count > 0) sb.Append(" · ").Append(UiText.Fmt("asw.status.contact", g.Contacts.Count));
            return sb.ToString();
        }

        public static string WarningText(string w)
        {
            if (w == null) return "";
            string code = w, rest = "";
            int sp = w.IndexOf(' ');
            if (sp > 0) { code = w.Substring(0, sp); rest = w.Substring(sp); }
            int col = code.IndexOf(':');
            if (col > 0) code = code.Substring(0, col);
            switch (code)
            {
                case "continuity_gap": case "buoys_low": case "tc_blocked": case "player_took": case "game_rtb": case "sar_took": case "no_aircraft":
                case "target_lost": case "restored": case "member_lost": case "hangar_taken":
                case "no_cover": case "cover_path_risk": case "cover_radar_off":   // T-175 (ADR-0075 item 2): the retreat of an aircraft under an air threat
                case "buoys_short": case "corridor_too_wide": case "life_short": case "barrier_rows_short": case "barrier_late": case "deck_down": case "area_done": case "tactic_removed":   // T-176 (ADR-0076 s.9)
                    return UiText.Get("asw.warn." + code) + rest;
                case "removed_unfit": case "game_sar_zone": case "no_ammo": case "low_capacity": case "no_capacity": case "no_reserve_deck": case "launch_failed": case "sar_off":
                    return UiText.Get("sar.warn." + code) + rest;
                default: return w;
            }
        }

        // ---- T-109: SAR order, rows, commands (strings: UiText keys sar.*) ------------------------------------------------

        public static readonly string[] OrderModes = { "course", "area", "sar", "strike" };

        /// <summary>The three order buttons of the window: course | area | sar (labels: SarWindow.OrderButtons).</summary>
        public static string[] OrderButtonTexts() { var b = SarWindow.OrderButtons(); return new[] { b[0], b[1], UiText.Get("asw.tab.sar"), UiText.Get("asw.tab.strike") }; }   // T-155: the third tab is short ("Rescue"), the full name is in its tip

        /// <summary>The mode in the summary bar of the collapsed "Order" section (T-174).</summary>
        public static string ModeSummary(string mode) { return UiText.Get(mode == "area" ? "asw.mode.area" : mode == "sar" ? "asw.mode.sar" : mode == "strike" ? "asw.mode.strike" : "asw.mode.course"); }

        /// <summary>The mode a group is in (course|area|sar); a group without an order keeps the window choice.</summary>
        public static string ModeOf(AswGroupView g, string fallback)
        {
            return g != null && (g.Order == "course" || g.Order == "area" || g.Order == "sar" || g.Order == "strike") ? g.Order : fallback;
        }

        public static string SarAreaText(AswGroupView g)
        {
            switch (g.SarArea)
            {
                case "rect": return UiText.Get("sar.area.rect");
                case "circle": return UiText.Fmt("sar.area.circle", Math.Round(g.SarRadiusNm, 1).ToString("0.#", CultureInfo.InvariantCulture));
                default: return UiText.Get("sar.area.all");
            }
        }

        public static string SarOnCompleteText(string oc)
        {
            return UiText.Get(oc == "loiter" ? "sar.complete.loiter" : "sar.complete.rtb");
        }

        public static string SarTaskLine(AswGroupView g)
        {
            return SarWindow.TaskLine(new SarGroupLine
            {
                Group = g.Name, AreaText = SarAreaText(g) + " · " + SarOnCompleteText(g.SarOnComplete),
                Rafts = g.SarRafts, People = g.SarPeople, Active = g.SarActive, Rescued = g.SarRescued, NextLaunchS = g.SarNextLaunchS
            });
        }

        public static string SarStatusText(string st)
        {
            switch (st)
            {
                case "searching": case "idle_no_rafts": case "no_capacity": case "no_reserve_deck": case "sar_off": case "empty": return UiText.Get("sar.status." + st);
                default: return st ?? "";
            }
        }

        public static string SarStatusLine(AswGroupView g)
        {
            var sb = new StringBuilder(OrderText(g));
            sb.Append(" · ").Append(SarAreaText(g)).Append(" · ").Append(SarOnCompleteText(g.SarOnComplete));
            if (!string.IsNullOrEmpty(g.Status) && g.Status != "none") sb.Append(" · ").Append(SarStatusText(g.Status));
            return sb.ToString();
        }

        /// <summary>Cells "На борту" and "Цель" of a member row.</summary>
        public static string SarOnboardText(AswRow r) { return SarWindow.OnboardCell(r.Onboard, r.Capacity); }
        public static string SarTargetText(AswRow r) { return SarWindow.TargetCell(r.TargetRaft, r.TargetDistNm); }

        /// <summary>sar_order for the group; area: "all" | "rect" (rect = 8 numbers lat/lon of corners) | "circle" (lat, lon, radiusNm). Null when the group is unknown or the area is incomplete.</summary>
        public static Dictionary<string, object> SarOrderCommand(string group, string kind, double[] rect8, double lat, double lon, double radiusNm, string onComplete)
        {
            if (string.IsNullOrEmpty(group)) return null;
            var cmd = new Dictionary<string, object> { { "group", group }, { "on_complete", onComplete == "loiter" ? "loiter" : "rtb" } };
            if (kind == "rect")
            {
                if (rect8 == null || rect8.Length != 8) return null;
                var l = new List<object>();
                foreach (double d in rect8) l.Add(d);
                cmd["area"] = l;
            }
            else if (kind == "circle")
            {
                if (radiusNm <= 0) return null;
                cmd["area"] = new Dictionary<string, object> { { "lat", lat }, { "lon", lon }, { "radius_nm", radiusNm } };
            }
            else cmd["area"] = "all";
            return cmd;
        }

        /// <summary>Same area as the group has now, only on_complete changes (the toggle "go home / wait").</summary>
        public static Dictionary<string, object> SarOnCompleteCommand(AswGroupView g, string onComplete)
        {
            if (g == null) return null;
            if (g.SarArea == "rect" && g.SarRect != null && g.SarRect.Length == 4)
                return SarOrderCommand(g.Name, "rect", new[] { g.SarRect[0], g.SarRect[1], g.SarRect[0], g.SarRect[3], g.SarRect[2], g.SarRect[3], g.SarRect[2], g.SarRect[1] }, 0, 0, 0, onComplete);
            if (g.SarArea == "circle") return SarOrderCommand(g.Name, "circle", null, g.SarLat, g.SarLon, g.SarRadiusNm, onComplete);
            return SarOrderCommand(g.Name, "all", null, 0, 0, 0, onComplete);
        }

        public static string SarSelfTest()
        {
            using (UiText.Push("ru")) return SarSelfTestRu();
        }

        private static string SarSelfTestRu()
        {
            {
                var g = new AswGroupView { Name = "SAR-1", Order = "sar", SarArea = "all", SarOnComplete = "rtb", SarRafts = 5, SarPeople = 23, SarActive = 2, SarRescued = 11, SarNextLaunchS = 200, Status = "searching" };
                if (TaskLine(g) != "SAR-1 · везде · затем домой · плотов 5 (людей 23) · вертолётов в работе 2 · спасено 11 · взлёт через 03:20") return "sar task ru: " + TaskLine(g);
                g.SarNextLaunchS = -1; g.SarArea = "circle"; g.SarRadiusNm = 20; g.SarOnComplete = "loiter";
                if (TaskLine(g) != "SAR-1 · вокруг точки, 20 миль · затем зависнуть над кораблём · плотов 5 (людей 23) · вертолётов в работе 2 · спасено 11 · взлёт через —") return "sar task circle: " + TaskLine(g);
                using (UiText.Push("en"))
                {
                    if (TaskLine(g) != "SAR-1 · around a point, 20 nm · then hover over the ship · rafts 5 (people 23) · helicopters at work 2 · rescued 11 · launch in —") return "sar task en: " + TaskLine(g);
                    if (OrderText(g) != "Search and rescue" || OrderButtonTexts().Length != 4) return "sar order text";
                    if (WarningText("sar_off") != "SAR is off for the group" || ExcludedText("no_capacity") != "no seats for survivors") return "sar en texts";
                    if (UiText.Get("asw.btn.stop_all") != "Stop all" || UiText.Get("sar.ui.radius") != "radius") return "t147 en labels";
                    if (UiText.Get("sar.area.btn_rect") != "Rectangle on map" || UiText.Get("sar.area.btn_circle") != "Circle around point") return "t147 en area buttons";
                    if (WarningText("no_capacity") != "no SAR loadout on any deck of the group") return "t147 en no_capacity warn";
                    if (WarningText("no_reserve_deck (SAR-1)") != "no deck reserve: the group has no deck with a SAR loadout (SAR-1)" || SarStatusText("no_reserve_deck") != "no deck reserve") return "t147 en no_reserve_deck";
                }
                if (OrderText(g) != "Поиск и спасение" || OrderButtonTexts()[0] != "По курсу" || OrderButtonTexts()[1] != "В районе") return "sar buttons";
                if (StatusLine(g) != "SAR-1: Поиск и спасение · вокруг точки, 20 миль · затем зависнуть над кораблём · поиск") return "sar status: " + StatusLine(g);
                if (SarStatusText("idle_no_rafts") != "плотов нет, ждём" || SarStatusText("x") != "x") return "sar status text";
                if (ExcludedText("ext_sar") != "занят AutomaticSAR" || ExcludedText("no_capacity") != "нет мест для спасённых" || ExcludedText("game_sar_zone") != "занят SAR-зоной миссии"
                    || ExcludedText("in_group:ПЛО-1") != "в группе ПЛО-1") return "sar excluded: " + ExcludedText("in_group:ПЛО-1");
                if (ExcludedText("lost") != "потерян") return "sar excluded regress";
                var row = new AswRow { Onboard = 3, Capacity = 8, TargetRaft = 12, TargetDistNm = 4.3 };
                if (SarOnboardText(row) != "3/8" || SarTargetText(row) != "плот 12, 4.3 мили") return "sar row: " + SarTargetText(row);
                if (SarTargetText(new AswRow()) != "—") return "sar row none";
                if (WarningText("low_capacity (SAR-1)") != "мест для спасённых меньше, чем людей на плотах (SAR-1)") return "sar warn: " + WarningText("low_capacity (SAR-1)");
                if (WarningText("sar_off") != "SAR выключен для группы") return "sar warn off";
                if (UiText.Get("asw.btn.stop_all") != "Стоп всех" || UiText.Get("sar.ui.radius") != "радиус") return "t147 ru labels";
                if (UiText.Get("sar.area.btn_all") != "Везде" || UiText.Get("sar.area.btn_rect") != "Прямоугольник на карте" || UiText.Get("sar.area.btn_circle") != "Круг вокруг точки") return "t147 ru area buttons";
                if (WarningText("no_capacity") != "нет SAR-загрузки ни на одной палубе группы") return "t147 ru no_capacity warn";
                if (WarningText("no_reserve_deck (SAR-1)") != "резерв на палубе не готовится: у группы нет палубы с SAR-загрузкой (SAR-1)" || SarStatusText("no_reserve_deck") != "без резерва на палубе") return "t147 ru no_reserve_deck";
                if (ModeOf(g, "course") != "sar" || ModeOf(null, "area") != "area" || ModeOf(new AswGroupView { Order = "none" }, "course") != "course") return "mode";
                // commands
                var c = SarOrderCommand("SAR-1", "all", null, 0, 0, 0, "loiter");
                string err;
                var po = SarCommands.ParseOrder(c, out err);
                if (po == null || po.Area.Kind != "all" || po.OnComplete != "loiter") return "cmd all: " + err;
                c = SarOrderCommand("SAR-1", "rect", new[] { 35.0, 32.0, 35.0, 33.0, 34.0, 33.0, 34.0, 32.0 }, 0, 0, 0, "rtb");
                po = SarCommands.ParseOrder(c, out err);
                if (po == null || po.Area.Kind != "rect" || po.Area.Rect[0] != 34 || po.Area.Rect[3] != 33) return "cmd rect: " + err;
                c = SarOrderCommand("SAR-1", "circle", null, 35, 32, 15, "rtb");
                po = SarCommands.ParseOrder(c, out err);
                if (po == null || po.Area.Kind != "circle" || po.Area.RadiusNm != 15) return "cmd circle: " + err;
                if (SarOrderCommand(null, "all", null, 0, 0, 0, "rtb") != null || SarOrderCommand("g", "rect", null, 0, 0, 0, "rtb") != null || SarOrderCommand("g", "circle", null, 1, 1, 0, "rtb") != null) return "cmd bad";
                var gr = new AswGroupView { Name = "SAR-2", Order = "sar", SarArea = "rect", SarRect = new[] { 34.0, 32.0, 35.0, 33.0 } };
                po = SarCommands.ParseOrder(SarOnCompleteCommand(gr, "loiter"), out err);
                if (po == null || po.Area.Kind != "rect" || po.Area.Rect[0] != 34 || po.Area.Rect[2] != 35 || po.OnComplete != "loiter") return "cmd keep rect: " + err;
                gr.SarArea = "circle"; gr.SarLat = 35; gr.SarLon = 32; gr.SarRadiusNm = 7;
                po = SarCommands.ParseOrder(SarOnCompleteCommand(gr, "rtb"), out err);
                if (po == null || po.Area.Kind != "circle" || po.Area.RadiusNm != 7) return "cmd keep circle: " + err;
                return null;
            }
        }

        // ---- drawing the area with the mouse ----------------------------------------------------------------------

        /// <summary>Two opposite corners in any order -> {minX, minY, maxX, maxY}; false if the rectangle is smaller than minPx.</summary>
        public static bool NormalizeRect(double x0, double y0, double x1, double y1, double minPx, out double[] r)
        {
            r = new[] { Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1) };
            return r[2] - r[0] >= minPx && r[3] - r[1] >= minPx;
        }

        /// <summary>Inverse of the (lat, lon) -> pixels affine; false when singular.</summary>
        public static bool PixelToGeo(double[] aff, double x, double y, out double lat, out double lon)
        {
            lat = lon = 0;
            double det = aff[0] * aff[4] - aff[1] * aff[3];
            if (Math.Abs(det) < 1e-12) return false;
            double px = x - aff[2], py = y - aff[5];
            lat = (px * aff[4] - aff[1] * py) / det;
            lon = (aff[0] * py - aff[3] * px) / det;
            return true;
        }

        /// <summary>Screen rectangle -> 8 numbers lat/lon (top-left, top-right, bottom-right, bottom-left): the order the controller expects.</summary>
        public static double[] RectToGeo(double[] aff, double[] r)
        {
            return RectToGeo((x, y) => { double la, lo; return PixelToGeo(aff, x, y, out la, out lo) ? new[] { la, lo } : null; }, r);
        }

        /// <summary>Same with an arbitrary pixel -> {lat, lon} function (null = failed), e.g. IAswMap.TryPixelToGeo (T-142 W4).</summary>
        public static double[] RectToGeo(Func<double, double, double[]> toGeo, double[] r)
        {
            var res = new double[8];
            double[] xs = { r[0], r[2], r[2], r[0] }, ys = { r[1], r[1], r[3], r[3] };
            for (int i = 0; i < 4; i++)
            {
                var p = toGeo(xs[i], ys[i]);
                if (p == null) return null;
                res[2 * i] = p[0]; res[2 * i + 1] = p[1];
            }
            return res;
        }

        public static string SelfTest()
        {
            using (UiText.Push("ru"))
            {
                string e = SelfTestRu();
                if (e != null) return e;
            }
            using (UiText.Push("en")) return SelfTestEn();
        }

        /// <summary>English texts of the five model functions (the Russian expectations are in SelfTestRu).</summary>
        private static string SelfTestEn()
        {
            var air = new AswRow { Name = "B", State = "air", FuelFrac = 0.5, MinutesOnStation = 90.4, Passive = 12, ActiveBuoys = 8, Vlad = 4, Dipping = true, Mad = true };
            if (StateText(air) != "airborne" || FuelText(air) != "50% · 90 min" || SensorsText(air) != "dipping sonar MAD") return "en row text: " + StateText(air) + " | " + FuelText(air) + " | " + SensorsText(air);
            if (StateText(new AswRow { State = "deck_prep", ReadyInS = 600 }) != "preparing 10 min" || StateText(new AswRow { State = "deck_prep", ReadyInS = 600, TakeoffInS = 780 }) != "preparing 10 min · takeoff in 13 min" || StateText(new AswRow { State = "deck_ready", TakeoffInS = 120 }) != "on deck: ready · takeoff in 2 min") return "en deck text";
            var g = new AswGroupView { Name = "ASW-1", Order = "course", TargetName = "Yorktown", RNm = 2.7, RSource = "game", BuoysInWater = 12, NextBarrierInS = 90, GapMin = 12 };
            g.Tactics.Add("dip"); g.Tactics.Add("rows");
            string st = StatusLine(g);
            if (st != "ASW-1: on course · Yorktown · R 2.7 nm (game estimate) · buoys in water 12 · barrier in 01:30") return "en status: " + st;
            if (TaskLine(g) != "ASW-1 · on course Yorktown · dipping sonar+Corridor · gap 12 min") return "en task line: " + TaskLine(g);
            if (TaskLine(new AswGroupView { Name = "X", Order = "none" }) != "X · no order") return "en task none";
            if (WarningText("continuity_gap (ASW-1): 16 min") != "continuity not ensured (fewer than 3 aircraft) (ASW-1): 16 min") return "en warning: " + WarningText("continuity_gap (ASW-1): 16 min");
            if (ExcludedText("aew") != "AEW aircraft" || ExcludedText("lost") != "lost") return "en excluded";
            if (RSourceText("clamped:ini") != "clamped, 0.5×ini") return "en rsource";
            return null;
        }

        private static string TacticsSelfTest()
        {
            var gb = new AswGroupView { Name = "g", Order = "area" };
            gb.MemberRows.Add(new AswRow { Passive = 12 });
            var a = TacticsFor("area", gb);
            if (a.Count != 3 || a[0].Tactic != "grid" || a[1].Tactic != "dip" || a[2].Tactic != "mad") return "area tactics list";
            if (!a[0].Enabled || a[1].Enabled || a[2].Enabled) return "area tactics by composition";
            if (a[1].DisabledReason != "нет бортов с ГАС" || a[2].DisabledReason != "нет бортов с MAD") return "area disabled reasons: " + a[1].DisabledReason + " | " + a[2].DisabledReason;
            if (a[0].Label != "Сетка") return "area rows label " + a[0].Label;
            gb.MemberRows.Add(new AswRow { Dipping = true, Mad = true });
            foreach (var o in TacticsFor("area", gb)) if (!o.Enabled) return "area all enabled " + o.Tactic;
            var none = new AswGroupView { Name = "e" };
            foreach (var o in TacticsFor("area", none)) if (!o.Enabled) return "empty group enables all";
            foreach (var o in TacticsFor("area", null)) if (!o.Enabled) return "no group enables all";
            var nobuoy = new AswGroupView { Name = "n" }; nobuoy.MemberRows.Add(new AswRow { Dipping = true });
            if (TacticsFor("area", nobuoy)[0].Enabled || FirstEnabledTactic("area", nobuoy) != "dip") return "no buoys";
            var c = TacticsFor("course", gb);
            if (c.Count != 3 || c[0].Tactic != "dip" || c[1].Tactic != "corridor" || c[2].Tactic != "barrier") return "course tactics list";
            foreach (var o in c) if (!o.Enabled) return "course tactics enabled";
            var gc = new AswGroupView { Name = "x" }; gc.Tactics.Add("cross");
            if (PrimaryTacticFor("area", gc) != "grid" || PrimaryTacticFor("course", gc) != "barrier" || PrimaryTacticFor("area", null) != null) return "primary for mode";
            if (ShowCourseControls("area") || !ShowCourseControls("course") || ShowAreaControls("course") || !ShowAreaControls("area") || ShowAreaControls("sar") || ShowCourseControls("sar")) return "controls by mode";
            return null;
        }

        /// <summary>T-155: which unit the links centre on, the "Ship" cell and the line about the excluded rows.</summary>
        private static string LinksSelfTest()
        {
            var air = new AswRow { Uid = 11, State = "air", ShipUid = 7, Base = "Yorktown" };
            if (AircraftFocusUid(air) != 11 || ShipFocusUid(air) != 7 || ShipText(air) != "Yorktown") return "links air";
            foreach (string st in new[] { "sar", "game_patrol", "player" }) if (AircraftFocusUid(new AswRow { Uid = 12, State = st, ShipUid = 7 }) != 12) return "links airborne " + st;
            var deck = new AswRow { Uid = -5, State = "deck_ready", ShipUid = 7, DeckUid = 7, Base = "Yorktown" };
            if (AircraftFocusUid(deck) != 7 || ShipFocusUid(deck) != 7) return "links deck: a deck row centres on its ship";
            var unit = new AswRow { Uid = 13, State = "deck_launching", ShipUid = 7 };
            if (AircraftFocusUid(unit) != 7) return "links launching unit stands on the deck";
            var noShip = new AswRow { Uid = 14, State = "deck_ready", Base = "Gone" };
            if (AircraftFocusUid(noShip) != 14 || ShipFocusUid(noShip) != 0 || ShipText(noShip) != "\u2014") return "links no ship";
            if (AircraftFocusUid(new AswRow { Uid = 15, State = "air" }) != 15 || ShipText(new AswRow { Uid = 15, ShipUid = 7 }) != "\u2014" || ShipText(null) != "\u2014" || AircraftFocusUid(null) != 0) return "links air without ship";
            var ex = new List<AswExcludedRow>
            {
                new AswExcludedRow { Row = new AswRow { Name = "A", Base = "Kirov" }, Reason = "in_group:ASW-1" },
                new AswExcludedRow { Row = new AswRow { Name = "B" }, Reason = "in_group:ASW-2" },
                new AswExcludedRow { Row = new AswRow { Name = "C" }, Reason = "player" }
            };
            if (RefusalSummary(null) != null || RefusalSummary(new List<AswExcludedRow>()) != null || RefusalList(null, 8) != null) return "refusal none";
            // 2026-10-22: a strike-group member is "in group STRIKE-4" for every other group, its own group drops it (the Members table shows it)
            var seen = new List<AswExcludedRow>(ex) { new AswExcludedRow { Row = new AswRow { Uid = 159, Name = "P-3C" }, Reason = "in_group:STRIKE-4" } };
            if (ExcludedSeenBy(seen, "ASW-1").Count != 3 || !ExcludedSeenBy(seen, "ASW-1").Exists(x => x.Row.Uid == 159 && x.Reason == "in_group:STRIKE-4")) return "excluded seen by another group";
            if (ExcludedSeenBy(seen, "STRIKE-4").Exists(x => x.Row.Uid == 159) || ExcludedSeenBy(seen, "STRIKE-4").Count != 3) return "excluded seen by its own group";
            if (ExcludedSeenBy(seen, null).Count != 4 || ExcludedSeenBy(null, "X").Count != 0) return "excluded seen without a group";
            string sum = RefusalSummary(ex), list = RefusalList(ex, 2);
            bool hasTx = UiText.Has(UiText.DefaultSection, "asw.hint.no_free_why"); // selfcheck loads no texts: the wording is checked by the stands
            if (hasTx && sum != "Свободных нет: 2 в группах, 1 в руках игрока") return "refusal summary: " + sum;
            if (hasTx && list != "A (Kirov) \u2014 " + ExcludedText("in_group:ASW-1") + "\nB \u2014 " + ExcludedText("in_group:ASW-2") + "\n…ещё 1") return "refusal list: " + list;
            return null;
        }

        private static string SelfTestRu()
        {
            if (MmSs(125) != "02:05" || MmSs(-1) != "--:--" || MmSs(0) != "00:00") return "mmss";
            var air = new AswRow { Name = "B", State = "air", FuelFrac = 0.5, MinutesOnStation = 90.4, Passive = 12, ActiveBuoys = 8, Vlad = 4, Dipping = true, Mad = true };
            if (StateText(air) != "в воздухе" || BuoysText(air) != "12/8/4" || FuelText(air) != "50% · 90 мин" || SensorsText(air) != "ГАС MAD") return "row text: " + FuelText(air);
            if (StateText(new AswRow { State = "deck_prep", ReadyInS = 600 }) != "готовится 10 мин" || StateText(new AswRow { State = "deck_service", ReadyInS = 61 }) != "обслуживание 2 мин" || StateText(new AswRow { State = "deck_prep", ReadyInS = 1560, TakeoffInS = 1740 }) != "готовится 26 мин · взлёт через 29 мин") return "deck text";
            if (StateText(new AswRow { State = "sar" }) != "занят AutomaticSAR" || StateText(new AswRow { State = "player" }) != "занят игроком") return "taken text";
            var rows = new List<AswRow> { new AswRow { Name = "C", State = "deck_ready", FuelFrac = 1 }, new AswRow { Name = "A", State = "air", FuelFrac = 0.2 }, new AswRow { Name = "B", State = "deck_service", FuelFrac = 1 } };
            SortRows(rows, "name", true);
            if (rows[0].Name != "A" || rows[2].Name != "C") return "sort name";
            SortRows(rows, "state", true);
            if (rows[0].Name != "A" || rows[1].Name != "C" || rows[2].Name != "B") return "sort state";
            SortRows(rows, "fuel", false);
            if (rows[0].FuelFrac != 1 || rows[2].Name != "A") return "sort fuel desc";
            var g = new AswGroupView { Name = "ПЛО-1", Order = "course", TargetName = "Yorktown", RNm = 2.7, RSource = "game", BuoysInWater = 12, NextBarrierInS = 90 };
            g.Contacts.Add(new AswContactView());
            string st = StatusLine(g);
            if (!st.Contains("по курсу") || !st.Contains("R 2.7 мили (оценка игры)") || !st.Contains("буёв в воде 12") || !st.Contains("рубеж через 01:30") || !st.Contains("КОНТАКТ 1")) return "status: " + st;
            if (StatusLine(new AswGroupView { Name = "X", Order = "none" }) != "X: нет приказа") return "status none";
            if (StatusLine(new AswGroupView { Name = "Y", Order = "area", CoveredFrac = 0.5 }).IndexOf("покрыто 50%", StringComparison.Ordinal) < 0) return "status covered";
            if (RSourceText("clamped:ini") != "ограничено, 0,5×ini") return "rsource";
            if (WarningText("continuity_gap (ПЛО-1): 16 min") != "непрерывность не обеспечена (меньше 3 бортов) (ПЛО-1): 16 min") return "warning text: " + WarningText("continuity_gap (ПЛО-1): 16 min");
            var gt = new AswGroupView { Name = "ПЛО-1", Order = "course", TargetName = "Yorktown", GapMin = 12 };
            gt.Tactics.Add("dip"); gt.Tactics.Add("corridor");
            if (TaskLine(gt) != "ПЛО-1 · по курсу Yorktown · ГАС+Коридор · дыра 12 мин") return "task line course: " + TaskLine(gt);
            var ga = new AswGroupView { Name = "ПЛО-2", Order = "area", TargetText = "район около Hull" };
            ga.Tactics.Add("mad");
            if (TaskLine(ga) != "ПЛО-2 · в районе район около Hull · змейка MAD") return "task line area: " + TaskLine(ga);
            if (TaskLine(new AswGroupView { Name = "X", Order = "none" }) != "X · нет приказа") return "task line none";
            if (!TaskLine(null).StartsWith("Нет группы", StringComparison.Ordinal)) return "task line null";
            if (ExcludedText("aew") != "самолёт ДРЛО (AEW)" || ExcludedText("no_asw_load") != "нет ПЛО-загрузки" || ExcludedText("player") != "в руках игрока" || ExcludedText("lost") != "потерян") return "excluded text";
            if (PrimaryTactic(gt) != "dip" || PrimaryTactic(new AswGroupView()) != null) return "primary tactic";
            var sc = SetTacticCommand("ПЛО-1", "mad");
            var sl = sc == null ? null : sc["tactics"] as List<object>;
            if (sl == null || sl.Count != 1 || (string)sl[0] != "mad" || (string)sc["group"] != "ПЛО-1") return "set tactic command";
            if (SetTacticCommand(null, "mad") != null || SetTacticCommand("g", "zig") != null) return "set tactic bad";
            var dc = DisbandCommand("ПЛО-1");
            if (dc == null || (string)dc["op"] != "disband" || DisbandCommand(null) != null) return "disband command";
            if (BackgroundAlpha != 1f) return "window must be opaque";
            double[] r;
            if (!NormalizeRect(100, 80, 40, 20, 8, out r) || r[0] != 40 || r[1] != 20 || r[2] != 100 || r[3] != 80) return "normalize";
            if (NormalizeRect(10, 10, 14, 40, 8, out r)) return "tiny rect accepted";
            // projection: x = 100*lon - 3000 (east), y = -100*lat + 3600 (north up)
            double[] aff = { 0, 100, -3000, -100, 0, 3600 };
            double la, lo;
            if (!PixelToGeo(aff, 200, 100, out la, out lo) || Math.Abs(la - 35) > 1e-9 || Math.Abs(lo - 32) > 1e-9) return "pixel to geo " + la + "," + lo;
            var geo = RectToGeo(aff, new[] { 200.0, 100.0, 300.0, 200.0 });
            if (geo == null || Math.Abs(geo[0] - 35) > 1e-9 || Math.Abs(geo[1] - 32) > 1e-9 || Math.Abs(geo[4] - 34) > 1e-9 || Math.Abs(geo[5] - 33) > 1e-9) return "rect to geo";
            if (geo[0] <= geo[4]) return "top-left must be north of bottom-right";
            if (PixelToGeo(new double[] { 0, 0, 0, 0, 0, 0 }, 1, 1, out la, out lo)) return "singular";
            var geo2 = RectToGeo((x, y) => new[] { 35 - (y - 100) / 100.0, 32 + (x - 200) / 100.0 }, new[] { 200.0, 100.0, 300.0, 200.0 });
            if (geo2 == null || Math.Abs(geo2[0] - 35) > 1e-9 || Math.Abs(geo2[5] - 33) > 1e-9 || Math.Abs(geo2[4] - 34) > 1e-9) return "rect to geo func";
            if (RectToGeo((x, y) => null, new[] { 0.0, 0, 1, 1 }) != null) return "rect to geo func fail";
            string lErr = LinksSelfTest();
            if (lErr != null) return lErr;
            string tErr = TacticsSelfTest();
            if (tErr != null) return tErr;
            string sarErr = SarSelfTest();
            if (sarErr != null) return sarErr;
            return null;
        }
    }

}
