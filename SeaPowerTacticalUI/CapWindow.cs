// T-112 §3.4 CapWindow (pure text model of the "CAP" window). All strings are UiText keys cap.* (lang/language_xx/sea_power_tactical_ui.cap.ini, T-103).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed class CapGroupLine
    {
        public string Group; public double WNm, HNm; public int OnStation, N, Relieving, Ready; public double NextReliefS = -1, GapS = -1;
        public bool DeckPriority; public List<string> Warns;
    }

    internal static class CapWindow
    {
        private static string T(string key, params object[] args) { return UiText.Fmt(key, args); }

        private static bool Known(string key) { return UiText.Has(UiText.DefaultSection, key); }

        /// <summary>Runs f with the UI language temporarily switched (SelfTest / stands).</summary>
        internal static T In<T>(string lang, Func<T> f) { using (UiText.Push(lang)) return f(); }

        /// <summary>m:ss as mm:ss ("05:30"); negative -> em dash.</summary>
        public static string Mmss(double s)
        {
            if (s < 0 || double.IsNaN(s)) return "—";
            int t = (int)Math.Round(s);
            return (t / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (t % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private static string Size(double nm) { return Math.Round(nm).ToString("0", CultureInfo.InvariantCulture); }

        /// <summary>T-154: a group without an order has no zone to show ("0x0 nm" looked like an order): says so.</summary>
        public static string TaskLine(CapGroupLine l, bool ordered)
        {
            return ordered ? TaskLine(l) : T("cap.line.no_order", l.Group);
        }

        public static string TaskLine(CapGroupLine l)
        {
            var sb = new StringBuilder();
            sb.Append(T("cap.line.zone", l.Group, Size(l.WNm), Size(l.HNm)));
            sb.Append(" · ").Append(T("cap.line.onstation", l.OnStation, l.N));
            sb.Append(" · ").Append(T("cap.line.relieving", l.Relieving));
            sb.Append(" · ").Append(T("cap.line.ready", l.Ready));
            sb.Append(" · ").Append(T("cap.line.relief", Mmss(l.NextReliefS)));
            if (l.GapS >= 0) sb.Append(" · ").Append(T("cap.line.gap", Mmss(l.GapS)));
            if (l.DeckPriority) sb.Append(" · ").Append(T("cap.line.prio"));
            if (l.Warns != null) foreach (string w in l.Warns) if (!string.IsNullOrEmpty(w)) sb.Append(" · ").Append(WarnText(w));
            return sb.ToString();
        }

        /// <summary>where: zone|to_zone|leaving|deck_ready|deck_prep|deck_cool|hangar|player|game_zone|lost. reason: leave code (leaving) or a ready-in "mm:ss" (deck_prep/deck_cool).</summary>
        public static string WhereText(string where, string reason) { return WhereText(where, reason, null); }

        /// <summary>takeoff = a ready "takeoff in ..." span (deck_ready / deck_prep of a launch that is allowed), else null.</summary>
        public static string WhereText(string where, string reason, string takeoff)
        {
            switch (where)
            {
                case "leaving":
                    if (string.IsNullOrEmpty(reason)) return T("cap.where.leaving");
                    string rk = "cap.leave." + reason;
                    return T("cap.where.leaving_why", Known(rk) ? T(rk) : reason);
                case "deck_prep":
                case "deck_cool":
                case "deck_ready":
                    {
                        // live finding 0.27.2: with times the short state ("preparing · 26 min · takeoff 29 min") keeps the cell readable
                        bool timed = !string.IsNullOrEmpty(takeoff) || (where != "deck_ready" && !string.IsNullOrEmpty(reason));
                        string t = T("cap.where." + where + (timed ? "_short" : ""));
                        if (where != "deck_ready" && !string.IsNullOrEmpty(reason)) t += " · " + reason;
                        return string.IsNullOrEmpty(takeoff) ? t : t + " · " + T("cap.where.takeoff", takeoff);
                    }
                case "hangar":
                    return string.IsNullOrEmpty(reason) ? T("cap.where.hangar") : T("cap.where.hangar") + " · " + reason;
                case "deck_launch": case "zone": case "to_zone": case "player": case "game_zone": case "lost": case "deck_down":
                    return T("cap.where." + where);
                default:
                    return where ?? "";
            }
        }

        /// <summary>"6/8 SR/MR"</summary>
        public static string AamCell(int now, int start, string classes)
        {
            string s = now.ToString(CultureInfo.InvariantCulture) + "/" + start.ToString(CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(classes) ? s : s + " " + classes;
        }

        /// <summary>"54 % · 1:12": fuel percent and time to leave as h:mm; unknown time -> em dash.</summary>
        public static string FuelCell(double pct, double leaveInS)
        {
            string p = Math.Round(pct).ToString("0", CultureInfo.InvariantCulture) + " %";
            if (leaveInS < 0 || double.IsNaN(leaveInS)) return p + " · —";
            int m = (int)Math.Round(leaveInS / 60.0);
            return p + " · " + (m / 60).ToString(CultureInfo.InvariantCulture) + ":" + (m % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>in_group:&lt;name&gt;|game_cap_zone|ext_sar|player|hidden</summary>
        public static string ReasonText(string code)
        {
            if (code == null) return "";
            if (code.StartsWith("in_group:", StringComparison.Ordinal)) return T("cap.reason.in_group", code.Substring(9));
            string k = "cap.reason." + code;
            return Known(k) ? T(k) : code;
        }

        public static string WarnText(string code)
        {
            if (code == null) return "";
            string k = "cap.warn." + code;
            return Known(k) ? T(k) : code;
        }

        public static string LoadoutHint(int aam, string classes, int tanks, double hours, bool far)
        {
            string cl = string.IsNullOrEmpty(classes) ? "" : " (" + classes + ")";
            string h = Math.Max(0, hours).ToString("0.0", CultureInfo.InvariantCulture);
            if (UiText.Lang == "ru") h = h.Replace('.', ',');
            string s = T("cap.hint.main", aam, cl, tanks, h);
            return far ? s + " · " + T("cap.hint.far") : s;
        }

        /// <summary>null = ok, otherwise error text.</summary>
        public static string SelfTest()
        {
            {
                var l = new CapGroupLine { Group = "ПВО-1", WNm = 40, HNm = 20, OnStation = 2, N = 2, Relieving = 1, Ready = 2, NextReliefS = 754 };
                string ru = In("ru", () => TaskLine(l));
                if (ru != "ПВО-1 · зона 40×20 миль · в зоне 2/2 · на смену 1 · готовы 2 · смена через 12:34") return "task ru: " + ru;
                l.Group = "CAP-1";
                string en = In("en", () => TaskLine(l));
                if (en != "CAP-1 · zone 40×20 nm · on station 2/2 · relieving 1 · ready 2 · relief in 12:34") return "task en: " + en;
                l.NextReliefS = -1; l.GapS = 65; l.DeckPriority = true; l.Warns = new List<string> { "continuity_gap" };
                ru = In("ru", () => TaskLine(l));
                if (ru != "CAP-1 · зона 40×20 миль · в зоне 2/2 · на смену 1 · готовы 2 · смена через — · без прикрытия 01:05 · приоритет палубы · разрыв прикрытия: нет резерва") return "task gap ru: " + ru;
                en = In("en", () => TaskLine(l));
                if (!en.Contains("relief in — · no cover 01:05 · deck priority · cover gap: no reserve")) return "task gap en: " + en;

                if (AamCell(6, 8, "SR/MR") != "6/8 SR/MR" || AamCell(2, 2, "") != "2/2") return "aam";
                if (FuelCell(54, 4320) != "54 % · 1:12" || FuelCell(8.4, 300) != "8 % · 0:05" || FuelCell(50, -1) != "50 % · —") return "fuel " + FuelCell(54, 4320);

                string[] wh = { "zone", "to_zone", "leaving", "deck_ready", "deck_prep", "deck_cool", "hangar", "player", "game_zone", "lost", "deck_launch", "deck_down" };
                string[] wru = { "в зоне", "идёт в зону", "уходит", "на палубе: готов", "на палубе: готовится", "на палубе: обслуживание", "в ангаре", "занят игроком", "в зоне миссии", "потерян", "взлетает", "палуба выведена из строя" };
                string[] wen = { "on station", "heading to zone", "leaving", "on deck: ready", "on deck: preparing", "on deck: servicing", "in hangar", "taken by player", "in mission zone", "lost", "launching", "flight deck out of action" };
                for (int i = 0; i < wh.Length; i++)
                {
                    if (In("ru", () => WhereText(wh[i], null)) != wru[i]) return "where ru " + wh[i];
                    if (In("en", () => WhereText(wh[i], null)) != wen[i]) return "where en " + wh[i];
                }
                if (In("ru", () => WhereText("leaving", "missiles")) != "уходит (ракеты)" || In("en", () => WhereText("leaving", "game_bingo")) != "leaving (game: Bingo)") return "where leaving reason";
                if (In("ru", () => WhereText("deck_prep", "26 мин")) != "готовится · 26 мин" || In("en", () => WhereText("deck_cool", "1 min")) != "servicing · 1 min") return "where deck time";
                if (In("ru", () => WhereText("deck_prep", "26 мин", "29 мин")) != "готовится · 26 мин · взлёт 29 мин" || In("en", () => WhereText("deck_ready", null, "3 min")) != "ready · takeoff 3 min") return "where takeoff";
                if (In("en", () => WhereText("deck_prep", "26 min", "29 min")) != "preparing · 26 min · takeoff 29 min" || In("ru", () => WhereText("deck_ready", null, "3 мин")) != "готов · взлёт 3 мин") return "where takeoff 2";
                if (In("ru", () => WhereText("hangar", "15 мин")) != "в ангаре · 15 мин" || In("en", () => WhereText("hangar", null)) != "in hangar") return "where hangar time";
                if (In("en", () => UiText.Duration(45)) != "45 s" || In("ru", () => UiText.Duration(1560)) != "26 мин" || In("en", () => UiText.Duration(1561)) != "27 min" || In("en", () => UiText.Duration(3900)) != "1 h 05 min" || UiText.Duration(-1) != "\u2014") return "duration";
                if (In("en", () => WhereText("weird", null)) != "weird") return "where unknown";

                if (In("ru", () => ReasonText("in_group:ПЛО-1")) != "в группе ПЛО-1" || In("en", () => ReasonText("in_group:ПЛО-1")) != "in group ПЛО-1") return "reason in_group";
                string[] rc = { "game_cap_zone", "ext_sar", "player", "hidden", "strike_loadout" };
                foreach (string c in rc) if (In("ru", () => ReasonText(c)) == c || In("en", () => ReasonText(c)) == c) return "reason missing " + c;

                string[] wc = { "continuity_gap", "no_reserve", "loadout_fallback", "no_ammo", "zone_far", "anchor_lost", "player_took", "tc_blocked", "bingo_fuel", "deck_priority", "deck_down", "hangar_taken" };
                foreach (string c in wc) if (In("ru", () => WarnText(c)) == c || In("en", () => WarnText(c)) == c) return "warn missing " + c;
                if (In("en", () => WarnText("zzz")) != "zzz") return "warn unknown";

                if (In("ru", () => LoadoutHint(8, "SR/MR", 2, 6.04, false)) != "AAM 8 (SR/MR) · баков 2 · ≈6,0 ч в зоне") return "hint ru: " + In("ru", () => LoadoutHint(8, "SR/MR", 2, 6.04, false));
                if (In("en", () => LoadoutHint(6, "LR", 0, 1.25, true)) != "AAM 6 (LR) · tanks 0 · ≈1.3 h on station · zone is beyond the type's range"
                    && In("en", () => LoadoutHint(6, "LR", 0, 1.25, true)) != "AAM 6 (LR) · tanks 0 · ≈1.2 h on station · zone is beyond the type's range") return "hint far en: " + In("en", () => LoadoutHint(6, "LR", 0, 1.25, true));

                return null;
            }
        }
    }
}
