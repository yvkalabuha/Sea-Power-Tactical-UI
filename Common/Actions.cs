using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    internal sealed class ActionStep
    {
        public string Cmd, When;
        public Dictionary<string, object> Args = new Dictionary<string, object>();
        public bool Optional, Unit = true;
    }

    internal sealed class ActionDef
    {
        public string Id, Kind, Flag, LabelRu, Label;
        /// <summary>"unit" (default): flag of the selected unit; "mod": a global mod setting (T-066 radar coverage overlay).</summary>
        public string Target = "unit";
        public string[] Applies = new string[0], Values = new string[0], ConfirmValues = new string[0];
        public Dictionary<string, string> ValueLabelsRu = new Dictionary<string, string>(), ValueLabels = new Dictionary<string, string>();
        public bool Confirm;
        /// <summary>combo only (T-091): a failed required step skips the remaining steps ("after_error").</summary>
        public bool StopOnError;
        /// <summary>false: not shown in the "XO" (Старпом) submenu (radar_coverage lives in the game's map layers menu, 0.10.3).</summary>
        public bool InMenu = true;
        /// <summary>Where the interface mod shows the action (T-107 K): "ring" | "menu" | "none"; null = as before (menu per `menu`, ring per the radial layout).
        /// The voice mod ignores the field: every registered action stays callable by voice.</summary>
        public string Place;
        /// <summary>Optional short sector label (<= 14 chars) and the voice phrase shown as a hint (T-068).</summary>
        public string ShortRu, Short, SayRu;
        /// <summary>Optional glyph key of the interface mod ring (T-140, ADR-0047); null = the id. The voice mod ignores it.</summary>
        public string Icon;
        /// <summary>Values / combo flag that need confirmation when the call comes from the menu or the ring (null = same as the bot's).</summary>
        public string[] MenuConfirmValues;
        public bool? MenuConfirm;
        public List<ActionStep> Steps = new List<ActionStep>();
    }

    /// <summary>One sector of the radial layout: a leaf action or a group of up to 5 actions (T-068).</summary>
    internal sealed class RadialSlot
    {
        public string ActionId;                  // leaf
        public string GroupId, LabelRu, Label;   // group
        public string[] Items = new string[0];   // group items (action ids)
    }

    /// <summary>
    /// Registry of mod actions (actions.json, embedded resource, the same file the bot tests read). One list drives both the
    /// "XO" (Старпом) context submenu and the protocol commands actions/action. Pure parsing, checked offline by SelfTest().
    /// </summary>
    internal static class ActionRegistry
    {
        /// <summary>Commands a combo step may call (everything except `action` itself: no recursion).</summary>
        internal static readonly HashSet<string> StepCommands = new HashSet<string>
        {
            "ping", "get_state", "set_course", "goto", "add_waypoint", "clear_waypoints", "waypoints", "attack", "attack_nearest_hostile",
            "contacts", "weapons", "clear_course", "set_telegraph", "pause", "unpause", "select", "set_emcon", "set_radar", "set_roe",
            "cease_fire", "launch_decoy", "evade_torpedo", "formations", "formation_create", "formation_join", "formation_leave",
            "formation_disband", "formation_leader", "formation_mode", "formation_pattern", "formation_recall", "flight_deck",
            "launch_aircraft", "set_altitude", "return_to_base", "recover_all", "allow_launch", "set_time_compression", "events",
            "load_mission", "dialog_ignore", "debrief_preview", "actions", "flags", "set_flags", "set_doctrine_defaults",
            "set_game_option", "course_from_threat", "restore_control", "smart_attack",
        };
        private static readonly string[] Flags = { "auto_evade", "veto_neutral", "post_launch" };
        private static readonly string[] ModFlags = { "radar_coverage", "overview" };

        /// <summary>Capability a "mod" toggle needs: radar_coverage -> radar_overlay, overview -> overview.</summary>
        public static string ModCap(string flag) { return flag == "overview" ? "overview" : "radar_overlay"; }
        private static readonly Regex IdRx = new Regex("^[a-z][a-z0-9_]*$");

        public static string LoadEmbedded()
        {
            using (var s = typeof(ActionRegistry).Assembly.GetManifestResourceStream("VoiceBotBridge.actions.json"))
            {
                if (s == null) return null;
                using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        private static string[] Strs(object o)
        {
            var l = o as List<object>;
            if (l == null) return new string[0];
            var res = new List<string>();
            foreach (var x in l) { var s = x as string; if (s != null) res.Add(s); }
            return res.ToArray();
        }

        private static Dictionary<string, string> StrMap(object o)
        {
            var res = new Dictionary<string, string>();
            var d = o as Dictionary<string, object>;
            if (d != null) foreach (var kv in d) { var s = kv.Value as string; if (s != null) res[kv.Key] = s; }
            return res;
        }

        /// <summary>Glyph key: [a-z][a-z0-9_]*, at most 32 chars.</summary>
        private static bool IsGlyphKey(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 32 || s[0] < 'a' || s[0] > 'z') return false;
            foreach (char c in s) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return false;
            return true;
        }

        private static string S(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) ? v as string : null;
        }

        public static List<ActionDef> Parse(string json, out string error)
        {
            error = null;
            try
            {
                var root = Json.Parse(json) as Dictionary<string, object>;
                if (root == null) { error = "root is not an object"; return null; }
                var arr = root.ContainsKey("actions") ? root["actions"] as List<object> : null;
                if (arr == null || arr.Count == 0) { error = "actions[] missing"; return null; }
                var res = new List<ActionDef>();
                var ids = new HashSet<string>();
                foreach (var item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    if (d == null) { error = "action is not an object"; return null; }
                    var a = new ActionDef { Id = S(d, "id"), Kind = S(d, "kind"), Flag = S(d, "flag"), LabelRu = S(d, "label_ru"), Label = S(d, "label") };
                    if (a.Id == null || !IdRx.IsMatch(a.Id)) { error = "bad id: " + a.Id; return null; }
                    if (!ids.Add(a.Id)) { error = "duplicate id: " + a.Id; return null; }
                    if (a.Kind != "combo" && a.Kind != "toggle" && a.Kind != "mode") { error = a.Id + ": unknown kind " + a.Kind; return null; }
                    if (string.IsNullOrEmpty(a.LabelRu) || string.IsNullOrEmpty(a.Label)) { error = a.Id + ": labels required"; return null; }
                    string tgt = S(d, "target");
                    if (tgt != null && tgt != "unit" && tgt != "mod") { error = a.Id + ": bad target " + tgt; return null; }
                    if (tgt != null) a.Target = tgt;
                    a.Applies = Strs(d.ContainsKey("applies") ? d["applies"] : null);
                    if (a.Applies.Length == 0) { error = a.Id + ": applies empty"; return null; }
                    foreach (var ap in a.Applies) if (Array.IndexOf(UnitKinds, ap) < 0) { error = a.Id + ": bad applies " + ap; return null; }
                    a.Confirm = d.ContainsKey("confirm") && d["confirm"] is bool && (bool)d["confirm"];
                    if (d.ContainsKey("stop_on_error"))
                    {
                        if (!(d["stop_on_error"] is bool)) { error = a.Id + ": stop_on_error must be a boolean"; return null; }
                        if (a.Kind != "combo") { error = a.Id + ": stop_on_error only for combo"; return null; }
                        a.StopOnError = (bool)d["stop_on_error"];
                    }
                    if (d.ContainsKey("menu") && d["menu"] is bool) a.InMenu = (bool)d["menu"];
                    var place = S(d, "place");
                    if (d.ContainsKey("place") && (place == null || Array.IndexOf(Places, place) < 0)) { error = a.Id + ": bad place " + place; return null; }
                    a.Place = place;
                    a.Values = Strs(d.ContainsKey("values") ? d["values"] : null);
                    a.ConfirmValues = Strs(d.ContainsKey("confirm_values") ? d["confirm_values"] : null);
                    a.ShortRu = S(d, "short_ru"); a.Short = S(d, "short"); a.SayRu = S(d, "say_ru");
                    if ((a.ShortRu != null && a.ShortRu.Length > 14) || (a.Short != null && a.Short.Length > 14)) { error = a.Id + ": short label over 14 chars"; return null; }
                    a.Icon = S(d, "icon");
                    if (d.ContainsKey("icon") && (a.Icon == null || !IsGlyphKey(a.Icon))) { error = a.Id + ": bad icon " + a.Icon; return null; }
                    if (d.ContainsKey("menu_confirm_values")) a.MenuConfirmValues = Strs(d["menu_confirm_values"]);
                    if (d.ContainsKey("menu_confirm") && d["menu_confirm"] is bool) a.MenuConfirm = (bool)d["menu_confirm"];
                    a.ValueLabelsRu = StrMap(d.ContainsKey("value_labels_ru") ? d["value_labels_ru"] : null);
                    a.ValueLabels = StrMap(d.ContainsKey("value_labels") ? d["value_labels"] : null);
                    foreach (var cv in a.ConfirmValues) if (Array.IndexOf(a.Values, cv) < 0) { error = a.Id + ": confirm_values outside values"; return null; }
                    if (a.MenuConfirmValues != null)
                        foreach (var cv in a.MenuConfirmValues) if (Array.IndexOf(a.Values, cv) < 0) { error = a.Id + ": menu_confirm_values outside values"; return null; }
                    if (a.Kind == "combo")
                    {
                        var steps = d.ContainsKey("steps") ? d["steps"] as List<object> : null;
                        if (steps == null || steps.Count == 0) { error = a.Id + ": combo needs steps"; return null; }
                        foreach (var so in steps)
                        {
                            var sd = so as Dictionary<string, object>;
                            if (sd == null) { error = a.Id + ": step is not an object"; return null; }
                            var st = new ActionStep { Cmd = S(sd, "cmd"), When = S(sd, "when") };
                            if (st.Cmd == null || !StepCommands.Contains(st.Cmd)) { error = a.Id + ": bad step cmd " + st.Cmd; return null; }
                            var args = sd.ContainsKey("args") ? sd["args"] as Dictionary<string, object> : null;
                            if (args != null) st.Args = args;
                            st.Optional = sd.ContainsKey("optional") && sd["optional"] is bool && (bool)sd["optional"];
                            if (sd.ContainsKey("unit") && sd["unit"] is bool) st.Unit = (bool)sd["unit"];
                            if (st.When != null && !st.When.StartsWith("cap:") && !st.When.StartsWith("!cap:")) { error = a.Id + ": bad when " + st.When; return null; }
                            a.Steps.Add(st);
                        }
                    }
                    else
                    {
                        if (a.Target == "mod")
                        {
                            if (a.Kind != "toggle") { error = a.Id + ": target mod needs kind toggle"; return null; }
                            if (Array.IndexOf(ModFlags, a.Flag) < 0) { error = a.Id + ": bad mod flag " + a.Flag; return null; }
                        }
                        else if (Array.IndexOf(Flags, a.Flag) < 0) { error = a.Id + ": bad flag " + a.Flag; return null; }
                        if (a.Kind == "mode" && a.Values.Length == 0) { error = a.Id + ": mode needs values"; return null; }
                    }
                    res.Add(a);
                }
                return res;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return null; }
        }

        /// <summary>Sides of the ring, index 0..7 clockwise from the top.</summary>
        public const string Sides = "N,NE,E,SE,S,SW,W,NW";

        /// <summary>Unit kinds an action may apply to: the voice mod only sees ship|air; sub|helo|group are for the interface mod (T-099).</summary>
        internal static readonly string[] UnitKinds = { "ship", "air", "sub", "helo", "group" };
        internal static readonly string[] Places = { "ring", "menu", "none" };

        private static ActionDef ById(List<ActionDef> defs, string id)
        {
            foreach (var d in defs) if (d.Id == id) return d;
            return null;
        }

        /// <summary>Parses the top-level "radial" key: kind (ship|air) -> 8 slots (null = empty sector). null + error on a bad layout.</summary>
        public static Dictionary<string, RadialSlot[]> ParseRadial(string json, List<ActionDef> defs, out string error)
        {
            error = null;
            try
            {
                var root = Json.Parse(json) as Dictionary<string, object>;
                var rad = root != null && root.ContainsKey("radial") ? root["radial"] as Dictionary<string, object> : null;
                if (rad == null) { error = "radial missing"; return null; }
                var sides = Sides.Split(',');
                var res = new Dictionary<string, RadialSlot[]>();
                foreach (var kv in rad)
                {
                    string kind = kv.Key;
                    if (Array.IndexOf(UnitKinds, kind) < 0) { error = "radial: unknown kind " + kind; return null; }
                    var lay = kv.Value as Dictionary<string, object>;
                    if (lay == null) { error = "radial." + kind + " is not an object"; return null; }
                    var slots = new RadialSlot[8];
                    foreach (var sv in lay)
                    {
                        int si = Array.IndexOf(sides, sv.Key);
                        if (si < 0) { error = "radial." + kind + ": unknown side " + sv.Key; return null; }
                        var slot = new RadialSlot();
                        var leaf = sv.Value as string;
                        var grp = sv.Value as Dictionary<string, object>;
                        var ids = new List<string>();
                        if (leaf != null) { slot.ActionId = leaf; ids.Add(leaf); }
                        else if (grp != null)
                        {
                            slot.GroupId = S(grp, "group"); slot.LabelRu = S(grp, "label_ru"); slot.Label = S(grp, "label");
                            if (string.IsNullOrEmpty(slot.GroupId) || string.IsNullOrEmpty(slot.LabelRu) || string.IsNullOrEmpty(slot.Label)) { error = "radial." + kind + "." + sv.Key + ": group needs group,label_ru,label"; return null; }
                            slot.Items = Strs(grp.ContainsKey("items") ? grp["items"] : null);
                            if (slot.Items.Length == 0) { error = "radial." + kind + "." + sv.Key + ": empty group"; return null; }
                            if (slot.Items.Length > 5) { error = "radial." + kind + "." + sv.Key + ": more than 5 items"; return null; }
                            ids.AddRange(slot.Items);
                            if (grp.ContainsKey("items"))
                                foreach (var it in (List<object>)grp["items"]) if (!(it is string)) { error = "radial." + kind + "." + sv.Key + ": nested group"; return null; }
                        }
                        else { error = "radial." + kind + "." + sv.Key + ": bad slot"; return null; }
                        foreach (var id in ids)
                        {
                            var def = ById(defs, id);
                            if (def == null) { error = "radial." + kind + ": unknown action " + id; return null; }
                            if (Array.IndexOf(def.Applies, kind) < 0) { error = "radial." + kind + ": " + id + " does not apply to " + kind; return null; }
                        }
                        slots[si] = slot;
                    }
                    res[kind] = slots;
                }
                return res;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return null; }
        }

        /// <summary>The value (mode) or the whole action (combo/toggle) makes the unit attack: AllowAttack=false forbids it everywhere.</summary>
        public static bool IsAttackValue(ActionDef def, string value)
        {
            if (def.Kind == "mode") return value != null && Array.IndexOf(def.ConfirmValues, value) >= 0;
            return def.Confirm;
        }

        /// <summary>Does this call need "confirm":true? source: bot | menu | radial. The menu and the ring use the menu_* variants.</summary>
        public static bool NeedsConfirm(ActionDef def, string value, string source)
        {
            bool fromMenu = source == "menu" || source == "radial";
            if (def.Kind == "mode")
            {
                var list = fromMenu && def.MenuConfirmValues != null ? def.MenuConfirmValues : def.ConfirmValues;
                return value != null && Array.IndexOf(list, value) >= 0;
            }
            return fromMenu && def.MenuConfirm.HasValue ? def.MenuConfirm.Value : def.Confirm;
        }

        private static string _fail;
        private static void Eq(string name, bool ok) { if (!ok && _fail == null) _fail = name; }

        private static string Wrap(string action) { return "{\"actions\":[" + action + "]}"; }
        private const string Combo = "{\"id\":\"a\",\"kind\":\"combo\",\"applies\":[\"ship\"],\"label_ru\":\"А\",\"label\":\"A\",\"steps\":[{\"cmd\":\"cease_fire\"}]}";

        /// <summary>Table checks of the validator plus the embedded registry itself. null = ok.</summary>
        public static string SelfTest()
        {
            _fail = null; string err;
            Eq("valid combo", Parse(Wrap(Combo), out err) != null && err == null);
            Eq("duplicate id", Parse("{\"actions\":[" + Combo + "," + Combo + "]}", out err) == null && err.StartsWith("duplicate"));
            Eq("unknown kind", Parse(Wrap(Combo.Replace("combo", "macro")), out err) == null);
            Eq("recursive action step", Parse(Wrap(Combo.Replace("cease_fire", "action")), out err) == null);
            Eq("unknown step cmd", Parse(Wrap(Combo.Replace("cease_fire", "fly_to_moon")), out err) == null);
            Eq("bad id", Parse(Wrap(Combo.Replace("\"a\"", "\"A-1\"")), out err) == null);
            Eq("bad applies", Parse(Wrap(Combo.Replace("ship", "tank")), out err) == null);
            Eq("applies sub|helo|group", Parse(Wrap(Combo.Replace("\"ship\"", "\"sub\",\"helo\",\"group\"")), out err) != null);
            var placed = Parse(Wrap(Combo.Replace("\"kind\":\"combo\"", "\"kind\":\"combo\",\"place\":\"none\"")), out err);
            Eq("place none parses", placed != null && placed[0].Place == "none");
            Eq("place default is null", Parse(Wrap(Combo), out err)[0].Place == null);
            Eq("icon default is null", Parse(Wrap(Combo), out err)[0].Icon == null);
            var iconed = Parse(Wrap(Combo.Replace("\"kind\":\"combo\"", "\"kind\":\"combo\",\"icon\":\"decoy_salvo\"")), out err);
            Eq("icon parses", iconed != null && iconed[0].Icon == "decoy_salvo");
            Eq("bad icon", Parse(Wrap(Combo.Replace("\"kind\":\"combo\"", "\"kind\":\"combo\",\"icon\":\"Bad Key\"")), out err) == null);
            Eq("bad place", Parse(Wrap(Combo.Replace("\"kind\":\"combo\"", "\"kind\":\"combo\",\"place\":\"sky\"")), out err) == null);
            Eq("mod toggle overview", Parse(Wrap("{\"id\":\"t\",\"kind\":\"toggle\",\"target\":\"mod\",\"flag\":\"overview\",\"applies\":[\"ship\"],\"label_ru\":\"Т\",\"label\":\"T\"}"), out err) != null);
            Eq("mod toggle bad flag", Parse(Wrap("{\"id\":\"t\",\"kind\":\"toggle\",\"target\":\"mod\",\"flag\":\"nope\",\"applies\":[\"ship\"],\"label_ru\":\"Т\",\"label\":\"T\"}"), out err) == null);
            Eq("mod toggle", Parse(Wrap("{\"id\":\"t\",\"kind\":\"toggle\",\"target\":\"mod\",\"flag\":\"radar_coverage\",\"applies\":[\"ship\"],\"label_ru\":\"Т\",\"label\":\"T\"}"), out err) != null);
            Eq("mod toggle with a unit flag", Parse(Wrap("{\"id\":\"t\",\"kind\":\"toggle\",\"target\":\"mod\",\"flag\":\"auto_evade\",\"applies\":[\"ship\"],\"label_ru\":\"Т\",\"label\":\"T\"}"), out err) == null);
            Eq("unit toggle with a mod flag", Parse(Wrap("{\"id\":\"t\",\"kind\":\"toggle\",\"flag\":\"radar_coverage\",\"applies\":[\"ship\"],\"label_ru\":\"Т\",\"label\":\"T\"}"), out err) == null);
            Eq("mod target must be a toggle", Parse(Wrap("{\"id\":\"t\",\"kind\":\"mode\",\"target\":\"mod\",\"flag\":\"radar_coverage\",\"values\":[\"a\"],\"applies\":[\"ship\"],\"label_ru\":\"Т\",\"label\":\"T\"}"), out err) == null);
            Eq("bad target", Parse(Wrap("{\"id\":\"t\",\"kind\":\"toggle\",\"target\":\"world\",\"flag\":\"auto_evade\",\"applies\":[\"ship\"],\"label_ru\":\"Т\",\"label\":\"T\"}"), out err) == null);
            Eq("combo without steps", Parse(Wrap(Combo.Replace("\"steps\":[{\"cmd\":\"cease_fire\"}]", "\"steps\":[]")), out err) == null);
            Eq("toggle bad flag", Parse(Wrap("{\"id\":\"t\",\"kind\":\"toggle\",\"flag\":\"x\",\"applies\":[\"ship\"],\"label_ru\":\"Т\",\"label\":\"T\"}"), out err) == null);
            Eq("confirm_values outside values", Parse(Wrap("{\"id\":\"m\",\"kind\":\"mode\",\"flag\":\"post_launch\",\"applies\":[\"air\"],\"values\":[\"off\"],\"confirm_values\":[\"x\"],\"label_ru\":\"М\",\"label\":\"M\"}"), out err) == null);
            Eq("bad when", Parse(Wrap(Combo.Replace("{\"cmd\":\"cease_fire\"}", "{\"cmd\":\"cease_fire\",\"when\":\"maybe\"}")), out err) == null);
            Eq("not json", Parse("{oops", out err) == null);
            Eq("stop_on_error true", Parse(Wrap(Combo.Replace("\"steps\"", "\"stop_on_error\":true,\"steps\"")), out err) != null);
            Eq("stop_on_error not a bool", Parse(Wrap(Combo.Replace("\"steps\"", "\"stop_on_error\":\"yes\",\"steps\"")), out err) == null);
            Eq("stop_on_error on a toggle", Parse(Wrap("{\"id\":\"t\",\"kind\":\"toggle\",\"stop_on_error\":true,\"flag\":\"auto_evade\",\"applies\":[\"ship\"],\"label_ru\":\"Т\",\"label\":\"T\"}"), out err) == null);
            const string PL = "{\"id\":\"m\",\"kind\":\"mode\",\"flag\":\"post_launch\",\"applies\":[\"air\"],\"values\":[\"off\",\"retarget\"],\"confirm_values\":[\"retarget\"],\"label_ru\":\"М\",\"label\":\"M\"}";
            var plDefs = Parse(Wrap(PL), out err);
            Eq("mode parses", plDefs != null);
            if (plDefs != null)
            {
                var pl = plDefs[0];
                Eq("menu_confirm_values default = confirm_values", NeedsConfirm(pl, "retarget", "menu") && NeedsConfirm(pl, "retarget", "radial") && NeedsConfirm(pl, "retarget", "bot"));
                Eq("IsAttackValue", IsAttackValue(pl, "retarget") && !IsAttackValue(pl, "off"));
                var plm = Parse(Wrap(PL.Replace("\"label_ru\"", "\"menu_confirm_values\":[],\"label_ru\"")), out err);
                Eq("menu_confirm_values empty parses", plm != null);
                if (plm != null)
                {
                    Eq("retarget: bot confirms, menu and ring do not", NeedsConfirm(plm[0], "retarget", "bot") && !NeedsConfirm(plm[0], "retarget", "menu") && !NeedsConfirm(plm[0], "retarget", "radial"));
                    Eq("retarget stays an attack value", IsAttackValue(plm[0], "retarget"));
                }
            }
            Eq("menu_confirm_values outside values", Parse(Wrap(PL.Replace("\"label_ru\"", "\"menu_confirm_values\":[\"x\"],\"label_ru\"")), out err) == null);
            Eq("short label over 14", Parse(Wrap(Combo.Replace("\"label_ru\"", "\"short\":\"0123456789abcde\",\"label_ru\"")), out err) == null);
            var cd = Parse(Wrap(Combo), out err);
            Eq("combo confirm rule", cd != null && !NeedsConfirm(cd[0], null, "bot") && !IsAttackValue(cd[0], null));
            if (cd != null)
            {
                string rj = "{\"actions\":[" + Combo + "],\"radial\":";
                Eq("radial valid", ParseRadial(rj + "{\"ship\":{\"N\":\"a\"}}}", cd, out err) != null);
                Eq("radial slots", ParseRadial(rj + "{\"ship\":{\"N\":\"a\"}}}", cd, out err)["ship"].Length == 8);
                Eq("radial unknown side", ParseRadial(rj + "{\"ship\":{\"X\":\"a\"}}}", cd, out err) == null);
                Eq("radial unknown id", ParseRadial(rj + "{\"ship\":{\"N\":\"zz\"}}}", cd, out err) == null);
                Eq("radial wrong kind", ParseRadial(rj + "{\"air\":{\"N\":\"a\"}}}", cd, out err) == null);
                Eq("radial unknown unit kind", ParseRadial(rj + "{\"tank\":{\"N\":\"a\"}}}", cd, out err) == null);
                Eq("radial group over 5", ParseRadial(rj + "{\"ship\":{\"NW\":{\"group\":\"g\",\"label_ru\":\"Г\",\"label\":\"G\",\"items\":[\"a\",\"a\",\"a\",\"a\",\"a\",\"a\"]}}}}", cd, out err) == null);
                Eq("radial nested group", ParseRadial(rj + "{\"ship\":{\"NW\":{\"group\":\"g\",\"label_ru\":\"Г\",\"label\":\"G\",\"items\":[{\"group\":\"h\"}]}}}}", cd, out err) == null);
            }
            var text = LoadEmbedded();
            Eq("embedded present", text != null);
            if (text != null)
            {
                var reg = Parse(text, out err);
                Eq("embedded valid: " + err, reg != null);
                if (reg != null)
                {
                    Eq("embedded has 13 actions", reg.Count == 13);
                    var lay = ParseRadial(text, reg, out err);
                    Eq("embedded radial valid: " + err, lay != null && lay.ContainsKey("ship") && lay.ContainsKey("air") && lay.ContainsKey("sub") && lay.ContainsKey("helo"));
                    var plr = ById(reg, "post_launch");
                    var bc = ById(reg, "break_contact");
                    Eq("break_contact stops on error", bc != null && bc.StopOnError);
                    var rc = ById(reg, "release_control");
                    Eq("release_control is a combo for every unit kind", rc != null && rc.Kind == "combo" && rc.Applies.Length == 4 && rc.Steps.Count == 1 && rc.Steps[0].Cmd == "restore_control");
                    Eq("ring W = release_control", lay != null && lay["ship"][6] != null && lay["ship"][6].ActionId == "release_control" && lay["air"][6] != null && lay["air"][6].ActionId == "release_control");
                    Eq("post_launch: bot confirms retarget, menu does not", plr != null && NeedsConfirm(plr, "retarget", "bot") && !NeedsConfirm(plr, "retarget", "menu") && IsAttackValue(plr, "retarget"));
                }
            }
            var cr = ComboRunnerSelfTest();
            Eq("ComboRunner: " + cr, cr == null);
            var ackFields = RestoreFieldsJson(Json.Parse("{\"roe\":{\"action\":\"restore\",\"from\":\"x\"},\"route\":{\"action\":\"nothing_saved\"},\"speed\":\"restore\",\"emcon\":\"player_changed\",\"radars\":\"nothing_saved\",\"summary_ru\":\"s\"}") as Dictionary<string, object>);
            Eq("combo ack restore fields: " + ackFields, ackFields == ",\"roe\":{\"action\":\"restore\"},\"route\":{\"action\":\"nothing_saved\"},\"speed\":\"restore\",\"emcon\":\"player_changed\",\"radars\":\"nothing_saved\"" && RestoreFieldsJson(new Dictionary<string, object>()) == "");
            var ackNoRu = RestoreFieldsOfAck(Json.Parse("{\"roe\":{\"action\":\"restore\",\"from\":\"x\"},\"route\":{\"action\":\"nothing_saved\"}}") as Dictionary<string, object>);
            Eq("combo ack restore fields without summary_ru (interface mod): " + ackNoRu, ackNoRu == ",\"roe\":{\"action\":\"restore\"},\"route\":{\"action\":\"nothing_saved\"}");
            var sn = GameApi.SnapSelfTest();
            Eq("SnapUnit: " + sn, sn == null);
            return _fail;
        }

        /// <summary>Ack fields of a restore_control ack: taken when the structured decisions ("roe") are present, with or without the voice-only summary_ru; null otherwise.</summary>
        internal static string RestoreFieldsOfAck(Dictionary<string, object> data)
        {
            return data != null && data.ContainsKey("roe") ? RestoreFieldsJson(data) : null;
        }

        /// <summary>The release_control decisions as ack fields: "roe":{"action":..},"route":{"action":..},"speed","emcon","radars" (only those present).</summary>
        internal static string RestoreFieldsJson(Dictionary<string, object> data)
        {
            var sb = new StringBuilder();
            foreach (var k in new[] { "roe", "route", "speed", "emcon", "radars" })
            {
                object o;
                if (!data.TryGetValue(k, out o)) continue;
                var d = o as Dictionary<string, object>;
                string a = d != null ? (d.ContainsKey("action") ? d["action"] as string : null) : o as string;
                if (a == null) continue;
                sb.Append(",\"" + k + "\":" + (d != null ? "{\"action\":" + Json.Str(a) + "}" : Json.Str(a)));
            }
            return sb.ToString();
        }

        private static string Ack(bool ok, string code) { return ok ? "{\"ok\":true}" : "{\"ok\":false,\"error\":\"" + code + "\"}"; }

        /// <summary>ComboRunner on the embedded registry with fake replies (T-091). null = ok.</summary>
        private static string ComboRunnerSelfTest()
        {
            var text = LoadEmbedded(); string err;
            var reg = text == null ? null : Parse(text, out err);
            if (reg == null) return "registry";
            var none = new HashSet<string>();
            var caps = new HashSet<string> { "auto_evade" };
            ActionDef decoy = ById(reg, "decoy_salvo"), torp = ById(reg, "torpedo_alert"), brk = ById(reg, "break_contact");
            if (decoy == null || torp == null || brk == null) return "missing actions";

            var o = ComboRunner.Run(decoy, none.Contains, (st, i) => Ack(false, i == 0 ? "no_decoy" : "not_ready"));
            if (ActionJournal.Result(o) != ActionJournal.Noop || !ActionJournal.AckOk(o) || ActionJournal.NotDone(o).Count != 2) return "decoy_salvo noop";

            int calls = 0;
            o = ComboRunner.Run(torp, caps.Contains, (st, i) => { calls++; return Ack(st.Cmd != "launch_decoy", "no_decoy"); });
            if (calls != 3 || o.Count != 4 || o[1].Status != StepStatus.Skipped || !o[1].ByWhen) return "torpedo_alert skips the !cap step (calls=" + calls + ")";
            if (ActionJournal.Result(o) != ActionJournal.Partial || !ActionJournal.AckOk(o)) return "torpedo_alert partial";

            calls = 0; var seen = new List<string>();
            o = ComboRunner.Run(brk, none.Contains, (st, i) => { calls++; return Ack(false, "no_hostile_contact"); },
                st => seen.Add("b:" + st.Cmd), (st, r) => seen.Add("a:" + st.Cmd));
            if (calls != 1) return "break_contact stops after the first error (calls=" + calls + ")";
            if (o.Count != 3 || o[1].Status != StepStatus.Skipped || o[1].Reason != "after_error" || o[2].Reason != "after_error") return "after_error rows";
            if (ActionJournal.AckOk(o) || ActionJournal.FirstReason(o) != "no_hostile_contact" || ActionJournal.Result(o) != ActionJournal.Noop) return "break_contact error";
            if (seen.Count != 2 || seen[0] != "b:course_from_threat" || seen[1] != "a:course_from_threat") return "hooks around exec";

            // without stop_on_error the later steps still run
            calls = 0;
            var air = ById(reg, "air_defense_up");
            o = ComboRunner.Run(air, none.Contains, (st, i) => { calls++; return Ack(i != 1, "no_sensor"); });
            if (calls != 3 || ActionJournal.AckOk(o)) return "air_defense_up keeps going (calls=" + calls + ")";
            return null;
        }
    }

    internal sealed partial class GameApi
    {
        public List<ActionDef> Actions;
        public string ActionsError;
        /// <summary>Radial layout (T-068): ship|air -> 8 slots. Null when the layout is missing or invalid (the ring is then off).</summary>
        public Dictionary<string, RadialSlot[]> RadialLayout;
        public string RadialError;

        private void EnsureActions()
        {
            if (Actions != null || ActionsError != null) return;
            try
            {
                string err;
                var text = ActionRegistry.LoadEmbedded();
                Actions = text == null ? null : ActionRegistry.Parse(text, out err);
                if (Actions == null) { Actions = new List<ActionDef>(); ActionsError = text == null ? "resource missing" : "invalid registry"; }
                else
                {
                    string rerr;
                    RadialLayout = ActionRegistry.ParseRadial(text, Actions, out rerr);
                    RadialError = RadialLayout == null ? rerr : null;
                }
            }
            catch (Exception e) { Actions = new List<ActionDef>(); ActionsError = e.Message; }
        }

        private ActionDef FindAction(string name)
        {
            EnsureActions();
            foreach (var a in Actions) if (a.Id == name) return a;
            return null;
        }

        /// <summary>"ship" for Vessel/Submarine, "air" for Aircraft/Helicopter, else null (by game type name).</summary>
        internal static string UnitKindOf(string type)
        {
            switch (type)
            {
                case "Vessel": case "Submarine": return "ship";
                case "Aircraft": case "Helicopter": return "air";
                default: return null;
            }
        }

        internal string UnitKindOfObj(object u)
        {
            for (var t = u == null ? null : u.GetType(); t != null; t = t.BaseType)
            {
                var k = UnitKindOf(t.Name);
                if (k != null) return k;
            }
            return null;
        }

        private static bool AppliesTo(ActionDef a, string kind) { return kind != null && Array.IndexOf(a.Applies, kind) >= 0; }

        /// <summary>"sub" | "helo" by the type chain (T-099); null for anything else. The voice mod only knows ship|air, the interface mod uses all four.</summary>
        private static string FineKind(object u)
        {
            for (var t = u == null ? null : u.GetType(); t != null; t = t.BaseType)
            {
                if (t.Name == "Submarine") return "sub";
                if (t.Name == "Helicopter") return "helo";
            }
            return null;
        }

        /// <summary>Legacy kind (ship|air) or the fine one (sub|helo) is listed in `applies`.</summary>
        private bool AppliesToUnit(ActionDef a, object u) { return AppliesTo(a, UnitKindOfObj(u)) || AppliesTo(a, FineKind(u)); }

        private static string ErrData(string id, string code, string detail, string dataJson)
        {
            return "{\"type\":\"ack\",\"id\":" + id + ",\"ok\":false,\"error\":" + Json.Str(code) +
                   (detail == null ? "" : ",\"detail\":" + Json.Str(detail)) + (dataJson == null ? "" : ",\"data\":{" + dataJson + "}") + "}";
        }

        private string ActionsCmd(string id, Dictionary<string, object> cmd)
        {
            EnsureActions();
            if (Actions.Count == 0) return Err(id, "unsupported", ActionsError ?? "registry empty");
            object unit = null; string kind = null;
            if (InMission())
            {
                string err;
                unit = cmd.ContainsKey("uid") && cmd["uid"] != null ? Target(cmd, out err) : SelectedUnitOrNull();
                kind = UnitKindOfObj(unit);
            }
            var sb = new StringBuilder("\"registry_version\":1,\"actions\":[");
            for (int i = 0; i < Actions.Count; i++)
            {
                var a = Actions[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(Json.Str(a.Id)).Append(",\"kind\":").Append(Json.Str(a.Kind));
                sb.Append(",\"applies\":[").Append(string.Join(",", Array.ConvertAll(a.Applies, Json.Str))).Append(']');
                sb.Append(",\"label_ru\":").Append(Json.Str(a.LabelRu)).Append(",\"label\":").Append(Json.Str(a.Label));
                sb.Append(",\"confirm\":").Append(Json.Bool(a.Confirm));
                if (a.Kind == "mode")
                {
                    sb.Append(",\"values\":[").Append(string.Join(",", Array.ConvertAll(a.Values, Json.Str))).Append(']');
                    sb.Append(",\"confirm_values\":[").Append(string.Join(",", Array.ConvertAll(a.ConfirmValues, Json.Str))).Append(']');
                    sb.Append(",\"menu_confirm_values\":[").Append(string.Join(",", Array.ConvertAll(a.MenuConfirmValues ?? a.ConfirmValues, Json.Str))).Append(']');
                }
                if (a.Target == "mod")
                {
                    sb.Append(",\"target\":\"mod\",\"available\":").Append(Json.Bool(Capabilities.Contains(ActionRegistry.ModCap(a.Flag))));
                    sb.Append(",\"value\":").Append(Json.Bool(ModFlagOn(a.Flag)));
                }
                else if (unit != null)
                {
                    bool avail = (AppliesTo(a, kind) || AppliesTo(a, FineKind(unit))) && (a.Kind == "combo" || Capabilities.Contains("unit_flags"));
                    sb.Append(",\"available\":").Append(Json.Bool(avail));
                    if (avail && a.Kind != "combo")
                    {
                        if (a.Kind == "toggle") sb.Append(",\"value\":").Append(Json.Bool(ResolveFlag(unit, a.Flag)));
                        else sb.Append(",\"value\":").Append(Json.Str(ResolvePostLaunch(unit)));
                    }
                }
                sb.Append('}');
            }
            return Ok(id, sb.Append(']').ToString());
        }

        private string ActionCmd(string id, Dictionary<string, object> cmd)
        {
            string name = cmd.ContainsKey("name") ? cmd["name"] as string : null;
            var def = FindAction(name);
            if (def == null) return Err(id, "unknown_action", name);
            return RunAction(id, def, cmd, "bot");
        }

        // commands that understand `scope` (wrapped by Scoped)
        /// <summary>Error code of a failed ack ("error":"code"), or "error" when none.</summary>
        private static string ReplyErrorCode(string reply)
        {
            if (reply == null) return "error";
            var m = System.Text.RegularExpressions.Regex.Match(reply, "\"error\":\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "error";
        }

        private static readonly HashSet<string> ScopedStepCmds = new HashSet<string> { "set_emcon", "set_radar", "set_roe", "cease_fire", "set_telegraph", "set_course", "set_flags" };

        /// <summary>Runs an action for the menu (source "menu") and for the protocol (source "bot"). Main thread only.</summary>
        internal string RunAction(string id, ActionDef def, Dictionary<string, object> cmd, string source)
        {
            if (def.Kind == "toggle")
            {
                if (!cmd.ContainsKey("on") || !(cmd["on"] is bool)) return Err(id, "bad_args", "on (true/false) required");
            }
            else if (def.Kind == "mode")
            {
                string v = cmd.ContainsKey("value") ? cmd["value"] as string : null;
                if (v == null || Array.IndexOf(def.Values, v) < 0) return Err(id, "bad_args", "value: " + string.Join("|", def.Values));
                if (ActionRegistry.IsAttackValue(def, v) && !AllowAttack) return Err(id, "attack_disabled", "AllowAttack=false in mod config");
                if (ActionRegistry.NeedsConfirm(def, v, source) && !Confirmed(cmd)) return Err(id, "confirm_required", "value \"" + v + "\" needs \"confirm\":true");
            }
            else
            {
                if (ActionRegistry.IsAttackValue(def, null) && !AllowAttack) return Err(id, "attack_disabled", "AllowAttack=false in mod config");
                if (ActionRegistry.NeedsConfirm(def, null, source) && !Confirmed(cmd)) return Err(id, "confirm_required", def.Id + " needs \"confirm\":true");
            }

            if (def.Target == "mod")
            {
                // global mod setting, no unit needed (T-066)
                Need(ActionRegistry.ModCap(def.Flag));
                bool prev = ModFlagOn(def.Flag);
                SetModFlag(def.Flag, (bool)cmd["on"], source);
                if (EventsAvailable)
                    EvLine("mod_action", "{\"name\":" + Json.Str(def.Id) + ",\"uid\":null,\"source\":" + Json.Str(source) + ",\"ok\":true}");
                return Ok(id, "\"name\":" + Json.Str(def.Id) + ",\"on\":" + Json.Bool(ModFlagOn(def.Flag)) + ",\"previous\":" + Json.Bool(prev));
            }
            Need("actions");
            string err; var u = Target(cmd, out err);
            if (u == null) return Err(id, err, null);
            if (!AppliesToUnit(def, u)) return Err(id, "not_applicable", def.Id + " applies to " + string.Join("|", def.Applies));
            double uid = UidOf(u);

            string reply; bool ok; string result = null;
            LastActionSummary = null;
            if (def.Kind == "combo") reply = RunCombo(id, def, cmd, u, uid, source, out ok, out result);
            else
            {
                string before = null;
                if (ActionDebug)
                {
                    try { before = def.Kind == "toggle" ? (ResolveFlag(u, def.Flag) ? "on" : "off") : ResolvePostLaunch(u); } catch { }
                }
                var c = new Dictionary<string, object> { { "uid", uid } };
                if (cmd.ContainsKey("scope")) c["scope"] = cmd["scope"];
                if (cmd.ContainsKey("formation")) c["formation"] = cmd["formation"];
                if (cmd.ContainsKey("confirm")) c["confirm"] = cmd["confirm"];
                c[def.Flag] = def.Kind == "toggle" ? cmd["on"] : cmd["value"];
                reply = Scoped(id, c, "set_flags", (cc, f) => SetFlagsCmd(id, cc));
                ok = reply.Contains("\"ok\":true");
                LastActionSummary = ActionJournal.Summary(source, def.Id, ok ? ActionJournal.Ok : "error", ok ? new List<string>() : new List<string> { ReplyErrorCode(reply) });
                if (ActionDebug)
                {
                    string after = null;
                    try { after = def.Kind == "toggle" ? (ResolveFlag(u, def.Flag) ? "on" : "off") : ResolvePostLaunch(u); } catch { }
                    Log("action " + def.Id + " uid=" + (int)uid + " src=" + source + " " + def.Flag + ": " + before + "\u2192" + after + " (" + FlagEffect(def.Flag) + ")" + (ok ? "" : " FAILED"));
                }
            }
            if (EventsAvailable)
                EvLine("mod_action", "{\"name\":" + Json.Str(def.Id) + ",\"uid\":" + (int)uid + ",\"source\":" + Json.Str(source) + ",\"ok\":" + Json.Bool(ok) + (result != null ? ",\"result\":" + Json.Str(result) : "") + "}");
            return reply;
        }

        private string RunCombo(string id, ActionDef def, Dictionary<string, object> cmd, object u, double uid, string source, out bool ok, out string result)
        {
            string scope = cmd.ContainsKey("scope") ? cmd["scope"] as string : null;
            var snapBefore = SnapUnit(u);
            string summaryRu = null;
            string restoreFields = ""; // T-103: structured release_control decisions (additive; summary_ru is added by the voice mod only)
            var outcomes = ComboRunner.Run(def, Capabilities.Contains, (st, i) =>
            {
                var c = new Dictionary<string, object>(st.Args);
                c["cmd"] = st.Cmd; c["id"] = id + "." + i;
                if (st.Unit) c["uid"] = uid;
                if (st.Unit && scope != null && ScopedStepCmds.Contains(st.Cmd))
                {
                    c["scope"] = scope;
                    if (cmd.ContainsKey("formation")) c["formation"] = cmd["formation"];
                }
                string r = Execute(c);
                try
                {
                    var rd = Json.Parse(r) as Dictionary<string, object>;
                    var data = rd != null && rd.ContainsKey("data") ? rd["data"] as Dictionary<string, object> : null;
                    if (data != null && data.ContainsKey("summary_ru") && data["summary_ru"] is string) summaryRu = (string)data["summary_ru"];
                    var rf = ActionRegistry.RestoreFieldsOfAck(data); // summary_ru is voice-only (ADR-0044), the structured decisions are shared
                    if (rf != null) restoreFields = rf;
                }
                catch { }
                return r;
            }, st => BeforeComboStep(u, st), (st, r) => AfterComboStep(u, st, r));
            var snapAfter = SnapUnit(u);

            ok = ActionJournal.AckOk(outcomes);
            result = ActionJournal.Result(outcomes);
            var notDone = ActionJournal.NotDone(outcomes);
            int okSteps = 0;
            foreach (var o in outcomes) if (o.Status == StepStatus.Ok) okSteps++;
            LastActionSummary = ActionJournal.Summary(source, def.Id, ok ? result : "error", ok ? notDone : new List<string> { ActionJournal.FirstReason(outcomes) });
            if (ActionDebug) DebugLog(ActionJournal.DebugLines(def.Id, uid, source, result, outcomes, snapBefore, snapAfter));

            var nd = new StringBuilder("[");
            for (int i = 0; i < notDone.Count; i++) { if (i > 0) nd.Append(','); nd.Append(Json.Str(notDone[i])); }
            nd.Append(']');
            string data2 = "\"name\":" + Json.Str(def.Id) + ",\"uid\":" + (int)uid + ",\"ok_steps\":" + okSteps + ",\"result\":" + Json.Str(result) +
                           ",\"not_done\":" + nd + (summaryRu != null ? ",\"summary_ru\":" + Json.Str(summaryRu) : "") + restoreFields + ",\"steps\":" + ActionJournal.StepsJson(outcomes);
            if (ok) return Ok(id, data2);
            string reason = ActionJournal.FirstReason(outcomes);
            string detail = null;
            foreach (var o in outcomes) if (o.Status == StepStatus.Error && !o.Optional) { detail = o.Cmd + (o.Detail != null ? ": " + o.Detail : ""); break; }
            return ErrData(id, reason ?? "error", detail, data2);
        }
    }
}
