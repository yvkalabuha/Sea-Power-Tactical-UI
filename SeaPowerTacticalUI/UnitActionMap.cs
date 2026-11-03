using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-234 (stream B): pure functions "fields of a controller -> label". The controllers (partial *.ActionLabels.cs) only read their own state and call these;
    /// the phase tables of the plan (section 5) live here. Texts: lang/language_xx/unit_action.ini. No group name in any label (T-234-Q2).
    /// A state that is not known gives a label with a command and no phase (never null); null = the unit is not driven now.
    /// </summary>
    internal static class UnitActionMap
    {
        private static string[] A(params string[] a) { return a; }
        private static string Inv(int n) { return n.ToString(CultureInfo.InvariantCulture); }
        private static string Track(int id) { return id > 0 ? "T" + Inv(id) : "?"; }
        private static bool Is(string s, string v) { return string.Equals(s, v, StringComparison.OrdinalIgnoreCase); }

        private static UnitActionLabel Make(int uid, int prio, string source, string cmdKey, string[] cmdArgs, string phaseKey = null, string[] phaseArgs = null)
        {
            return new UnitActionLabel { Uid = uid, Prio = prio, Source = source, CmdKey = cmdKey, CmdArgs = cmdArgs, PhaseKey = phaseKey, PhaseArgs = phaseArgs };
        }

        /// <summary>A leave reason code ("fuel", "alert_clear", ...): the text of the CAP / ASW window when it exists, else the code itself.</summary>
        private static string Reason(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            string k = "cap.leave." + code;
            return UiText.Has(UiText.DefaultSection, k) ? UiText.Get(k) : code;
        }

        public static UnitActionLabel Evasion(int uid, int trackId, bool holding, bool slowed, bool atwTurn = false)
        {
            string src = "evasion";
            if (atwTurn) return Make(uid, UaPrio.Evasion, src, "ua.cmd.evasion", null, "ua.ph.ev.atw_turn");   // T-230: turning onto the salvo of the anti-torpedo mount
            if (slowed) return Make(uid, UaPrio.Evasion, src, "ua.cmd.evasion", null, "ua.ph.ev.slow");   // T-236: the slow phase is seen even while the track is frozen
            if (holding) return Make(uid, UaPrio.Evasion, src, "ua.cmd.evasion", null, "ua.ph.ev.hold");
            if (trackId > 0) return Make(uid, UaPrio.Evasion, src, "ua.cmd.evasion", null, "ua.ph.ev.run");   // T-236: no track number in the text (the primary track changes every 2-5 s -> blinking)
            return Make(uid, UaPrio.Evasion, src, "ua.cmd.evasion", null, "ua.ph.ev.run_notrack");
        }

        public static UnitActionLabel SubEvasion(int uid, string mode)
        {
            string ph = Is(mode, "own") ? "ua.ph.sub.own" : Is(mode, "native") ? "ua.ph.sub.native" : null;
            return Make(uid, UaPrio.Evasion, "sub_evasion", "ua.cmd.sub_evasion", null, ph);
        }

        public static UnitActionLabel AirEvasion(int uid, string mode, bool chaffDone)
        {
            return Make(uid, UaPrio.Evasion, "air_evasion", "ua.cmd.air_evasion", null, chaffDone ? "ua.ph.air.chaff" : "ua.ph.air.maneuver");
        }

        public static UnitActionLabel Retreat(int uid, string steerMode, bool orbiting, string coverName, double threatNm)
        {
            const string src = "air_guard", cmd = "ua.cmd.retreat";
            bool cover = !string.IsNullOrEmpty(coverName);
            if (orbiting && cover) return Make(uid, UaPrio.Retreat, src, cmd, null, "ua.ph.retreat.orbit", A(coverName));
            if (Is(steerMode, "cover") && cover) return Make(uid, UaPrio.Retreat, src, cmd, null, "ua.ph.retreat.cover", A(coverName));
            if (Is(steerMode, "away") && !double.IsNaN(threatNm) && !double.IsInfinity(threatNm))
                return Make(uid, UaPrio.Retreat, src, cmd, null, "ua.ph.retreat.away", A(Inv((int)Math.Round(threatNm))));
            return Make(uid, UaPrio.Retreat, src, cmd, null);
        }

        public static UnitActionLabel SmartAttack(int uid, string state, int trackId)
        {
            if (Is(state, "Done")) return null;
            string ph = null;
            if (Is(state, "Preflight")) ph = "ua.ph.sa.preflight";
            else if (Is(state, "Positioning")) ph = "ua.ph.sa.positioning";
            else if (Is(state, "Launch")) ph = "ua.ph.sa.launch";
            else if (Is(state, "Leg")) ph = "ua.ph.sa.leg";
            else if (Is(state, "Approach")) ph = "ua.ph.sa.approach";
            else if (Is(state, "Final") || Is(state, "Terminal")) ph = "ua.ph.sa.final";
            return Make(uid, UaPrio.SmartAttack, "smart_attack", "ua.cmd.smart_attack", A(Track(trackId)), ph);
        }

        public static UnitActionLabel Intercept(int uid, string state, int trackId, double etaS)
        {
            string ph = null; string[] pa = null;
            if (Is(state, "routing")) ph = "ua.ph.ic.routing";
            else if (Is(state, "active"))
            {
                if (double.IsNaN(etaS) || double.IsInfinity(etaS) || etaS < 0) ph = "ua.ph.ic.active_noeta";
                else { ph = "ua.ph.ic.active"; pa = A(UiText.Duration(etaS)); }
            }
            else if (Is(state, "held")) ph = "ua.ph.ic.held";
            else if (Is(state, "suspended")) ph = "ua.ph.ic.suspended";
            return Make(uid, UaPrio.Intercept, "intercept", "ua.cmd.intercept", A(Track(trackId)), ph, pa);
        }

        public static UnitActionLabel AswAttack(int uid, string state, int contactId)
        {
            if (Is(state, "done") || Is(state, "idle")) return null;
            string ph = null;
            if (Is(state, "approach")) ph = "ua.ph.att.approach";
            else if (Is(state, "engaged")) ph = "ua.ph.att.engaged";
            else if (Is(state, "in_water")) ph = "ua.ph.att.in_water";
            else if (Is(state, "verify")) ph = "ua.ph.att.verify";
            else if (Is(state, "reacquire")) ph = "ua.ph.att.reacquire";
            else if (Is(state, "assess")) ph = "ua.ph.att.assess";
            else if (Is(state, "hold") || Is(state, "wait")) ph = "ua.ph.att.hold";
            return Make(uid, UaPrio.AswAttack, "asw_attack", "ua.cmd.asw_attack", A(Track(contactId)), ph);
        }

        public static UnitActionLabel AswStrike(int uid, string task, int trackId)
        {
            const string src = "asw_strike", cmd = "ua.cmd.asw_strike";
            if (Is(task, "target")) return Make(uid, UaPrio.Strike, src, cmd, null, "ua.ph.strike.target", A(Track(trackId)));
            if (Is(task, "idle")) return Make(uid, UaPrio.Strike, src, cmd, null, "ua.ph.strike.idle");
            if (Is(task, "rtb")) return Make(uid, UaPrio.Strike, src, cmd, null, "ua.ph.strike.rtb");
            return Make(uid, UaPrio.Strike, src, cmd, null);
        }

        /// <summary>order course|area, tactic = the tactic id (asw.tactic.*; null or auto = no tactic name), barrierRow = the ZERO-BASED row index of the barrier (shown +1), -1 = none.</summary>
        /// <summary>The name of a tactic / pattern as the ASW window shows it (asw.tactic.*, asw.lbl.*, asw.area.*), else the id.</summary>
        private static string TacticName(string order, string tactic)
        {
            string[] keys = Is(order, "area") ? new[] { "asw.area." + tactic } : new[] { "asw.tactic." + tactic, "asw.lbl." + tactic };
            foreach (string k in keys) if (UiText.Has(UiText.DefaultSection, k)) return UiText.Get(k);
            return tactic;
        }

        public static UnitActionLabel AswMember(int uid, string order, string tactic, bool dropping, bool dipping, string leaving, int barrierRow, bool standby, int barrierRows = 0)
        {
            string cmd; string[] ca = null;
            if (string.IsNullOrEmpty(tactic) || Is(tactic, "auto")) cmd = "ua.cmd.asw_plain";
            else
            {
                cmd = Is(order, "area") ? "ua.cmd.asw_area" : "ua.cmd.asw_course";
                ca = A(TacticName(order, tactic));
            }
            string ph; string[] pa = null;
            if (!string.IsNullOrEmpty(leaving)) { ph = "ua.ph.asw.leaving"; pa = A(Reason(leaving)); }
            else if (barrierRow >= 0 && !dipping) { ph = "ua.ph.asw.barrier_row"; pa = A(Inv(barrierRow + 1), Inv(Math.Max(barrierRows, barrierRow + 1))); }   // barrierRow = 0-based place among the rows ahead of the ship
            else if (dropping) ph = "ua.ph.asw.drop";
            else if (dipping) ph = "ua.ph.asw.dip";
            else if (standby) ph = "ua.ph.asw.reserve";
            else ph = "ua.ph.asw.to_station";
            return Make(uid, UaPrio.Asw, "asw", cmd, ca, ph, pa);
        }

        public static UnitActionLabel SarHelo(int uid, int raftId, bool hovering, int onboard, int capacity, bool returning)
        {
            const string src = "sar", cmd = "ua.cmd.sar";
            if (returning) return Make(uid, UaPrio.Sar, src, cmd, null, "ua.ph.sar.return", A(Inv(onboard)));
            if (hovering) return Make(uid, UaPrio.Sar, src, cmd, null, "ua.ph.sar.hoist", A(Inv(onboard), Inv(capacity)));
            if (raftId > 0) return Make(uid, UaPrio.Sar, src, cmd, null, "ua.ph.sar.raft", A(Inv(raftId)));
            return Make(uid, UaPrio.Sar, src, cmd, null);
        }

        public static UnitActionLabel SarShip(int uid, string state, int raftId)
        {
            const string src = "sar", cmd = "ua.cmd.sar";
            if (Is(state, "plan")) return Make(uid, UaPrio.Sar, src, cmd, null, "ua.ph.sar.plan");
            if (Is(state, "route") || Is(state, "transit")) return Make(uid, UaPrio.Sar, src, cmd, null, "ua.ph.sar.transit");
            if (Is(state, "final")) return raftId > 0 ? Make(uid, UaPrio.Sar, src, cmd, null, "ua.ph.sar.final", A(Inv(raftId))) : Make(uid, UaPrio.Sar, src, cmd, null, "ua.ph.sar.transit");
            return Make(uid, UaPrio.Sar, src, cmd, null);
        }

        /// <summary>Aircraft that are not airborne (deck rows) give null. kind patrol|alert; lent = a patrol flown by an alert-group aircraft.</summary>
        public static UnitActionLabel Cap(int uid, string kind, bool airborne, bool onStation, string alertState, string raid, string leaving, bool lent, string group = null)
        {
            if (!airborne) return null;
            bool named = !string.IsNullOrEmpty(group);   // T-335: the group name as in the CAP window (T-326 rename), like ASW "<tactic>"
            string cmd = Is(kind, "alert") ? (named ? "ua.cmd.cap_alert_n" : "ua.cmd.cap_alert") : lent ? (named ? "ua.cmd.cap_lent_n" : "ua.cmd.cap_lent") : (named ? "ua.cmd.cap_patrol_n" : "ua.cmd.cap_patrol");
            string[] ca = named ? A(group) : null;
            if (!string.IsNullOrEmpty(leaving)) return Make(uid, UaPrio.Cap, "cap", cmd, ca, "ua.ph.cap.leaving", A(Reason(leaving)));
            if (Is(alertState, "scramble")) return Make(uid, UaPrio.Cap, "cap", cmd, ca, "ua.ph.cap.scramble", A(raid ?? ""));
            if (Is(alertState, "return")) return Make(uid, UaPrio.Cap, "cap", cmd, ca, "ua.ph.cap.return");
            return Make(uid, UaPrio.Cap, "cap", cmd, ca, onStation ? "ua.ph.cap.station" : "ua.ph.cap.to_zone");
        }

        // ---------- self-test ----------

        private static string Want(UnitActionLabel l, string cmd, string phase, params string[] phaseArgs)
        {
            if (l == null) return "label is null";
            if (l.CmdKey != cmd) return "CmdKey " + l.CmdKey + " want " + cmd;
            if (l.PhaseKey != phase) return "PhaseKey " + (l.PhaseKey ?? "-") + " want " + (phase ?? "-");
            string got = string.Join("|", l.PhaseArgs ?? new string[0]), exp = string.Join("|", phaseArgs ?? new string[0]);
            if (got != exp) return "PhaseArgs " + got + " want " + exp;
            return null;
        }

        private static string Chk(string what, string err) { return err == null ? null : what + ": " + err; }

        public static string SelfTest()
        {
            using (UiText.Push("en"))
            {
                string e;
                // priorities and sources
                var ev = Evasion(5, 12, false, false);
                if (ev.Prio != UaPrio.Evasion || ev.Source != "evasion" || ev.Uid != 5) return "evasion prio/source/uid";
                if ((e = Chk("evasion run", Want(ev, "ua.cmd.evasion", "ua.ph.ev.run"))) != null) return e;
                if ((e = Chk("evasion no track", Want(Evasion(5, 0, false, false), "ua.cmd.evasion", "ua.ph.ev.run_notrack"))) != null) return e;
                if ((e = Chk("evasion hold", Want(Evasion(5, 12, true, false), "ua.cmd.evasion", "ua.ph.ev.hold"))) != null) return e;
                if ((e = Chk("evasion atw turn", Want(Evasion(5, 12, true, true, true), "ua.cmd.evasion", "ua.ph.ev.atw_turn"))) != null) return e;
                if ((e = Chk("evasion slow beats hold", Want(Evasion(5, 12, true, true), "ua.cmd.evasion", "ua.ph.ev.slow"))) != null) return e;
                if (Evasion(5, 12, false, false).PhaseArgs != null || Evasion(5, 13, false, false).PhaseKey != Evasion(5, 12, false, false).PhaseKey) return "evasion run text depends on the track";
                if ((e = Chk("evasion slow", Want(Evasion(5, 12, false, true), "ua.cmd.evasion", "ua.ph.ev.slow"))) != null) return e;
                if ((e = Chk("sub own", Want(SubEvasion(1, "own"), "ua.cmd.sub_evasion", "ua.ph.sub.own"))) != null) return e;
                if ((e = Chk("sub native", Want(SubEvasion(1, "native"), "ua.cmd.sub_evasion", "ua.ph.sub.native"))) != null) return e;
                if ((e = Chk("sub unknown", Want(SubEvasion(1, "zzz"), "ua.cmd.sub_evasion", null))) != null) return e;
                if ((e = Chk("air maneuver", Want(AirEvasion(1, "own", false), "ua.cmd.air_evasion", "ua.ph.air.maneuver"))) != null) return e;
                if ((e = Chk("air chaff", Want(AirEvasion(1, "own", true), "ua.cmd.air_evasion", "ua.ph.air.chaff"))) != null) return e;
                var rt = Retreat(2, "cover", false, "Perry", 9.0);
                if (rt.Prio != UaPrio.Retreat || rt.Source != "air_guard") return "retreat prio/source";
                if ((e = Chk("retreat cover", Want(rt, "ua.cmd.retreat", "ua.ph.retreat.cover", "Perry"))) != null) return e;
                if ((e = Chk("retreat away", Want(Retreat(2, "away", false, null, 9.4), "ua.cmd.retreat", "ua.ph.retreat.away", "9"))) != null) return e;
                if ((e = Chk("retreat orbit", Want(Retreat(2, "cover", true, "Perry", 9), "ua.cmd.retreat", "ua.ph.retreat.orbit", "Perry"))) != null) return e;
                if ((e = Chk("retreat none", Want(Retreat(2, null, false, null, double.NaN), "ua.cmd.retreat", null))) != null) return e;
                // smart attack
                string[] sa = { "Preflight", "Positioning", "Launch", "Leg", "Approach", "Final", "Terminal" };
                string[] sk = { "preflight", "positioning", "launch", "leg", "approach", "final", "final" };
                for (int i = 0; i < sa.Length; i++)
                {
                    var l = SmartAttack(3, sa[i], 7);
                    if (l.Prio != UaPrio.SmartAttack || l.Source != "smart_attack") return "smart attack prio/source";
                    if (l.CmdArgs == null || l.CmdArgs[0] != "T7") return "smart attack cmd arg";
                    if ((e = Chk("smart attack " + sa[i], Want(l, "ua.cmd.smart_attack", "ua.ph.sa." + sk[i]))) != null) return e;
                }
                if (SmartAttack(3, "Done", 7) != null) return "smart attack Done must give null";
                if ((e = Chk("smart attack unknown", Want(SmartAttack(3, "Zzz", 7), "ua.cmd.smart_attack", null))) != null) return e;
                // intercept
                var ic = Intercept(4, "active", 9, 125);
                if (ic.Prio != UaPrio.Intercept || ic.Source != "intercept" || ic.CmdArgs[0] != "T9") return "intercept prio/source/arg";
                if ((e = Chk("intercept active", Want(ic, "ua.cmd.intercept", "ua.ph.ic.active", UiText.Duration(125)))) != null) return e;
                if ((e = Chk("intercept no eta", Want(Intercept(4, "active", 9, double.NaN), "ua.cmd.intercept", "ua.ph.ic.active_noeta"))) != null) return e;
                if ((e = Chk("intercept routing", Want(Intercept(4, "routing", 9, double.NaN), "ua.cmd.intercept", "ua.ph.ic.routing"))) != null) return e;
                if ((e = Chk("intercept held", Want(Intercept(4, "held", 9, 1), "ua.cmd.intercept", "ua.ph.ic.held"))) != null) return e;
                if ((e = Chk("intercept suspended", Want(Intercept(4, "suspended", 9, 1), "ua.cmd.intercept", "ua.ph.ic.suspended"))) != null) return e;
                // asw attack
                string[] at = { "approach", "engaged", "in_water", "verify", "reacquire", "assess", "hold" };
                foreach (var s in at)
                    if ((e = Chk("asw attack " + s, Want(AswAttack(6, s, 21), "ua.cmd.asw_attack", "ua.ph.att." + s))) != null) return e;
                if ((e = Chk("asw attack wait", Want(AswAttack(6, "wait", 21), "ua.cmd.asw_attack", "ua.ph.att.hold"))) != null) return e;
                if (AswAttack(6, "done", 21) != null || AswAttack(6, "idle", 21) != null) return "asw attack done/idle must give null";
                if (AswAttack(6, "approach", 21).Prio != UaPrio.AswAttack) return "asw attack prio";
                if ((e = Chk("asw attack unknown", Want(AswAttack(6, "zzz", 21), "ua.cmd.asw_attack", null))) != null) return e;
                // asw strike
                if (AswStrike(8, "idle", 0).Prio != UaPrio.Strike) return "strike prio";
                if ((e = Chk("strike target", Want(AswStrike(8, "target", 33), "ua.cmd.asw_strike", "ua.ph.strike.target", "T33"))) != null) return e;
                if ((e = Chk("strike idle", Want(AswStrike(8, "idle", 0), "ua.cmd.asw_strike", "ua.ph.strike.idle"))) != null) return e;
                if ((e = Chk("strike rtb", Want(AswStrike(8, "rtb", 0), "ua.cmd.asw_strike", "ua.ph.strike.rtb"))) != null) return e;
                // asw member: the order of the phases and the tactic in the command, no group name
                var bar = AswMember(10, "course", "barrier", false, false, null, 1, false, 3);
                if (bar.Prio != UaPrio.Asw || bar.Source != "asw") return "asw prio/source";
                if (bar.CmdKey != "ua.cmd.asw_course" || bar.CmdArgs.Length != 1 || bar.CmdArgs[0] != "Barrier") return "asw course cmd = " + bar.CmdKey + " " + string.Join("|", bar.CmdArgs ?? new string[0]);
                if ((e = Chk("asw barrier row", Want(bar, "ua.cmd.asw_course", "ua.ph.asw.barrier_row", "2", "3"))) != null) return e;
                if (UnitActionText.Format(bar) != "ASW \"Barrier\": to line 2/3") return "asw text = " + UnitActionText.Format(bar);
                var area = AswMember(10, "area", "grid", false, false, null, -1, false);
                if (area.CmdKey != "ua.cmd.asw_area") return "asw area cmd";
                if ((e = Chk("asw to station", Want(area, "ua.cmd.asw_area", "ua.ph.asw.to_station"))) != null) return e;
                if ((e = Chk("asw plain", Want(AswMember(10, "course", "auto", false, false, null, -1, false), "ua.cmd.asw_plain", "ua.ph.asw.to_station"))) != null) return e;
                var mad = AswMember(10, "area", "mad", false, false, null, -1, false);
                if (UnitActionText.Format(mad).IndexOf("\"MAD\"", StringComparison.Ordinal) < 0) return "asw area name = " + UnitActionText.Format(mad);
                if (AswMember(10, "course", null, false, false, null, -1, false).CmdArgs != null) return "plain asw command has no args";
                if ((e = Chk("asw drop", Want(AswMember(10, "course", "barrier", true, true, null, -1, true), "ua.cmd.asw_course", "ua.ph.asw.drop"))) != null) return e;
                if ((e = Chk("asw row beats drop", Want(AswMember(10, "course", "barrier", true, false, null, 0, false, 2), "ua.cmd.asw_course", "ua.ph.asw.barrier_row", "1", "2"))) != null) return e;
                if ((e = Chk("asw dip", Want(AswMember(10, "course", "barrier", false, true, null, 0, true), "ua.cmd.asw_course", "ua.ph.asw.dip"))) != null) return e;
                if ((e = Chk("asw barrier before reserve", Want(AswMember(10, "course", "barrier", false, false, null, 0, true), "ua.cmd.asw_course", "ua.ph.asw.barrier_row", "1", "1"))) != null) return e;
                if ((e = Chk("asw reserve", Want(AswMember(10, "course", "barrier", false, false, null, -1, true), "ua.cmd.asw_course", "ua.ph.asw.reserve"))) != null) return e;
                if ((e = Chk("asw leaving", Want(AswMember(10, "course", "barrier", true, true, "fuel", 3, true), "ua.cmd.asw_course", "ua.ph.asw.leaving", "fuel"))) != null) return e;
                // sar
                var sh = SarHelo(11, 40, false, 0, 3, false);
                if (sh.Prio != UaPrio.Sar || sh.Source != "sar") return "sar prio/source";
                if ((e = Chk("sar raft", Want(sh, "ua.cmd.sar", "ua.ph.sar.raft", "40"))) != null) return e;
                if ((e = Chk("sar hoist", Want(SarHelo(11, 40, true, 2, 3, false), "ua.cmd.sar", "ua.ph.sar.hoist", "2", "3"))) != null) return e;
                if ((e = Chk("sar return", Want(SarHelo(11, 40, true, 2, 3, true), "ua.cmd.sar", "ua.ph.sar.return", "2"))) != null) return e;
                if ((e = Chk("sar helo no raft", Want(SarHelo(11, 0, false, 0, 3, false), "ua.cmd.sar", null))) != null) return e;
                if ((e = Chk("sar ship plan", Want(SarShip(12, "plan", 40), "ua.cmd.sar", "ua.ph.sar.plan"))) != null) return e;
                if ((e = Chk("sar ship route", Want(SarShip(12, "route", 40), "ua.cmd.sar", "ua.ph.sar.transit"))) != null) return e;
                if ((e = Chk("sar ship transit", Want(SarShip(12, "transit", 40), "ua.cmd.sar", "ua.ph.sar.transit"))) != null) return e;
                if ((e = Chk("sar ship final", Want(SarShip(12, "final", 40), "ua.cmd.sar", "ua.ph.sar.final", "40"))) != null) return e;
                if ((e = Chk("sar ship unknown", Want(SarShip(12, "zzz", 40), "ua.cmd.sar", null))) != null) return e;
                // cap
                if (Cap(13, "patrol", false, false, null, null, null, false) != null) return "cap on the deck must give null";
                var cp = Cap(13, "patrol", true, true, null, null, null, false);
                if (cp.Prio != UaPrio.Cap || cp.Source != "cap") return "cap prio/source";
                if ((e = Chk("cap station", Want(cp, "ua.cmd.cap_patrol", "ua.ph.cap.station"))) != null) return e;
                if ((e = Chk("cap to zone", Want(Cap(13, "patrol", true, false, null, null, null, false), "ua.cmd.cap_patrol", "ua.ph.cap.to_zone"))) != null) return e;
                if ((e = Chk("cap lent", Want(Cap(13, "patrol", true, true, null, null, null, true), "ua.cmd.cap_lent", "ua.ph.cap.station"))) != null) return e;
                if ((e = Chk("cap scramble", Want(Cap(13, "alert", true, false, "scramble", "r7028", null, false), "ua.cmd.cap_alert", "ua.ph.cap.scramble", "r7028"))) != null) return e;
                if ((e = Chk("cap return", Want(Cap(13, "alert", true, false, "return", null, null, false), "ua.cmd.cap_alert", "ua.ph.cap.return"))) != null) return e;
                if ((e = Chk("cap leaving", Want(Cap(13, "patrol", true, true, "scramble", "r1", "lend_end", false), "ua.cmd.cap_patrol", "ua.ph.cap.leaving", "patrol ended"))) != null) return e;
                if ((e = Chk("cap named", Want(Cap(13, "patrol", true, true, null, null, null, false, "Hawk 1"), "ua.cmd.cap_patrol_n", "ua.ph.cap.station"))) != null) return e;
                if (UnitActionText.Format(Cap(13, "patrol", true, true, null, null, null, false, "Hawk 1")) != "CAP \"Hawk 1\": on station") return "cap named text = " + UnitActionText.Format(Cap(13, "patrol", true, true, null, null, null, false, "Hawk 1"));
                if (UnitActionText.Format(Cap(13, "alert", true, false, "scramble", "r7028", null, false)) != "Alert: intercepting raid r7028") return "cap text = " + UnitActionText.Format(Cap(13, "alert", true, false, "scramble", "r7028", null, false));
            }
            using (UiText.Push("ru"))
            {
                var bar = AswMember(10, "course", "barrier", false, false, null, 1, false, 3);
                if (UnitActionText.Format(bar) != "ПЛО «Барьер»: к рубежу 2/3") return "ru asw text = " + UnitActionText.Format(bar);
            }
            return null;
        }
    }
}
