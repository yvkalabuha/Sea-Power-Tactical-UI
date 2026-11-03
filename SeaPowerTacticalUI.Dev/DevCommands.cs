using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Dev
{
    /// <summary>Closed command list of the dev channel and its translation into `link` commands (ADR-0046, plan T-139 section 3).
    /// Anything outside the table is "forbidden"; unknown or mistyped arguments are "bad_args".</summary>
    internal static class DevCommandTable
    {
        private sealed class Row
        {
            public string Cmd, Group;
            public string[] Args, Link;
        }

        private static Row R(string cmd, string group, string args, string link)
        {
            return new Row
            {
                Cmd = cmd, Group = group,
                Args = args.Length == 0 ? new string[0] : args.Split(' '),
                Link = link.Length == 0 ? new string[0] : link.Split(' '),
            };
        }

        private const string AswOrderArgs = "target_uid depth tactics lead_nm width_nm rect bearing dist_nm size_nm pattern corridor_x_nm barrier_x_nm standoff";   // T-176: lead_nm / width_nm stay as synonyms (migrated by the mod)
        private const string CapOrderArgs = "rect bearing dist_nm size_nm w_nm origin_uid origin_lat origin_lon anchor_uid on_station ready alt_ft type loadout"
                                            + " mode ref_uid per_raid level radius_nm targets pursuit_nm speed roe after fill accept_alert";   // T-179 (ADR-0079): mode alert + its settings
        private const string CapAlertArgs = "ref_uid ready per_raid level radius_nm targets pursuit_nm speed roe after fill";
        private const string SarArgs = "area on_complete origin";
        private const string CapParamArgs = "missile_pct fuel_margin_s ring_margin_nm fuel_floor_pct need_long_range dash_rtb alt_ft";

        private static readonly Row[] Rows =
        {
            R("state", "read", "sections limit", ""),
            R("log", "read", "lines contains level", ""),
            R("gc", "read", "", ""),   // T-212: forced GC on the main thread, answers live_mb (never from a timer; a pause of 0.1-1 s)
            R("ping", "control", "", "ping"),
            R("window", "control", "name state", "overview radar_overlay ui_window"),
            R("ui_undo", "control", "window", "ui_undo"),   // T-178 I3 (ADR-0077 s.8): presses "Undo" of the card of "Stop all" / "Disband"
            R("layer", "control", "id on", "radar_overlay range_rings contact_info"),
            R("time", "control", "mode value", "pause unpause set_time_compression"),
            R("select_unit", "control", "uid track_id center", "select select_contact center_map"),
            R("group_create", "control", "kind group uids " + AswOrderArgs + " " + SarArgs + " " + CapOrderArgs,
                "asw_group asw_search_course asw_search_area sar_order cap_group cap_order cap_loadout"),
            R("group_delete", "control", "kind group", "asw_stop asw_group cap_stop cap_group"),
            R("cap_scramble", "control", "group", "cap_scramble"),   // T-179 (ADR-0079): "Scramble now" of a CAP group in the alert mode
            R("group_edit", "control", "kind group depth tactics add remove type loadout " + CapParamArgs + " name",
                "asw_depth asw_set_tactics asw_group cap_params cap_loadout cap_group"),
            R("intercept", "control", "uid target_id cancel", "intercept intercept_cancel intercept_status"),
            R("air_evade", "control", "uid", "air_evade"),
            R("sub_evade", "control", "uid", "sub_evade"),
            R("air_guard_ignore", "control", "group window", "air_guard_ignore"),
            R("air_guard_state", "control", "", "air_guard_state"),
            R("sub_datum_list", "control", "limit", "sub_datum_list"),   // T-288 (ADR-0115): the sub launch sectors
            R("sub_datum_search", "control", "datum group", "sub_datum_search"),   // "datum" = the sector id (the link argument "id"; "id" is a string argument of "layer")
            R("sub_datum_dismiss", "control", "datum", "sub_datum_dismiss"),
            R("air_ready", "control", "ship_uid key all", ""),   // QA cheat: runs in the dev DLL itself (DevAirReady), never through link
            R("gfx", "control", "op lever on", ""),               // T-165 graphics A/B: runs in the dev DLL itself (DevGfx), never through link
            R("force", "control", "op uid target_uid to_uid shooter target_id dist_nm bearing ahead_nm off_deg stop_nm ammo require_ai away hold_fire", "qa_force"),   // T-361 (ADR-0136): QA scenarios by real game orders, dev only
            R("attack", "fire", "shooter target weapon salvo", "attack"),
            R("smart_attack", "fire", "shooter target ammo", "smart_attack"),
            R("asw_attack", "fire", "proposal_id", "asw_attack"),   // T-173: the link answers no_confirm_needed (the question is gone); kept for old clients
            R("asw_strike", "fire", "group ref_uid radius_nm targets airborne", "asw_order"),   // T-173 (ADR-0074 s.8): a strike order = the consent to attack
            R("asw_strike_resume", "fire", "contact", "asw_strike_resume"),
            R("asw_strike_skip", "control", "contact", "asw_strike_skip"),
            R("set_roe", "fire", "uid formation roe", "set_roe"),
            R("strike_group", "control", "op group uids name deck_keys", "strike_group"),   // T-341 (ADR-0132): the strike groups (the window, the ring and the dev mod use one path)
            R("strike_target", "control", "group mode target_id track_ids", "strike_target"),
            R("strike_plan", "control", "group choose shift_s", "strike_plan"),   // counts the plan only (choose = the card of an existing plan); no launch
            R("strike_goal", "control", "group goal", "strike_goal"),   // T-342 (ADR-0133): the goal of the strike, breach | sink (not while the strike runs; a plan that exists goes stale)
            R("strike_deck_cancel", "control", "group key", "strike_deck_cancel"),   // T-342 B (ADR-0133): cancels one order of the window on a deck (key) or every one that has not launched; blocks the group's deck orders until the target changes or strike_deck_reorder
            R("strike_deck_reorder", "control", "group", "strike_deck_reorder"),   // T-342 B: lifts the block and counts the plan again (orders what is missing from the decks; never a launch)
            R("strike_auto", "control", "group on", "strike_auto"),   // T-342 C (ADR-0133): the flag Auto of a group; the plan, the waiting for the decks and the launches then follow by themselves
            R("strike_cancel", "control", "group", "strike_cancel"),   // T-341 B: drops the plan, the group waits at its point
            R("strike_launch", "fire", "group variant stale_ok", "strike_launch"),   // T-341 C: the launch of the chosen plan (REAL missiles: hostile identified ships only); stale_ok = the plan is known to be stale (the link arg confirm)
            R("strike_probe", "fire", "op uid target_id ammo salvo on obj", "strike_probe"),   // the early live probes; op fire is a REAL launch of anti-ship missiles
        };

        // argument types: S string, N number, I integer, B bool, U list of integers, T list of strings, A any non-null
        private static readonly Dictionary<string, char> Types = BuildTypes();

        private static Dictionary<string, char> BuildTypes()
        {
            var t = new Dictionary<string, char>();
            foreach (string s in "targets weapon ammo proposal_id roe name state id mode kind group window depth pattern on_complete type loadout contains level key op lever speed after goal".Split(' ')) t[s] = 'S';
            foreach (string s in "value radius_nm lead_nm width_nm corridor_x_nm barrier_x_nm bearing dist_nm size_nm w_nm origin_lat origin_lon missile_pct fuel_margin_s ring_margin_nm fuel_floor_pct alt_ft k pursuit_nm shift_s".Split(' ')) t[s] = 'N';
            foreach (string s in "ahead_nm off_deg stop_nm".Split(' ')) t[s] = 'N';   // T-361 (force)
            foreach (string s in "datum uid airborne ref_uid contact formation shooter target salvo track_id target_id target_uid to_uid origin_uid anchor_uid on_station ready limit lines ship_uid per_raid choose variant".Split(' ')) t[s] = 'I';
            foreach (string s in "on center cancel need_long_range dash_rtb all fill accept_alert stale_ok obj require_ai away hold_fire".Split(' ')) t[s] = 'B';
            foreach (string s in "uids add remove track_ids".Split(' ')) t[s] = 'U';
            foreach (string s in "tactics sections deck_keys".Split(' ')) t[s] = 'T';
            foreach (string s in "rect area origin standoff".Split(' ')) t[s] = 'A';
            return t;
        }

        private static Row Find(string cmd)
        {
            foreach (var r in Rows) if (r.Cmd == cmd) return r;
            return null;
        }

        /// <summary>"read" | "control" | "fire", or null when the command is not in the closed list.</summary>
        public static string GroupOf(string cmd)
        {
            var r = cmd == null ? null : Find(cmd);
            return r == null ? null : r.Group;
        }

        /// <summary>Reference table as JSON (compared with tests/data/ui_dev_command_table.json).</summary>
        public static string TableJson()
        {
            var l = new List<object>();
            foreach (var r in Rows)
                l.Add(new Dictionary<string, object> { { "cmd", r.Cmd }, { "group", r.Group }, { "args", new List<object>(r.Args) }, { "link", new List<object>(r.Link) } });
            return DevJson.Write(new Dictionary<string, object> { { "commands", l } });
        }

        private static bool TypeOk(char t, object v)
        {
            switch (t)
            {
                case 'S': return v is string;
                case 'N': return v is double;
                case 'I': return v is double && Math.Abs((double)v - Math.Round((double)v)) < 1e-9;
                case 'B': return v is bool;
                case 'U':
                {
                    var l = v as List<object>;
                    if (l == null) return false;
                    foreach (object o in l) if (!TypeOk('I', o)) return false;
                    return true;
                }
                case 'T':
                {
                    var l = v as List<object>;
                    if (l == null) return false;
                    foreach (object o in l) if (!(o is string)) return false;
                    return true;
                }
                default: return v != null;
            }
        }

        private static string Bad(string detail) { return "bad_args:" + detail; }

        private static Dictionary<string, object> Step(string cmd)
        {
            return new Dictionary<string, object> { { "cmd", cmd } };
        }

        private static void Copy(Dictionary<string, object> to, Dictionary<string, object> from, string names)
        {
            foreach (string n in names.Split(' '))
            {
                object v;
                if (n.Length > 0 && from.TryGetValue(n, out v)) to[n] = v;
            }
        }

        private static bool Has(Dictionary<string, object> a, string names)
        {
            foreach (string n in names.Split(' ')) if (a.ContainsKey(n)) return true;
            return false;
        }

        private static bool Only(Dictionary<string, object> a, string allowed, out string bad)
        {
            var set = new HashSet<string>(allowed.Split(' '));
            foreach (string k in a.Keys) if (!set.Contains(k)) { bad = k; return false; }
            bad = null;
            return true;
        }

        private static string Str(Dictionary<string, object> a, string k)
        {
            object v; return a.TryGetValue(k, out v) ? v as string : null;
        }

        private static bool IsKind(string k) { return k == "asw" || k == "sar" || k == "cap"; }

        /// <summary>Builds the `link` step chain. err: "forbidden" | "bad_args:&lt;detail&gt;" | "unknown_layer"; null = ok.</summary>
        public static bool Translate(string cmd, Dictionary<string, object> args, out List<Dictionary<string, object>> steps, out string err)
        {
            steps = new List<Dictionary<string, object>>();
            err = null;
            var row = cmd == null ? null : Find(cmd);
            if (row == null) { err = "forbidden"; return false; }
            if (args == null) args = new Dictionary<string, object>();
            var allowed = new HashSet<string>(row.Args);
            foreach (var kv in args)
            {
                if (!allowed.Contains(kv.Key)) { err = Bad("unknown argument " + kv.Key); return false; }
                char t;
                if (!Types.TryGetValue(kv.Key, out t) || !TypeOk(t, kv.Value)) { err = Bad("argument " + kv.Key + " has a wrong type"); return false; }
            }
            if (row.Group == "read") return true;
            err = Build(row.Cmd, args, steps);
            return err == null;
        }

        private static string Build(string cmd, Dictionary<string, object> a, List<Dictionary<string, object>> steps)
        {
            string bad;
            switch (cmd)
            {
                case "ping": steps.Add(Step("ping")); return null;
                case "window": return Window(a, steps);
                case "layer":
                {
                    string lid = Str(a, "id");
                    string link = lid == "radar_coverage" ? "radar_overlay" : lid == "range_rings" ? "range_rings" : lid == "contact_info" ? "contact_info" : null;
                    if (link == null) return "unknown_layer";
                    var s = Step(link); Copy(s, a, "on"); steps.Add(s); return null;
                }
                case "cap_scramble":
                {
                    if (string.IsNullOrEmpty(Str(a, "group"))) return Bad("group is required");
                    var s = Step("cap_scramble"); s["group"] = a["group"]; steps.Add(s); return null;
                }
                case "ui_undo":
                {
                    string w = Str(a, "window");
                    if (w != "asw" && w != "cap") return Bad("window: asw|cap");
                    var s = Step("ui_undo"); s["window"] = w; steps.Add(s); return null;
                }
                case "time": return Time(a, steps);
                case "select_unit": return Select(a, steps);
                case "group_create": return GroupCreate(a, steps);
                case "group_delete": return GroupDelete(a, steps);
                case "group_edit": return GroupEdit(a, steps);
                case "attack":
                {
                    if (!a.ContainsKey("shooter") || !a.ContainsKey("target")) return Bad("shooter and target are required");
                    double sv = a.ContainsKey("salvo") ? (double)a["salvo"] : 1;
                    if (sv < 1 || sv > 8) return Bad("salvo 1..8");
                    var s = Step("attack");
                    s["uid"] = a["shooter"]; s["target_id"] = a["target"]; Copy(s, a, "weapon salvo"); s["confirm"] = true;
                    steps.Add(s); return null;
                }
                case "intercept":
                {
                    if (!a.ContainsKey("uid")) return Bad("uid is required");
                    bool cancel = a.ContainsKey("cancel") && a["cancel"] is bool && (bool)a["cancel"];
                    if (cancel && a.ContainsKey("target_id")) return Bad("cancel excludes target_id");
                    var s = Step(cancel ? "intercept_cancel" : a.ContainsKey("target_id") ? "intercept" : "intercept_status");
                    s["uid"] = a["uid"]; Copy(s, a, "target_id");
                    steps.Add(s); return null;
                }
                case "sub_datum_list":
                {
                    var s = Step("sub_datum_list"); Copy(s, a, "limit"); steps.Add(s); return null;
                }
                case "sub_datum_search":
                case "sub_datum_dismiss":
                {
                    if (!a.ContainsKey("datum")) return Bad("datum is required");
                    var s = Step(cmd); s["id"] = a["datum"];
                    if (cmd == "sub_datum_search") Copy(s, a, "group");
                    steps.Add(s); return null;
                }
                case "air_ready":
                {
                    bool all = a.ContainsKey("all") && (bool)a["all"];
                    if (all && Has(a, "ship_uid key")) return Bad("all excludes ship_uid and key");
                    if (!all && !Has(a, "ship_uid key")) return Bad("one of ship_uid, key, all=true is required");
                    string k = Str(a, "key");
                    if (k != null && !k.StartsWith("task:", StringComparison.Ordinal) && !k.StartsWith("cool:", StringComparison.Ordinal)) return Bad("key: task:<uid>|cool:<uid>");
                    return null;   // no link steps: DevExecutor runs it locally
                }
                case "gfx":
                {
                    string op = Str(a, "op");
                    if (op == "set")
                    {
                        string lever = Str(a, "lever");
                        if (lever == null || Array.IndexOf(DevGfx.Levers, lever) < 0) return Bad("lever: " + string.Join("|", DevGfx.Levers));
                        if (!a.ContainsKey("on")) return Bad("on is required");
                        return null;
                    }
                    if (op != "dump" && op != "reset") return Bad("op: dump|set|reset");
                    if (Has(a, "lever on")) return Bad(op + " takes no lever / on");
                    return null;   // no link steps: DevExecutor runs it locally (DevGfx)
                }
                case "force":
                {
                    string ferr = DevForce.Check(a);
                    if (ferr != null) return ferr;
                    var s = Step("qa_force"); Copy(s, a, "op uid target_uid to_uid shooter target_id dist_nm bearing ahead_nm off_deg stop_nm ammo require_ai away hold_fire"); steps.Add(s); return null;
                }
                case "smart_attack":
                {
                    if (!a.ContainsKey("shooter") || !a.ContainsKey("target")) return Bad("shooter and target are required");
                    var s = Step("smart_attack");
                    s["uid"] = a["shooter"]; s["target_id"] = a["target"]; Copy(s, a, "ammo");
                    steps.Add(s); return null;
                }
                case "asw_attack":
                {
                    if (!a.ContainsKey("proposal_id")) return Bad("proposal_id is required");
                    var s = Step("asw_attack");
                    s["proposal_id"] = a["proposal_id"]; s["confirm"] = true; s["by"] = "window";
                    steps.Add(s); return null;
                }
                case "asw_strike":
                {
                    if (!a.ContainsKey("group")) return Bad("group is required");
                    var s = Step("asw_order");
                    s["group"] = a["group"]; s["order"] = "strike";
                    Copy(s, a, "ref_uid"); Copy(s, a, "radius_nm"); Copy(s, a, "targets"); Copy(s, a, "airborne");
                    steps.Add(s); return null;
                }
                case "asw_strike_resume":
                case "asw_strike_skip":
                {
                    if (!a.ContainsKey("contact")) return Bad("contact is required");
                    var s = Step(cmd);
                    s["contact"] = a["contact"];
                    steps.Add(s); return null;
                }
                case "strike_group":
                {
                    string sop = Str(a, "op");
                    if (sop != "create" && sop != "add" && sop != "remove" && sop != "disband" && sop != "rename") return Bad("op: create|add|remove|disband|rename");
                    var s = Step("strike_group"); Copy(s, a, "op group uids name deck_keys"); steps.Add(s); return null;
                }
                case "strike_target":
                {
                    if (!a.ContainsKey("group") || !a.ContainsKey("target_id")) return Bad("group and target_id are required");
                    var s = Step("strike_target"); Copy(s, a, "group mode target_id track_ids"); steps.Add(s); return null;
                }
                case "strike_plan":
                {
                    if (!a.ContainsKey("group")) return Bad("group is required");
                    var s = Step("strike_plan"); Copy(s, a, "group choose shift_s"); steps.Add(s); return null;
                }
                case "strike_goal":
                {
                    if (!a.ContainsKey("group")) return Bad("group is required");
                    string sg = Str(a, "goal");
                    if (sg != "breach" && sg != "sink") return Bad("goal: breach|sink");
                    var s = Step("strike_goal"); s["group"] = a["group"]; s["goal"] = sg; steps.Add(s); return null;
                }
                case "strike_deck_cancel":
                {
                    if (!a.ContainsKey("group")) return Bad("group is required");
                    var s = Step("strike_deck_cancel"); Copy(s, a, "group key"); steps.Add(s); return null;
                }
                case "strike_deck_reorder":
                {
                    if (!a.ContainsKey("group")) return Bad("group is required");
                    var s = Step("strike_deck_reorder"); s["group"] = a["group"]; steps.Add(s); return null;
                }
                case "strike_auto":
                {
                    if (!a.ContainsKey("group")) return Bad("group is required");
                    if (!(a.ContainsKey("on") && a["on"] is bool)) return Bad("on is required (true|false)");
                    var s = Step("strike_auto"); s["group"] = a["group"]; s["on"] = a["on"]; steps.Add(s); return null;
                }
                case "strike_cancel":
                {
                    if (!a.ContainsKey("group")) return Bad("group is required");
                    var s = Step("strike_cancel"); s["group"] = a["group"]; steps.Add(s); return null;
                }
                case "strike_launch":
                {
                    if (!a.ContainsKey("group")) return Bad("group is required");
                    var s = Step("strike_launch"); Copy(s, a, "group variant"); if (a.ContainsKey("stale_ok") && a["stale_ok"] is bool && (bool)a["stale_ok"]) s["confirm"] = true; steps.Add(s); return null;
                }
                case "strike_probe":
                {
                    if (Str(a, "op") == null) return Bad("op is required");   // an unknown op goes on to the mod, which answers bad_args itself
                    if (a.ContainsKey("salvo") && ((double)a["salvo"] < 1 || (double)a["salvo"] > 8)) return Bad("salvo 1..8");
                    var s = Step("strike_probe"); Copy(s, a, "op uid target_id ammo salvo on obj"); steps.Add(s); return null;
                }
                case "set_roe":
                {
                    string roe = Str(a, "roe");
                    if (roe != "tight" && roe != "hold" && roe != "free") return Bad("roe: tight|hold|free");
                    if (a.ContainsKey("uid") == a.ContainsKey("formation")) return Bad("exactly one of uid, formation");
                    var s = Step("set_roe");
                    if (a.ContainsKey("uid")) { s["uid"] = a["uid"]; }
                    else { s["uid"] = a["formation"]; s["scope"] = "formation"; s["formation"] = a["formation"]; }
                    s["status"] = roe;
                    if (roe == "free") s["confirm"] = true;
                    steps.Add(s); return null;
                }
            }
            return Only(a, "", out bad) ? null : "forbidden";
        }

        private static string Window(Dictionary<string, object> a, List<Dictionary<string, object>> steps)
        {
            string name = Str(a, "name"), st = Str(a, "state") ?? "toggle";
            if (st != "open" && st != "close" && st != "toggle") return Bad("state: open|close|toggle");
            string link = name == "overview" ? "overview" : name == "radar" ? "radar_overlay" : (name == "asw" || name == "cap" || name == "strike") ? "ui_window" : null;
            if (link == null) return Bad("name: overview|radar|asw|cap|strike");
            var s = Step(link);
            if (link == "ui_window") s["name"] = name;
            if (st != "toggle") s["on"] = st == "open";
            steps.Add(s);
            return null;
        }

        private static string Time(Dictionary<string, object> a, List<Dictionary<string, object>> steps)
        {
            string mode = Str(a, "mode");
            if (mode == "pause") { steps.Add(Step("pause")); return null; }
            if (mode == "resume") { steps.Add(Step("unpause")); return null; }
            if (mode == "speed")
            {
                if (!a.ContainsKey("value")) return Bad("value is required for speed");
                var s = Step("set_time_compression"); s["value"] = a["value"]; steps.Add(s); return null;
            }
            return Bad("mode: pause|resume|speed");
        }

        private static string Select(Dictionary<string, object> a, List<Dictionary<string, object>> steps)
        {
            if (a.ContainsKey("uid") == a.ContainsKey("track_id")) return Bad("exactly one of uid, track_id");
            bool own = a.ContainsKey("uid");
            string key = own ? "uid" : "track_id";
            var s = Step(own ? "select" : "select_contact"); s[key] = a[key]; steps.Add(s);
            if (a.ContainsKey("center") && (bool)a["center"]) { var c = Step("center_map"); c[key] = a[key]; steps.Add(c); }
            return null;
        }

        private static string KindGroup(Dictionary<string, object> a, out string kind, out string group)
        {
            kind = Str(a, "kind"); group = Str(a, "group");
            if (!IsKind(kind)) return Bad("kind: asw|sar|cap");
            if (string.IsNullOrEmpty(group)) return Bad("group is required");
            return null;
        }

        private static string GroupCreate(Dictionary<string, object> a, List<Dictionary<string, object>> steps)
        {
            string kind, group, bad;
            string e = KindGroup(a, out kind, out group);
            if (e != null) return e;
            if (!a.ContainsKey("uids")) return Bad("uids is required");
            bool alertMode = kind == "cap" && Str(a, "mode") == "alert";
            if (kind == "cap" && a.ContainsKey("mode") && Str(a, "mode") != "patrol" && !alertMode) return Bad("mode: patrol|alert");
            string capAllowed = alertMode ? "kind group uids mode type loadout " + CapAlertArgs : "kind group uids mode accept_alert " + "rect bearing dist_nm size_nm w_nm origin_uid origin_lat origin_lon anchor_uid on_station ready alt_ft type loadout";
            string allowed = kind == "cap" ? capAllowed : "kind group uids " + (kind == "asw" ? AswOrderArgs : SarArgs);
            if (!Only(a, allowed, out bad)) return Bad(bad + " is not allowed for " + kind + (alertMode ? " (mode alert)" : ""));
            var g = Step(kind == "cap" ? "cap_group" : "asw_group");
            g["op"] = "create"; g["group"] = group; g["uids"] = a["uids"];
            if (kind != "cap") g["kind"] = kind;   // T-346 D4: the controller refuses the aircraft that cannot serve in this kind of group
            steps.Add(g);
            if (kind == "asw")
            {
                bool area = Has(a, "rect bearing dist_nm size_nm pattern");
                if (area)
                {
                    var s = Step("asw_search_area"); s["group"] = group; Copy(s, a, "rect bearing dist_nm size_nm target_uid depth tactics pattern"); steps.Add(s);
                }
                else if (a.ContainsKey("target_uid"))
                {
                    var s = Step("asw_search_course"); s["group"] = group; Copy(s, a, "target_uid depth tactics lead_nm width_nm corridor_x_nm barrier_x_nm standoff"); steps.Add(s);
                }
            }
            else if (kind == "sar")
            {
                var s = Step("sar_order"); s["group"] = group; Copy(s, a, SarArgs); steps.Add(s);
            }
            else if (alertMode)
            {
                var s = Step("cap_order"); s["group"] = group; s["kind"] = "alert"; Copy(s, a, CapAlertArgs); steps.Add(s);   // one order of the alert kind (no zone)
                if (a.ContainsKey("type") && a.ContainsKey("loadout"))
                {
                    var l = Step("cap_loadout"); l["group"] = group; Copy(l, a, "type loadout"); steps.Add(l);
                }
            }
            else
            {
                if (Has(a, "rect bearing dist_nm size_nm w_nm origin_uid origin_lat origin_lon"))
                {
                    var s = Step("cap_order"); s["group"] = group; Copy(s, a, "rect bearing dist_nm size_nm w_nm origin_uid origin_lat origin_lon anchor_uid on_station ready alt_ft accept_alert"); steps.Add(s);
                }
                else if (a.ContainsKey("alt_ft")) { var s = Step("cap_params"); s["group"] = group; Copy(s, a, "alt_ft"); steps.Add(s); }   // T-166: altitude without an order
                if (a.ContainsKey("type") && a.ContainsKey("loadout"))
                {
                    var s = Step("cap_loadout"); s["group"] = group; Copy(s, a, "type loadout"); steps.Add(s);
                }
            }
            return null;
        }

        private static string GroupDelete(Dictionary<string, object> a, List<Dictionary<string, object>> steps)
        {
            string kind, group;
            string e = KindGroup(a, out kind, out group);
            if (e != null) return e;
            var stop = Step(kind == "cap" ? "cap_stop" : "asw_stop"); stop["group"] = group; steps.Add(stop);
            var d = Step(kind == "cap" ? "cap_group" : "asw_group"); d["op"] = "disband"; d["group"] = group; steps.Add(d);
            return null;
        }

        private static string GroupEdit(Dictionary<string, object> a, List<Dictionary<string, object>> steps)
        {
            string kind, group, bad;
            string e = KindGroup(a, out kind, out group);
            if (e != null) return e;
            string allowed = kind == "cap" ? "kind group add remove type loadout name " + CapParamArgs : "kind group depth tactics add remove name";
            if (!Only(a, allowed, out bad)) return Bad(bad + " is not allowed for " + kind);
            string grp = kind == "cap" ? "cap_group" : "asw_group";
            if (kind == "cap")
            {
                if (Has(a, CapParamArgs)) { var s = Step("cap_params"); s["group"] = group; Copy(s, a, CapParamArgs); steps.Add(s); }
                if (a.ContainsKey("type") || a.ContainsKey("loadout"))
                {
                    if (!(a.ContainsKey("type") && a.ContainsKey("loadout"))) return Bad("type and loadout go together");
                    var s = Step("cap_loadout"); s["group"] = group; Copy(s, a, "type loadout"); steps.Add(s);
                }
            }
            else
            {
                if (a.ContainsKey("depth")) { var s = Step("asw_depth"); s["group"] = group; s["mode"] = a["depth"]; steps.Add(s); }
                if (a.ContainsKey("tactics")) { var s = Step("asw_set_tactics"); s["group"] = group; s["tactics"] = a["tactics"]; steps.Add(s); }
            }
            foreach (string op in new[] { "add", "remove" })
                if (a.ContainsKey(op)) { var s = Step(grp); s["op"] = op; s["group"] = group; s["uids"] = a[op]; if (op == "add" && kind != "cap") s["kind"] = kind; steps.Add(s); }
            // T-326: the rename goes last: the other steps address the group by its old name
            if (a.ContainsKey("name"))
            {
                if (!(a["name"] is string)) return Bad("name must be a string");
                var s = Step(grp); s["op"] = "rename"; s["group"] = group; s["name"] = a["name"]; steps.Add(s);
            }
            return steps.Count == 0 ? Bad("nothing to edit") : null;
        }

        public static string SelfTest()
        {
            List<Dictionary<string, object>> st; string err;
            if (Translate("foo", null, out st, out err) || err != "forbidden") return "forbidden foo";
            var a = new Dictionary<string, object> { { "shooter", 1.0 }, { "target", 101.0 }, { "scope", "all" } };
            if (Translate("attack", a, out st, out err) || !err.StartsWith("bad_args")) return "scope not rejected";
            a.Remove("scope"); a["extra"] = true;
            if (Translate("attack", a, out st, out err) || !err.StartsWith("bad_args")) return "extra key accepted";
            a.Clear(); a["shooter"] = 1.0; a["target"] = 101.0;
            if (!Translate("attack", a, out st, out err) || st.Count != 1 || !(bool)st[0]["confirm"]) return "attack";
            var w = new Dictionary<string, object> { { "name", "asw" }, { "state", "open" } };
            if (!Translate("window", w, out st, out err) || st[0]["cmd"] as string != "ui_window" || !(bool)st[0]["on"]) return "window";
            var gc = new Dictionary<string, object> { { "kind", "cap" }, { "group", "g" }, { "uids", new List<object> { 1.0 } }, { "roe", "free" } };
            if (Translate("group_create", gc, out st, out err) || !err.StartsWith("bad_args")) return "cap roe accepted";
            var ga = new Dictionary<string, object> { { "kind", "cap" }, { "group", "g" }, { "uids", new List<object> { 1.0 } }, { "alt_ft", 9000.0 } };
            if (!Translate("group_create", ga, out st, out err) || st.Count != 2 || st[1]["cmd"] as string != "cap_params" || (double)st[1]["alt_ft"] != 9000.0) return "cap alt_ft create";
            var ge = new Dictionary<string, object> { { "kind", "cap" }, { "group", "g" }, { "alt_ft", 9000.0 } };
            if (!Translate("group_edit", ge, out st, out err) || st.Count != 1 || st[0]["cmd"] as string != "cap_params") return "cap alt_ft edit";
            // T-326: group_edit name - the rename step is the last one
            var gn = new Dictionary<string, object> { { "kind", "cap" }, { "group", "g" }, { "name", "A" }, { "alt_ft", 9000.0 } };
            if (!Translate("group_edit", gn, out st, out err) || st.Count != 2 || st[0]["cmd"] as string != "cap_params" || st[1]["cmd"] as string != "cap_group" || st[1]["op"] as string != "rename" || st[1]["name"] as string != "A" || st[1]["group"] as string != "g") return "cap rename last";
            gn = new Dictionary<string, object> { { "kind", "asw" }, { "group", "g" }, { "name", "B" } };
            if (!Translate("group_edit", gn, out st, out err) || st.Count != 1 || st[0]["cmd"] as string != "asw_group" || st[0]["op"] as string != "rename" || st[0]["name"] as string != "B") return "asw rename";
            gn["kind"] = "sar";
            if (!Translate("group_edit", gn, out st, out err) || st.Count != 1 || st[0]["cmd"] as string != "asw_group" || st[0]["op"] as string != "rename") return "sar rename";
            // T-176: the v3 by-course fields travel into asw_search_course as they are; the old lead_nm / width_nm stay
            var gv = new Dictionary<string, object> { { "kind", "asw" }, { "group", "g" }, { "uids", new List<object> { 1.0 } }, { "target_uid", 5.0 }, { "tactics", new List<object> { "corridor", "barrier" } },
                { "corridor_x_nm", 30.0 }, { "barrier_x_nm", 50.0 }, { "standoff", new Dictionary<string, object> { { "dip", 40.0 } } }, { "depth", "auto" } };
            if (!Translate("group_create", gv, out st, out err) || st.Count != 2 || st[1]["cmd"] as string != "asw_search_course" || (double)st[1]["corridor_x_nm"] != 30.0 || (double)st[1]["barrier_x_nm"] != 50.0 || !(st[1]["standoff"] is Dictionary<string, object>)) return "v3 course fields";
            gv["lead_nm"] = 8.0;
            if (!Translate("group_create", gv, out st, out err) || (double)st[1]["lead_nm"] != 8.0) return "lead_nm synonym";
            // T-346 D1: every argument of every row has a type (obj of strike_probe was declared but not typed)
            foreach (var r in Rows)
                foreach (string an in r.Args)
                    if (!Types.ContainsKey(an)) return "argument " + an + " of " + r.Cmd + " has no type";
            var po = new Dictionary<string, object> { { "op", "ew" }, { "uid", 7.0 }, { "target_id", 9.0 }, { "on", true }, { "obj", true } };
            if (!Translate("strike_probe", po, out st, out err) || st.Count != 1 || !(st[0]["obj"] is bool) || !(bool)st[0]["obj"]) return "strike_probe obj " + err;
            // T-346 D4: an asw / sar group travels with its kind (the controller refuses the aircraft that cannot serve)
            var gk = new Dictionary<string, object> { { "kind", "asw" }, { "group", "g" }, { "uids", new List<object> { 1.0 } } };
            if (!Translate("group_create", gk, out st, out err) || st[0]["kind"] as string != "asw") return "asw create carries kind " + err;
            gk["kind"] = "sar";
            if (Translate("group_create", gk, out st, out err) && st[0]["kind"] as string != "sar") return "sar create carries kind";
            gk = new Dictionary<string, object> { { "kind", "asw" }, { "group", "g" }, { "add", new List<object> { 1.0 } } };
            if (!Translate("group_edit", gk, out st, out err) || st[0]["op"] as string != "add" || st[0]["kind"] as string != "asw") return "asw add carries kind " + err;
            var gc2 = new Dictionary<string, object> { { "kind", "cap" }, { "group", "g" }, { "uids", new List<object> { 1.0 } } };
            if (!Translate("group_create", gc2, out st, out err) || st[0].ContainsKey("kind")) return "cap create has no kind";
            if (!DevJson.Write(DevJson.Write(null)).Equals("\"null\"")) return "json";
            return null;
        }
    }
}
