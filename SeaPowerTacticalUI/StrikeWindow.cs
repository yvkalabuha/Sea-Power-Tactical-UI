using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-341 phase B: the texts of the strike window (pure formatters over UiText keys strike.*, lang/*/strike_window.ini). No Unity, no hardcoded words.
    internal static class StrikeWindow
    {
        private static string T(string key, params object[] args) { return UiText.Fmt(key, args); }
        private static string F1(double d) { return d.ToString("0.#", CultureInfo.InvariantCulture); }

        public static string Mmss(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s)) return "-";
            bool neg = s < 0;
            int t = (int)Math.Round(Math.Abs(s));
            return (neg ? "-" : "") + (t / 60).ToString(CultureInfo.InvariantCulture) + ":" + (t % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private static string Table(string prefix, string code, string fallbackKey, params object[] args)
        {
            string key = prefix + code;
            return UiText.Has(UiText.DefaultSection, key) ? T(key, args) : T(fallbackKey, code);
        }

        /// <summary>The reason of a grey aircraft row: helicopter | no_asm | ... | in_group:g | owned:window:g.</summary>
        public static string WhyText(string code)
        {
            if (code == null) return "";
            if (code.StartsWith("owned:", StringComparison.Ordinal)) return T("strike.why.owned", code.Substring(6));
            if (code.StartsWith("in_group:", StringComparison.Ordinal)) return T("strike.why.in_group", code.Substring(9));
            return Table("strike.why.", code, "strike.why.generic");
        }

        public static string ExclText(string code) { return code == null ? "" : Table("strike.excl.", code, "strike.why.generic"); }

        public static string SourceText(string src) { return Table("strike.src.", src ?? "none", "strike.why.generic"); }

        /// <summary>"8.9/27.7 (cache)": the AA capacity of a ship against a reference raid and where it comes from.</summary>
        public static string AdCell(StrikeShipRow r)
        {
            if (r == null || r.Ksub <= 0) return "-";
            return F1(Math.Round(r.Ksub, 1)) + "/" + F1(Math.Round(r.Ssub, 1)) + " (" + SourceText(r.AdSource) + ")";
        }

        public static string TonsText(double t) { return t <= 0 ? "-" : Math.Round(t).ToString("0", CultureInfo.InvariantCulture); }

        /// <summary>The reason of the place in the priority: "AA 8.9, 8300 t", "close AA, by tonnage", "last: Transport".</summary>
        public static string PrioText(StrikeShipRow r)
        {
            if (r == null) return "";
            string tons = TonsText(r.Tons);
            switch (r.Why)
            {
                case "low_role": return T("strike.prio.low_role", r.Role ?? "");
                case "tons": return T("strike.prio.tons", F1(Math.Round(r.Ksub, 1)), tons);
                default: return r.Ksub > 0 ? T("strike.prio.ad", F1(Math.Round(r.Ksub, 1)), tons) : T("strike.prio.no_ad", "", tons);
            }
        }

        public static string KindText(string kind) { return Table("strike.kind.", kind ?? "", "strike.why.generic"); }

        public static string CardTitle(StrikeCardView c)
        {
            return c == null ? "" : T("strike.card.title", KindText(c.Kind), c.Missiles, c.Targets);
        }

        /// <summary>"91% (estimate)": every odd is an estimate until the live calibration.</summary>
        public static string ProbCell(double p, bool estimate)
        {
            string v = Math.Round(p * 100).ToString("0", CultureInfo.InvariantCulture);
            return estimate ? T("strike.card.prob", v) : T("strike.card.prob_exact", v);
        }

        /// <summary>A warning code of a variant: "short n=3 target=7002" -> "short of 3 missiles"; the rest by its head word.</summary>
        public static string WarnText(string code) { return WarnText(code, null); }

        /// <summary>T-349: the same with a name resolver: "uid:136" -> aircraft name, "ammo:wp_as-2" -> missile name (null = fallback "#136" / the raw key).</summary>
        public static string WarnText(string code, Func<string, string> name)
        {
            if (string.IsNullOrEmpty(code)) return "";
            int sp = code.IndexOf(' '), co = code.IndexOf(':');
            int cut = sp < 0 ? co : (co < 0 ? sp : Math.Min(sp, co));
            string head = cut < 0 ? code : code.Substring(0, cut), rest = cut < 0 ? "" : code.Substring(cut + 1);
            Func<string, string> un = u => { string n = name == null ? null : name("uid:" + u); return string.IsNullOrEmpty(n) ? u : n; };
            Func<string, string> am = k => { string n = name == null ? null : name("ammo:" + k); return string.IsNullOrEmpty(n) ? k : n; };
            switch (head)
            {
                case "short": return T("strike.warn.short", Arg(rest, "n"));
                case "launch_land": return T("strike.warn.launch_land", rest);
                case "deck_pending": return T("strike.warn.deck_pending", rest);
                case "needs_illum": return T("strike.warn.needs_illum", un(Arg(rest, "uid")));
                case "sam_risk": return T("strike.warn.sam_risk", un(Arg(rest, "uid")));
                case "fuel": return T("strike.warn.fuel", un(Arg(rest, "uid")));
                case "spread": return T("strike.warn.spread", Arg(rest, "s"));
                case "not_airborne": return T("strike.warn.not_airborne", un(Arg(rest, "uid")));
                case "clearing_no_depletion": return T("strike.warn.clearing_no_depletion");
                case "later_wave": return T("strike.warn.later_wave");
                case "clear_pause": return T("strike.warn.clear_pause", rest);
                case "class_changed": return T("strike.warn.class_changed");
                case "gate": return T("strike.warn.gate", rest);
                case "mixed_order": return T("strike.warn.mixed_order", rest);
                case "mixed_shift": return T("strike.warn.mixed_shift", un(Arg(rest, "uid")), Arg(rest, "s"));
                case "mix_late": return T("strike.warn.mix_late", un(Arg(rest, "uid")), am(Arg(rest, "ammo")));
                case "kill_unknown": return T("strike.warn.kill_unknown", Arg(rest, "target"));
                case "kill_short": return T("strike.warn.kill_short", Arg(rest, "n"), Arg(rest, "target"));
                case "deck_wait": { double s; return T("strike.warn.deck_wait", double.TryParse(Arg(rest, "s"), NumberStyles.Float, CultureInfo.InvariantCulture, out s) ? Mmss(s) : Mmss(ParseS(rest))); }
                case "ew_late": { var p = rest.Split(':'); return T("strike.warn.ew_late", p.Length > 0 ? p[0] : "", p.Length > 1 ? p[1] : ""); }
                case "ew_uncovered": return T("strike.warn.ew_uncovered", rest);
                case "ew_band": { var p = rest.Split(':'); return T("strike.warn.ew_band", p.Length > 0 ? p[0] : "", p.Length > 1 ? p[1] : ""); }
                case "ew_no_station": { var p = rest.Split(':'); return T("strike.warn.ew_no_station", p.Length > 0 ? p[0] : "", EwWhyText(p.Length > 1 ? p[1] : "")); }
                case "ew_not_airborne": return T("strike.warn.ew_not_airborne", rest);
                case "ew_fighter_reserve": return T("strike.warn.ew_fighter_reserve", rest);
                default: return T("strike.warn.generic", code);
            }
        }

        private static double ParseS(string rest) { double s; return double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out s) ? s : 0; }

        public static string DeckStateText(string state) { return Table("strike.deck.state.", state ?? "sent", "strike.why.generic"); }
        public static string DeckErrText(string err) { return Table("strike.deck.err.", err ?? "other", "strike.deck.err.other"); }

        /// <summary>A note of the deck block (the controller writes the codes): deck_ordered | deck_err | deck_blocked | deck_ready.</summary>
        public static string DeckNoteText(string note)
        {
            if (string.IsNullOrEmpty(note)) return "";
            var p = note.Split(':');
            switch (p[0])
            {
                case "deck_ordered":
                    if (p.Length < 8) return "";
                    return T("strike.msg.deck_ordered", p[1], p[2], p[3], p[4], p[5], p[6], Math.Round(ParseS(p[7]) / 60.0).ToString("0", CultureInfo.InvariantCulture));
                case "deck_err": return p.Length < 5 ? "" : T("strike.msg.deck_err", DeckErrText(p[1]), p[2], p[3], p[4]);
                case "deck_blocked": return T("strike.msg.deck_blocked");
                case "deck_ready": return T("strike.msg.deck_ready");
                default: return "";
            }
        }

        /// <summary>The value of key=value inside "n=3 target=7"; a bare rest ("1") comes back as it is.</summary>
        private static string Arg(string rest, string key)
        {
            if (rest == null) return "";
            foreach (string part in rest.Split(' '))
                if (part.StartsWith(key + "=", StringComparison.Ordinal)) return part.Substring(key.Length + 1);
            return rest.IndexOf('=') >= 0 ? "" : rest;
        }

        /// <summary>A row is yellow when it is later than this against the plan (the arrival spread that the window allows by default).</summary>
        public const double LateToleranceS = 10;

        public static string EwStateText(string state) { return Table("strike.ew.state.", state ?? "plan", "strike.why.generic"); }
        public static string EwWhyText(string why)
        {
            if (string.IsNullOrEmpty(why)) return "";
            int c = why.IndexOf(':');
            if (c >= 0) why = why.Substring(c + 1);
            return Table("strike.ewwhy.", why, "strike.why.generic");
        }

        // ---- T-342 phase D: the jam of the fighters
        public static string EwModeText(string mode) { return Table("strike.ew.mode.", mode ?? "transit", "strike.why.generic"); }

        /// <summary>A note of the jam of the fighters: ewf_on:&lt;uid&gt;:&lt;track&gt; | ewf_off:&lt;uid&gt;:&lt;why&gt; | ewf_no_cover:&lt;uid&gt;:&lt;class&gt; | ewf_uncovered:&lt;n&gt; | ewf_ships_lost:&lt;uid&gt;:&lt;n&gt;.</summary>
        public static string EwNoteText(string note)
        {
            if (string.IsNullOrEmpty(note)) return "";
            var p = note.Split(':');
            switch (p[0])
            {
                case "ewf_on": return p.Length < 3 ? "" : T("strike.msg.ewf_on", "#" + p[1], "#" + p[2]);
                case "ewf_off": return p.Length < 3 ? "" : T("strike.msg.ewf_off", "#" + p[1], Table("strike.ewf.off.", p[2], "strike.why.generic"));
                case "ewf_no_cover": return p.Length < 3 ? "" : T("strike.msg.ewf_no_cover", p[2]);
                case "ewf_uncovered": return p.Length < 2 ? "" : T("strike.msg.ewf_uncovered", p[1]);
                case "ewf_ships_lost": return p.Length < 3 ? "" : T("strike.msg.ewf_ships_lost", "#" + p[1], p[2]);
                default: return "";
            }
        }

        // ---- T-342 phase C: the auto mode and the clearing
        public static string AutoStateText(string state) { return Table("strike.auto.state.", state ?? "off", "strike.why.generic"); }
        public static string ClearEndText(string stage) { return Table("strike.clear.end.", stage ?? "lost", "strike.why.generic"); }
        public static string AutoWhyText(string why) { return why == null ? "" : Table("strike.auto.why.", why, "strike.why.generic"); }

        /// <summary>The reason of a stop: no_targets | ... | launch_err:gate | blocked:no_rows.</summary>
        public static string AutoStopText(string why)
        {
            if (why == null) return "";
            int c = why.IndexOf(':');
            string head = c < 0 ? why : why.Substring(0, c), detail = c < 0 ? "" : why.Substring(c + 1);
            return Table("strike.auto.stop.", head, "strike.why.generic", detail);
        }

        /// <summary>A note of the auto mode / of the clearing: auto_on | auto_launch:&lt;wave&gt;:&lt;kind&gt; | auto_red:&lt;p&gt;:&lt;th&gt; | auto_stop:&lt;why&gt; | auto_off_loaded | clear_*.</summary>
        public static string AutoNoteText(string note)
        {
            if (string.IsNullOrEmpty(note)) return "";
            var p = note.Split(':');
            switch (p[0])
            {
                case "auto_on": return T("strike.msg.auto_on");
                case "auto_launch": return p.Length < 3 ? "" : T("strike.msg.auto_launch", p[1], KindText(p[2]));
                case "auto_red": return p.Length < 3 ? "" : T("strike.msg.auto_red", PctOf(p[1]), PctOf(p[2]));
                case "auto_stop": return p.Length < 2 ? "" : T("strike.msg.auto_stop", AutoStopText(note.Substring(10)));
                case "auto_off_loaded": return T("strike.msg.auto_off_loaded");
                case "clear_wait": return T("strike.msg.clear_wait", p.Length > 1 ? p[1] : "");
                case "clear_release": return T("strike.msg.clear_release", p.Length > 1 ? p[1] : "");
                case "clear_stage2": return T("strike.msg.clear_stage2", p.Length > 1 ? p[1] : "");
                case "clear_rewave": return T("strike.msg.clear_rewave", p.Length > 1 ? p[1] : "");
                case "clear_timeout": return T("strike.msg.clear_timeout", p.Length > 1 ? p[1] : "");
                case "clear_fuel": return T("strike.msg.clear_fuel", p.Length > 1 ? p[1] : "");
                case "clear_lost": return T("strike.msg.clear_lost", p.Length > 1 ? p[1] : "");
                case "clear_red": return T("strike.msg.clear_red");
                case "clear_stale": return T("strike.msg.clear_stale");
                case "clear_loaded": return T("strike.msg.clear_loaded");
                default: return "";
            }
        }

        private static string PctOf(string p) { double d; return double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? Math.Round(d * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : p; }

        public static string RoeText(string roe) { return Table("strike.roe.", roe ?? "veto", "strike.why.generic"); }
        public static string LaunchWhyText(string code) { return Table("strike.launch.why.", code ?? "no_plan", "strike.why.generic"); }
        public static string FateText(string state) { return Table("strike.fate.", state ?? "unknown", "strike.why.generic"); }

        /// <summary>The state of an execution row: "to the gate", "held: contact lost", "skipped: too late", "launched (+12 s late)".</summary>
        public static string RowStateText(StrikeTimelineRow t)
        {
            if (t == null) return "";
            string st = t.State ?? "plan";
            string text = Table("strike.state.", st, "strike.why.generic");
            if (!string.IsNullOrEmpty(t.Why) && (st == "held" || st == "skipped" || st == "failed" || st == "cancelled")) text += ": " + Table("strike.state.why.", t.Why, "strike.why.generic");
            if (t.LateS > LateToleranceS && st != "skipped" && st != "failed" && st != "cancelled") text += " " + T("strike.state.late", F1(Math.Round(t.LateS)));
            if (st == "launched" && t.DtOrderS >= 0) text += " " + T("strike.state.dt", F1(Math.Round(t.DtOrderS, 1)));
            return text;
        }

        /// <summary>The way inside the SAM zones: "12 nm / 1:35 under the horizon" | "... above the horizon" | "-".</summary>
        public static string RiskText(StrikeTimelineRow t)
        {
            if (t == null || t.InSamNm <= 0.05) return T("strike.risk.none");
            return T(t.UnderHorizon ? "strike.risk.under" : "strike.risk.above", F1(Math.Round(t.InSamNm, 1)), Mmss(t.InSamS));
        }

        public static string StaleText(string code) { return code == null ? "" : Table("strike.stale.", code, "strike.why.generic"); }
        public static string WaitText(string code) { return Table("strike.wait.", code ?? "none", "strike.why.generic"); }
        public static string GatherText(string code) { return Table("strike.gather.", code ?? "idle", "strike.why.generic"); }
        public static string PlanText(string status) { return Table("strike.plan.", status ?? "idle", "strike.why.generic"); }
        public static string LeftText(string code) { return Table("strike.left.", code ?? "", "strike.why.generic"); }

        /// <summary>The answer of a controller command ("!code|detail") as a line for the hint area.</summary>
        public static string ErrText(string answer)
        {
            if (answer == null) return T("strike.err.generic", "");
            string a = answer.StartsWith("!", StringComparison.Ordinal) ? answer.Substring(1) : answer;
            int bar = a.IndexOf('|');
            string code = bar < 0 ? a : a.Substring(0, bar), detail = bar < 0 ? "" : a.Substring(bar + 1);
            string key = "strike.err." + code;
            if (!UiText.Has(UiText.DefaultSection, key)) return T("strike.err.generic", a);
            if (code == "gate" || code == "launch_land" || code == "not_airborne" || code == "no_aircraft") return T(key, detail);
            if (code == "target_excluded" || code == "not_striker") return T(key, detail.Length == 0 ? "" : (UiText.Has(UiText.DefaultSection, "strike.excl." + detail) ? ExclText(detail) : WhyText(detail)));
            return T(key, detail);
        }

        public static string SelfTest()
        {
            if (Mmss(125) != "2:05" || Mmss(-61) != "-1:01" || Mmss(0) != "0:00") return "mmss";
            foreach (string code in new[] { "helicopter", "no_asm", "tv_only", "no_ammo", "mount_inoperable", "ew_later", "deck_not_ready", "loadout_unknown", "ew_no_api", "ew_inoperable" })
                if (WhyText(code) == "strike.why." + code || WhyText(code).Length == 0) return "why " + code;
            if (!WhyText("in_group:A").Contains("A") || !WhyText("owned:cap:CAP-1").Contains("cap:CAP-1")) return "why with an argument";
            foreach (string code in new[] { "civilian", "neutral", "friendly", "not_identified", "not_hostile" }) if (ExclText(code) == "strike.excl." + code) return "excl " + code;
            var kidd = new StrikeShipRow { Ksub = 8.86, Ssub = 27.7, AdSource = "cache", Tons = 8300, Why = "ad" };
            string ad = AdCell(kidd);
            if (!ad.StartsWith("8.9/27.7", StringComparison.Ordinal) || ad == "8.9/27.7") return "ad cell " + ad;
            if (AdCell(new StrikeShipRow { AdSource = "none" }) != "-") return "ad cell without data";
            if (!PrioText(kidd).Contains("8300") || !PrioText(new StrikeShipRow { Why = "low_role", Role = "Transport", Tons = 20000 }).Contains("Transport")) return "prio text";
            if (!ProbCell(0.91, true).Contains("91") || ProbCell(0.91, true) == ProbCell(0.91, false)) return "prob cell";
            string title = CardTitle(new StrikeCardView { Kind = "min_missiles", Missiles = 8, Targets = 1 });
            if (!title.Contains("8") || title.Contains("strike.")) return "card title " + title;
            if (!WarnText("short n=3 target=7002").Contains("3") || WarnText("short n=3 target=7002").Contains("target=") || WarnText("deck_pending:1") == "deck_pending:1") return "warn short " + WarnText("short n=3 target=7002");
            foreach (string w in new[] { "launch_land:4", "needs_illum uid=7", "sam_risk uid=4 nm=3.2 under_horizon", "fuel uid=1", "spread s=12.0 target=5", "not_airborne uid=9", "clearing_no_depletion" })
                if (WarnText(w).StartsWith("strike.", StringComparison.Ordinal) || WarnText(w).Length == 0) return "warn " + w;
            foreach (string s in new[] { "target_moved", "target_turned", "target_lost", "new_ad_ship", "aircraft_lost", "missiles_lost", "aircraft_changed", "target_changed", "loaded" })
                if (StaleText(s) == "strike.stale." + s) return "stale " + s;
            foreach (string w in new[] { "ew_late:5:4", "ew_uncovered:7002", "ew_band:7001:Ku", "ew_no_station:5:no_station:no_sea", "ew_not_airborne:5" })
                if (WarnText(w).StartsWith("strike.", StringComparison.Ordinal) || WarnText(w).Length == 0) return "warn " + w;
            if (!WarnText("ew_late:5:4").Contains("4") || !WarnText("ew_band:7001:Ku").Contains("Ku") || !WarnText("ew_band:7001:Ku").Contains("7001")) return "ew warn args";
            foreach (string w in new[] { "mixed_shift uid=2 s=66", "mix_late uid=2 ammo=wp_as-2", "kill_unknown target=7", "kill_short n=4 target=100" })
                if (WarnText(w).StartsWith("strike.", StringComparison.Ordinal) || WarnText(w).Length == 0 || WarnText(w).Contains("=")) return "warn " + w;
            Func<string, string> nm = k => k == "uid:2" ? "Tu-16K Badger-G" : k == "ammo:wp_as-2" ? "AS-2 Kipper" : null;
            string named = WarnText("mix_late uid=2 ammo=wp_as-2", nm);
            if (!named.Contains("Tu-16K Badger-G") || !named.Contains("AS-2 Kipper") || named.Contains("wp_as-2")) return "T-349 mix_late names " + named;
            if (WarnText("mix_late uid=2 ammo=wp_as-2", k => null) != WarnText("mix_late uid=2 ammo=wp_as-2")) return "T-349 null resolver";
            foreach (string w in new[] { "mixed_shift uid=2 s=66", "needs_illum uid=2", "sam_risk uid=2 nm=3.2", "fuel uid=2" })
                if (!WarnText(w, nm).Contains("Tu-16K Badger-G") || WarnText(w, k => null) != WarnText(w)) return "T-349 uid name " + w;
            if (!WarnText("mix_late uid=2 ammo=wp_as-2").Contains("wp_as-2") || !WarnText("mixed_shift uid=2 s=66").Contains("66") || KindText("damage_only").StartsWith("strike.", StringComparison.Ordinal)) return "T-342 warn args";
            foreach (string st in new[] { "plan", "wait", "to_station", "on_station", "jamming", "off", "back", "guard", "retreat", "no_station", "lost" }) if (EwStateText(st) == "strike.ew.state." + st) return "ew state " + st;
            foreach (string w in new[] { "no_sea", "in_sam", "cone", "horizon", "route_in_sam" }) if (EwWhyText("no_station:" + w) == "strike.ewwhy." + w) return "ew why " + w;
            foreach (string m in new[] { "transit", "attack", "retreat" }) if (EwModeText(m) == "strike.ew.mode." + m) return "ew mode " + m;
            foreach (string n in new[] { "ewf_on:5:4021", "ewf_off:5:clear", "ewf_no_cover:5:F-14A", "ewf_uncovered:2", "ewf_ships_lost:6:1" }) if (EwNoteText(n).Length == 0 || EwNoteText(n).StartsWith("strike.", StringComparison.Ordinal)) return "ewf note " + n;
            if (!ErrText("!target_excluded|neutral").Contains(ExclText("neutral")) || !ErrText("!in_group|A").Contains("A") || ErrText("!weird|x").Length == 0) return "err text";
            return null;
        }
    }
}
