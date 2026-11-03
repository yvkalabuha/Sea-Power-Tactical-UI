using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-328 (ADR-0127): game side of the ring sectors "+" (move / add to a group) and "-" (leave the group) of aircraft and helicopters.
    /// The ring reads the groups of the CAP and ASW windows and changes the membership only through the existing commands cap_group / asw_group
    /// (add / remove), so the logs, events and rules are those of the windows. The decisions live in RingGroups (pure code, offline tests).
    /// </summary>
    internal sealed partial class GameApi
    {
        private bool _ringGroupsErrLogged;

        /// <summary>Adapter of the CAP window: the list and the admission come from the controller, add / remove run cap_group.</summary>
        private sealed class CapWin : IRingGroupWindow
        {
            private readonly GameApi _a; private readonly CapController _c; private readonly string _source;
            public CapWin(GameApi a, CapController c, string source) { _a = a; _c = c; _source = source; }
            public string Window { get { return "cap"; } }
            public bool Ready { get { return _c.RingReady; } }
            public IList<RingGroupOption> Groups() { return _c.RingGroups(); }
            public string GroupOf(int uid) { return _c.RingGroupOf(uid); }
            public string AdmitWhy(int uid, string group, string ignoreWindow, string ignoreGroup) { return _c.AdmitWhy(uid, group, ignoreWindow, ignoreGroup); }
            public bool Add(int uid, string group) { return Run("add", uid, group); }
            public bool Remove(int uid, string group) { return Run("remove", uid, group); }
            private bool Run(string op, int uid, string group)
            {
                string r = _a.CapCmd("\"" + _source + "\"", "cap_group", GroupCmd(op, uid, group));
                return r != null && r.Contains("\"ok\":true");
            }
        }

        /// <summary>Adapter of the ASW window (search, Strike and SAR groups). EnsureLive() before reading: an idle controller skips the game scan.</summary>
        private sealed class AswWin : IRingGroupWindow
        {
            private readonly GameApi _a; private readonly AswController _c; private readonly string _source;
            public AswWin(GameApi a, AswController c, string source) { _a = a; _c = c; _source = source; }
            public string Window { get { return "asw"; } }
            public bool Ready { get { return _c.RingReady; } }
            public IList<RingGroupOption> Groups() { _c.EnsureLive(); return _c.RingGroups(); }
            public string GroupOf(int uid) { return _c.RingGroupOf(uid); }
            public string AdmitWhy(int uid, string group, string ignoreWindow, string ignoreGroup) { _c.EnsureLive(); return _c.AdmitWhy(uid, group, ignoreWindow, ignoreGroup); }
            public bool Add(int uid, string group) { return Run("add", uid, group); }
            public bool Remove(int uid, string group) { return Run("remove", uid, group); }
            private bool Run(string op, int uid, string group)
            {
                string r = _a.AswCmd("\"" + _source + "\"", "asw_group", GroupCmd(op, uid, group));
                return r != null && r.Contains("\"ok\":true");
            }
        }

        /// <summary>T-341: adapter of the Strike window (window "strike", kind "asuw"): the list and the admission come from the controller, add / remove run strike_group.</summary>
        private sealed class StrikeWin : IRingGroupWindow
        {
            private readonly GameApi _a; private readonly StrikeController _c; private readonly string _source;
            public StrikeWin(GameApi a, StrikeController c, string source) { _a = a; _c = c; _source = source; }
            public string Window { get { return StrikeController.RingWindow; } }
            public bool Ready { get { return _c.RingReady; } }
            public IList<RingGroupOption> Groups() { return _c.RingGroups(); }
            public string GroupOf(int uid) { return _c.RingGroupOf(uid); }
            public string AdmitWhy(int uid, string group, string ignoreWindow, string ignoreGroup) { return _c.AdmitWhy(uid, group, ignoreWindow, ignoreGroup); }
            public bool Add(int uid, string group) { return Run("add", uid, group); }
            public bool Remove(int uid, string group) { return Run("remove", uid, group); }
            private bool Run(string op, int uid, string group)
            {
                string r = _a.StrikeCmd("\"" + _source + "\"", "strike_group", GroupCmd(op, uid, group));
                return r != null && r.Contains("\"ok\":true");
            }
        }

        private static Dictionary<string, object> GroupCmd(string op, int uid, string group)
        {
            return new Dictionary<string, object> { { "op", op }, { "group", group }, { "uids", new List<object> { (double)uid } } };
        }

        /// <summary>The windows that exist now (a controller that is off is not a window), CAP first.</summary>
        private IList<IRingGroupWindow> RingWindows(string source)
        {
            var l = new List<IRingGroupWindow>();
            var cap = Cap; var asw = Asw; var strike = Strike;
            if (cap != null) l.Add(new CapWin(this, cap, source));
            if (asw != null) l.Add(new AswWin(this, asw, source));
            if (strike != null) l.Add(new StrikeWin(this, strike, source));   // T-341
            return l;
        }

        /// <summary>UnitView.Groups of an aircraft or helicopter. Any failure = no sectors and one log line.</summary>
        partial void RadialGroupsView(object unit, UnitView v)
        {
            v.Groups = null;
            if (unit == null || (v.Kind != "air" && v.Kind != "helo")) return;
            try
            {
                var wins = RingWindows("radial");
                if (wins.Count == 0) return;
                v.Groups = RingGroups.View(UidOf(unit), wins);
            }
            catch (Exception e)
            {
                v.Groups = null;
                if (!_ringGroupsErrLogged) { _ringGroupsErrLogged = true; Log("radial groups view failed (once): " + e.GetType().Name + ": " + e.Message); }
            }
        }

        private static string RingWhat(RingMoveResult r)
        {
            if (r.Ok) return "ok";
            if (r.RollbackFailed) return "rollback_failed";
            return (r.RolledBack ? "rolled_back(" : "refused(") + r.Error + ")";
        }

        private static string RingName(string w, string g) { return g == null ? "-" : w + ":" + g; }

        /// <summary>Runs the "+" (group_join, Value "win:name") or "-" (group_leave) node. Returns the RingMoveResult json (ok, error, from, to, rolled_back, rollback_failed).</summary>
        internal string RingGroupRun(MenuNode n, object unit, string source)
        {
            int uid = unit == null ? 0 : UidOf(unit);
            string win, grp;
            if (unit == null || !RingGroups.ParseValue(n.Value, out win, out grp)) return "{\"ok\":false,\"error\":\"unknown_group\"}";
            return RingGroupDo(uid, n.ActionId == "group_leave" ? "leave" : "join", win, grp, source);
        }

        private string RingGroupDo(int uid, string op, string win, string grp, string source)
        {
            var wins = RingWindows(source);
            RingMoveResult res = op == "leave" ? RingGroups.Leave(uid, wins, win, grp) : RingGroups.Join(uid, wins, win, grp);
            string line = source + " group_move uid=" + uid.ToString(CultureInfo.InvariantCulture) + " from=" + RingName(res.FromWindow, res.FromGroup) + " to=" + RingName(res.ToWindow, res.ToGroup) + " -> " + RingWhat(res);
            if (res.RollbackFailed) line += " (error " + res.Error + ")";
            Log(line);
            return res.Json();
        }

        private static string OptionJson(RingGroupOption o)
        {
            return "{\"value\":" + Json.Str(RingGroups.Value(o.Window, o.Name)) + ",\"name\":" + Json.Str(o.Name) + ",\"kind\":" + Json.Str(o.Kind) + ",\"members\":" + o.Members.ToString(CultureInfo.InvariantCulture)
                 + ",\"why\":" + (o.Why == null ? "null" : Json.Str(o.Why)) + "}";
        }

        /// <summary>Protocol command ring_group {op:"view"|"join"|"leave", uid, window?, group?}: the data behind the ring sectors, for live checks through ui_command.</summary>
        internal string RingGroupCmd(string id, Dictionary<string, object> cmd)
        {
            object o; double d = 0;
            if (cmd != null && cmd.TryGetValue("uid", out o) && o is double) d = (double)o;
            int uid = (int)d;
            string op = cmd != null && cmd.TryGetValue("op", out o) ? o as string : null;
            if (uid == 0 || (op != "view" && op != "join" && op != "leave")) return Err(id, "bad_args", "ring_group needs uid and op view|join|leave");
            string win = cmd.TryGetValue("window", out o) ? o as string : null, grp = cmd.TryGetValue("group", out o) ? o as string : null;
            try
            {
                if (op == "view")
                {
                    var v = RingGroups.View(uid, RingWindows("ui_command"));
                    if (v == null) return Ok(id, "\"sectors\":false,\"current\":null,\"admissible\":0,\"options\":[]");
                    var sb = new StringBuilder("\"sectors\":true,\"current\":" + (v.Current == null ? "null" : Json.Str(RingGroups.Value(v.Current.Window, v.Current.Name))) + ",\"admissible\":" + v.Admissible.ToString(CultureInfo.InvariantCulture) + ",\"options\":[");
                    for (int i = 0; i < v.Options.Count; i++) { if (i > 0) sb.Append(','); sb.Append(OptionJson(v.Options[i])); }
                    return Ok(id, sb.Append(']').ToString());
                }
                if (win == null || grp == null) return Err(id, "bad_args", "window and group are required");
                string res = RingGroupDo(uid, op, win, grp, "ui_command");
                return Ok(id, "\"result\":" + res);
            }
            catch (Exception e) { return Err(id, "failed", e.GetType().Name + ": " + e.Message); }
        }
    }
}
