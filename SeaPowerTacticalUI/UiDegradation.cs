using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge.Ui
{
    // T-120 (ADR-0043): the one table "function -> capabilities it needs -> fallback -> what the player sees".
    // Pure: capabilities are removed only here (outside the core), and the one line for the player is built here.

    internal sealed class DegradeRow
    {
        public readonly string Id;            // "asw", "sar_air", ...: the manifest feature and the suffix of the text key deg.<Id>
        public readonly string Module;        // Id of the IUiFeature that carries the function
        public readonly string[] Needs;       // capabilities; one missing = the function is off (no fallback) or reduced (fallback, no Soft)
        public readonly string[] Soft;        // capabilities of the optional part: missing = reduced, the function itself works
        public readonly string FallbackKey;   // null = no fallback path (off). Otherwise a short English id of the path, written to the log
        public readonly string NameKey;       // "deg.asw": the player-visible name (UiText)
        public readonly string[] Hotkeys;     // config keys "Section.Hotkey" of the function (manifest)

        public DegradeRow(string id, string module, string[] needs, string fallback, string[] soft = null, string[] hotkeys = null)
        {
            Id = id; Module = module; Needs = needs; FallbackKey = fallback; NameKey = "deg." + id;
            Soft = soft ?? new string[0]; Hotkeys = hotkeys ?? new string[0];
        }
    }

    internal sealed class DegradeResult
    {
        public readonly List<DegradeRow> Off = new List<DegradeRow>();
        public readonly List<DegradeRow> Reduced = new List<DegradeRow>();
        public readonly HashSet<string> SkipInit = new HashSet<string>();   // modules whose every row is off: no Init
        public readonly List<string> LogLines = new List<string>();
        public bool Any { get { return Off.Count > 0 || Reduced.Count > 0; } }
    }

    /// <summary>The current result of the table for the modules (UiCore provides it as a service before the first Init): the notice module asks it once.</summary>
    internal sealed class DegradeView
    {
        private readonly Func<DegradeResult> _get;
        public DegradeView(Func<DegradeResult> get) { _get = get; }
        public DegradeResult Current { get { return _get == null ? null : _get(); } }
    }

    internal static class UiDegradation
    {
        private static string[] A(params string[] s) { return s; }

        /// <summary>Plan T-120 section 5. Stream M adds rows / capability names when it moves a module to its Specs (a test keeps every registry capability in Needs/Soft or in the core list).</summary>
        public static readonly DegradeRow[] Rows =
        {
            new DegradeRow("radar_coverage", "radar", A("radar_coverage"), null, hotkeys: A("RadarCoverage.Hotkey")),
            new DegradeRow("radar_jam", "radar", A("radar_jam"), "hide_part"),   // T-182: without the members the zone is drawn as before (no jamming cut)
            new DegradeRow("radar_layer", "radar_pump", A("radar_noesis"), "imgui"),
            new DegradeRow("map_layer_radar", "map_layer", A("radar_layer"), "hotkey_only"),
            new DegradeRow("map_menu_windows", "map_menu", A("map_menu_windows"), "map_layers"),
            new DegradeRow("game_toolbar", "game_toolbar", A("game_toolbar"), "map_menu"),   // T-174
            new DegradeRow("overview", "overview", A("overview"), null, hotkeys: A("Overview.Hotkey")),
            new DegradeRow("radial", "radial", A("radial_menu", "radial_lmb"), "no_map_block", A("radial_map"), A("Radial.Hotkey")),
            new DegradeRow("bot_submenu", "menu", A("bot_menu"), "ring"),
            new DegradeRow("standing_orders", "doctrine", A("standing_orders"), "starpom_items"),
            new DegradeRow("air_defense_ready", "doctrine", A("air_defense_ready"), null),
            new DegradeRow("evasion_own", "evasion", A("evasion_own"), null),
            new DegradeRow("air_evasion", "air_evasion", A("air_evasion"), null),
            new DegradeRow("sub_evasion", "evasion", A("sub_evasion"), null),
            new DegradeRow("identify", "identify", A("identify"), null),
            new DegradeRow("asw", "asw", A("asw_info", "asw_deck_ops", "air_ops"), null, hotkeys: A("Asw.Hotkey")),
            new DegradeRow("sar_air", "asw", A("asw", "air_ops", "sar"), null),
            new DegradeRow("sar", "sar", A("sar", "air_ops"), null),
            new DegradeRow("cap", "cap", A("cap", "air_ops"), null, hotkeys: A("Cap.Hotkey")),
            new DegradeRow("air_deck", "air_tick", A("air_ops"), "no_head_move", A("deck_prioritise")),
            new DegradeRow("smart_attack", "smart_attack", A("smart_attack"), "window_only", A("smart_pick_block")),
            new DegradeRow("intercept", "intercept", A("intercept"), null),
            new DegradeRow("contact_info", "contact_info", A("contact_info"), null, hotkeys: A("ContactInfo.Hotkey", "ContactInfo.Hotkey.Cpa", "ContactInfo.Hotkey.Speed", "ContactInfo.Hotkey.Ttg")),
            new DegradeRow("contact_info_age", "contact_info", A("contact_info", "contact_info_age"), "hide_part", hotkeys: A("ContactInfo.Hotkey.Age")),
            new DegradeRow("contact_info_layer", "contact_info", A("contact_info", "contact_info_layer"), "hide_part", hotkeys: A("ContactInfo.Hotkey.Depth")),
            new DegradeRow("contact_info_noise", "contact_info", A("contact_info", "contact_info_noise"), "hide_part", hotkeys: A("ContactInfo.Hotkey.Noise")),
            new DegradeRow("contact_info_waypoint", "contact_info", A("contact_info", "contact_info_waypoint"), "hide_part", hotkeys: A("ContactInfo.Hotkey.Waypoint")),
            new DegradeRow("range_rings", "range_rings", A("range_rings"), null, hotkeys: A("RangeRings.Hotkey", "RangeRings.Hotkey.Group")),
            new DegradeRow("range_rings_own", "range_rings", A("range_rings", "range_rings_own"), "hide_part"),
            new DegradeRow("range_rings_kin", "range_rings", A("range_rings", "range_rings_own", "range_rings_kin"), "hide_part"),   // T-321: without it the rings keep the nominal range and the geodesic shape
            new DegradeRow("range_rings_class", "range_rings", A("range_rings", "range_rings_class"), "hide_part"),
            new DegradeRow("range_rings_weapon", "range_rings", A("range_rings"), "hide_part", soft: A("range_rings_arcs")),   // T-171: wedges of the rings need the ini loader of the class
            new DegradeRow("contact_info_canvas", "contact_info", A("contact_info", "contact_info_canvas"), "imgui"),
            new DegradeRow("air_guard", "air_guard", A("air_guard"), "hide_part"),   // T-175: the cover ship with a silent radar is not marked
            new DegradeRow("air_guard_tc", "air_guard", A("air_guard_tc"), "hide_part"),   // T-175: without the setter only the notice "reduce the acceleration"
            new DegradeRow("air_loss", "air_guard", A("air_loss"), "hide_part"),   // T-175: without the landing mark no loss is reported
            new DegradeRow("hit_estimate", "hit_estimate", A("hit_estimate"), null),   // T-168
            new DegradeRow("hit_estimate_hits", "hit_estimate", A("hit_estimate", "hit_estimate_hits"), "hide_part"),
            new DegradeRow("hit_estimate_bonus", "hit_estimate", A("hit_estimate", "hit_estimate_bonus"), "hide_part"),
            new DegradeRow("hit_estimate_zones", "hit_estimate", A("hit_estimate", "hit_estimate_zones"), "hide_part"),   // T-170
            new DegradeRow("fire_veto", "harmony", A("fire_veto"), null),
            new DegradeRow("save_hook", "harmony", A("save_hook"), "watch_marker"),   // T-198: without the hook the groups are saved by watching last_save and restored only in the first session of a process
            new DegradeRow("buoy_revive", "harmony", A("buoy_revive"), "hide_part"),   // T-094: without the patch buoys without a launch platform stay dead after a load (as in the game)
            new DegradeRow("light_smoke", "perf", A("light_smoke"), null),
            new DegradeRow("msg_hostile_red", "messages", A("msg_hostile_red"), null),   // T-329
            new DegradeRow("wp_reached_quiet", "wp_quiet", A("wp_reached_quiet"), null),   // T-331
        };

        /// <summary>Hotkeys of functions that need nothing from the game and so have no row (the manifest lists them as well).</summary>
        public static readonly string[] OtherHotkeys = { "Settings.Hotkey", "Strike.Hotkey" };

        public static DegradeResult Evaluate(ISet<string> missingCaps, ISet<string> failedModules)
        {
            return Evaluate(Rows, missingCaps, failedModules);
        }

        /// <summary>
        /// A row is off when its module failed, or a Needs capability is missing and it has no fallback (or has a Soft part, then the fallback is for the Soft part only);
        /// reduced when a Needs capability is missing and the fallback exists, or a Soft capability is missing. A reduced row that shares a missing capability
        /// with an off row of its module (contact_info_age needs contact_info) is off as well.
        /// </summary>
        public static DegradeResult Evaluate(DegradeRow[] rows, ISet<string> missingCaps, ISet<string> failedModules)
        {
            var r = new DegradeResult();
            missingCaps = missingCaps ?? new HashSet<string>();
            failedModules = failedModules ?? new HashSet<string>();
            var state = new Dictionary<DegradeRow, int>();   // 0 ok, 1 reduced, 2 off
            foreach (var row in rows)
            {
                bool hard = row.Needs.Any(missingCaps.Contains);
                bool soft = row.Soft.Any(missingCaps.Contains);
                int s = 0;
                if (failedModules.Contains(row.Module)) s = 2;
                else if (hard) s = row.FallbackKey == null || row.Soft.Length > 0 ? 2 : 1;
                else if (soft) s = 1;
                state[row] = s;
            }
            foreach (var row in rows)
            {
                if (state[row] != 1) continue;
                foreach (var other in rows)
                {
                    if (state[other] != 2 || other.Module != row.Module || other == row) continue;
                    if (row.Needs.Any(n => missingCaps.Contains(n) && other.Needs.Contains(n))) { state[row] = 2; break; }
                }
            }
            foreach (var row in rows)
            {
                if (state[row] == 2) r.Off.Add(row);
                else if (state[row] == 1) r.Reduced.Add(row);
            }
            foreach (var module in rows.Select(x => x.Module).Distinct())
            {
                var mine = rows.Where(x => x.Module == module).ToList();
                if (mine.All(x => state[x] == 2) && !failedModules.Contains(module)) r.SkipInit.Add(module);
            }
            foreach (var row in r.Off)
            {
                var why = failedModules.Contains(row.Module) ? "module " + row.Module + " failed" : "missing " + string.Join(",", row.Needs.Where(missingCaps.Contains).ToArray());
                r.LogLines.Add("degraded: " + row.Id + " off (" + why + ")");
            }
            foreach (var row in r.Reduced)
            {
                var miss = row.Needs.Concat(row.Soft).Where(missingCaps.Contains).ToArray();
                r.LogLines.Add("degraded: " + row.Id + " reduced (missing " + string.Join(",", miss) + "; fallback " + (row.FallbackKey ?? "none") + ")");
            }
            return r;
        }

        /// <summary>The only place that removes capabilities: the Needs of every off row, except those another working row of the same table still needs.</summary>
        public static void Apply(ISet<string> capabilities, DegradeResult r)
        {
            Apply(Rows, capabilities, r);
        }

        public static void Apply(DegradeRow[] rows, ISet<string> capabilities, DegradeResult r)
        {
            var keep = new HashSet<string>();
            foreach (var row in rows) if (!r.Off.Contains(row)) foreach (var n in row.Needs) keep.Add(n);
            foreach (var row in r.Off) foreach (var n in row.Needs) if (!keep.Contains(n)) capabilities.Remove(n);
        }

        /// <summary>null = everything works. Otherwise the one line for the player: the names (text(row.NameKey)) joined by ", ".</summary>
        public static string PlayerLine(DegradeResult r, Func<string, string> text, Func<string, object[], string> fmt)
        {
            if (r == null || !r.Any) return null;
            string off = string.Join(", ", r.Off.Select(x => text(x.NameKey)).ToArray());
            string red = string.Join(", ", r.Reduced.Select(x => text(x.NameKey)).ToArray());
            if (r.Off.Count > 0 && r.Reduced.Count > 0) return fmt("deg.line_both", new object[] { off, red });
            if (r.Off.Count > 0) return fmt("deg.line_off", new object[] { off });
            return fmt("deg.line_reduced", new object[] { red });
        }

        // ------------------------------------------------------------------ self-test

        private static ISet<string> S(params string[] x) { return new HashSet<string>(x); }

        private static string Ids(IEnumerable<DegradeRow> rows) { return string.Join(",", rows.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray()); }

        /// <summary>null = ok.</summary>
        public static string SelfTest()
        {
            try
            {
                var ids = new HashSet<string>();
                foreach (var row in Rows)
                {
                    if (!ids.Add(row.Id)) return "degradation: duplicate row " + row.Id;
                    if (row.Needs.Length == 0 || string.IsNullOrEmpty(row.Module)) return "degradation: row " + row.Id + " has no Needs / Module";
                    if (row.Soft.Length > 0 && row.FallbackKey == null) return "degradation: row " + row.Id + " has a Soft part but no fallback";
                }
                var none = Evaluate(S(), S());
                if (none.Any || none.SkipInit.Count != 0 || PlayerLine(none, k => k, (k, a) => k) != null) return "degradation: nothing missing must give nothing";

                // no fallback: off; fallback: reduced
                // T-101: the evasion module has a ship row and a submarine row; it skips Init only when both are off
                var r0 = Evaluate(S("evasion_own"), S());
                if (r0.SkipInit.Contains("evasion")) return "degradation: evasion skipped while sub_evasion is live";
                var r1 = Evaluate(S("evasion_own", "sub_evasion", "radar_noesis"), S());
                if (Ids(r1.Off) != "evasion_own,sub_evasion" || Ids(r1.Reduced) != "radar_layer") return "degradation: off/reduced = " + Ids(r1.Off) + " / " + Ids(r1.Reduced);
                if (!r1.SkipInit.Contains("evasion") || r1.SkipInit.Contains("radar_pump")) return "degradation: SkipInit = " + string.Join(",", r1.SkipInit.ToArray());
                // a Soft capability reduces only
                var r2 = Evaluate(S("radial_map"), S());
                if (Ids(r2.Off) != "" || Ids(r2.Reduced) != "radial" || r2.SkipInit.Count != 0) return "degradation: soft = " + Ids(r2.Off) + " / " + Ids(r2.Reduced);
                var r2b = Evaluate(S("radial_menu"), S());
                if (Ids(r2b.Off) != "radial") return "degradation: a hard capability of a row with a Soft part is off";
                // a module with one live row still gets Init (asw lives while sar_air is off)
                var r3 = Evaluate(S("sar"), S());
                if (!r3.Off.Exists(x => x.Id == "sar_air") || !r3.Off.Exists(x => x.Id == "sar") || r3.SkipInit.Contains("asw") || !r3.SkipInit.Contains("sar")) return "degradation: asw / sar_air / sar = " + Ids(r3.Off) + " skip " + string.Join(",", r3.SkipInit.ToArray());
                // a sub-row follows the off parent row in the same module
                var r4 = Evaluate(S("contact_info"), S());
                if (Ids(r4.Off).Split(',').Length != 6 || !r4.SkipInit.Contains("contact_info")) return "degradation: contact_info family = " + Ids(r4.Off);
                var r5 = Evaluate(S("contact_info_age"), S());
                if (Ids(r5.Reduced) != "contact_info_age" || Ids(r5.Off) != "" || r5.SkipInit.Count != 0) return "degradation: a sub-row alone is reduced";
                // a failed module: its rows are off, but it is not "skipped" (it already ran)
                var r6 = Evaluate(S(), S("cap"));
                if (Ids(r6.Off) != "cap" || r6.SkipInit.Contains("cap")) return "degradation: failed module = " + Ids(r6.Off);

                // Apply removes exactly the Needs of the off rows, but keeps what a working row still needs
                var caps = S("asw_info", "asw_deck_ops", "air_ops", "asw", "sar", "cap", "evasion_own", "radar_coverage");
                var r7 = Evaluate(S("sar"), S());
                Apply(caps, r7);
                if (caps.Contains("sar") || !caps.Contains("radar_coverage") || !caps.Contains("cap")) return "degradation: Apply = " + string.Join(",", caps.OrderBy(x => x).ToArray());
                if (!caps.Contains("air_ops") || caps.Contains("asw") || !caps.Contains("asw_info")) return "degradation: Apply must keep what a working row needs, has " + string.Join(",", caps.OrderBy(x => x).ToArray());
                var caps2 = S("evasion_own", "identify");
                Apply(caps2, Evaluate(S(), S("evasion")));
                if (caps2.Contains("evasion_own") || !caps2.Contains("identify")) return "degradation: a failed Init drops its capability only";

                // the line for the player, en and ru
                foreach (var lang in new[] { "en", "ru" })
                {
                    UiText.LoadForTests(lang);
                    foreach (var row in Rows)
                    {
                        string n = UiText.Get(row.NameKey);
                        if (string.IsNullOrWhiteSpace(n) || n == row.NameKey) return "degradation: no text " + row.NameKey + " in " + lang;
                    }
                    foreach (var k in new[] { "deg.line_off", "deg.line_reduced", "deg.line_both" })
                        if (UiText.Get(k) == k) return "degradation: no text " + k + " in " + lang;
                    Func<string, string> text = UiText.Get;
                    Func<string, object[], string> fmt = (k, a) => UiText.Fmt(k, a);
                    string off = PlayerLine(Evaluate(S("evasion_own", "identify"), S()), text, fmt);
                    string red = PlayerLine(Evaluate(S("radar_noesis"), S()), text, fmt);
                    string both = PlayerLine(Evaluate(S("evasion_own", "radar_noesis"), S()), text, fmt);
                    string n1 = UiText.Get("deg.evasion_own"), n2 = UiText.Get("deg.identify"), n3 = UiText.Get("deg.radar_layer");
                    if (off == null || !off.Contains(n1 + ", " + n2) || off.Contains("{")) return "degradation: line_off (" + lang + ") = " + off;
                    if (red == null || !red.Contains(n3) || red.Contains("{")) return "degradation: line_reduced (" + lang + ") = " + red;
                    // M21: the real line of a switched-off CAP (the capability cap is gone): the exact text of the language
                    string capLine = PlayerLine(Evaluate(S("cap"), S()), text, fmt);
                    string capWant = lang == "en" ? "Tactical UI: turned off after a game update - Air defence. Details: LogOutput.log"
                                                  : "Tactical UI: выключено после обновления игры — ПВО. Подробности — LogOutput.log";
                    if (capLine != capWant) return "degradation: cap line (" + lang + ") = " + capLine;
                    if (both == null || !both.Contains(n1) || !both.Contains(n3) || both.Contains("{") || both == off || both == red) return "degradation: line_both (" + lang + ") = " + both;
                }
                UiText.LoadForTests("ru");
                return null;
            }
            catch (Exception e) { return "degradation: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
