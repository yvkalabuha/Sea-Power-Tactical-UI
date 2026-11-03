using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceBotBridge
{
    internal sealed partial class StrikeController
    {
        /// <summary>op: create | add | remove | disband | rename; group, uids, name. Answer: ack json or "!code[|detail]".</summary>
        public string Group(Dictionary<string, object> cmd)
        {
            string op = Str(cmd, "op"), group = Str(cmd, "group");
            if (op != "create" && op != "add" && op != "remove" && op != "disband" && op != "rename") return Fail("bad_args", "op");
            var uids = new List<int>();
            object u;
            if (cmd.TryGetValue("uids", out u))
            {
                var l = u as List<object>;
                if (l == null) return Fail("bad_args", "uids");
                foreach (object o in l)
                {
                    if (!(o is double) || !IsInt((double)o)) return Fail("bad_args", "uids");
                    uids.Add((int)(double)o);
                }
            }
            var keys = new List<string>();
            if (cmd.TryGetValue("deck_keys", out u))
            {
                var kl = u as List<object>;
                if (kl == null) return Fail("bad_args", "deck_keys");
                foreach (object o in kl)
                {
                    string k = o as string;
                    if (string.IsNullOrEmpty(k)) return Fail("bad_args", "deck_keys");
                    keys.Add(k);
                }
            }
            if (op != "create" && string.IsNullOrEmpty(group)) return Fail("bad_args", "group");
            if (op == "add" && uids.Count == 0 && keys.Count == 0) return Fail("bad_args", "uids");
            if (op == "remove" && uids.Count == 0 && keys.Count == 0) return Fail("bad_args", "uids");
            switch (op)
            {
                case "create": return Create(group, uids, keys);
                case "add": return Add(group, uids, keys);
                case "remove": return Remove(group, uids, keys);
                case "disband": return Disband(group);
                default: return Rename(group, Str(cmd, "name"));
            }
        }

        private string Create(string group, List<int> uids, List<string> keys)
        {
            string name;
            if (string.IsNullOrEmpty(group))
            {
                int n = _seq + 1;
                while (Find(AswGroupNames.Auto(GroupPrefix, n)) != null) n++;
                name = AswGroupNames.Auto(GroupPrefix, n);
            }
            else
            {
                string bad = AswGroupNames.Validate(group, t => Find(t) != null);
                if (bad == "name_taken") return Fail("group_exists");
                if (bad != null) return Fail("bad_args", bad);
                name = group.Trim();
            }
            var gd = new StrikeGroup { Name = name, Seq = ++_seq, Goal = DefaultGoal };
            _groups.Add(gd);
            string extra = null;
            if (uids.Count > 0 || keys.Count > 0)
            {
                string r = Add(name, uids, keys);
                if (IsErr(r)) { ReleaseGroupClaims(gd); _groups.Remove(gd); _seq--; return r; }
                extra = "\"added\":" + AddedOf(gd);
            }
            if (Ownership != null) Ownership.GroupSeq("strike", name);
            RecomputeWait(gd, "create");
            _log("[Strike] strike group create " + name + " seq=" + gd.Seq.ToString(CultureInfo.InvariantCulture) + " members=" + gd.Members.Count.ToString(CultureInfo.InvariantCulture));
            return Ack(name, "\"seq\":" + gd.Seq.ToString(CultureInfo.InvariantCulture) + (extra == null ? "" : "," + extra));
        }

        private static string AddedOf(StrikeGroup g) { return "[" + string.Join(",", g.Members.Select(x => x.ToString(CultureInfo.InvariantCulture))) + "]"; }

        private string Add(string group, List<int> uids, List<string> keys)
        {
            var g = Find(group);
            if (g == null) return Fail("no_group");
            var added = new List<string>();
            var rejected = new List<string>();
            string firstCode = null;
            foreach (int uid in uids)
            {
                string code = null, why = null;
                var a = Air(uid);
                if (a == null) code = "no_aircraft";
                else
                {
                    string role = StrikeAirMath.RoleOf(a);
                    if (role == "excluded") { code = "not_striker"; why = a.Ew != null && (a.Why == "ew_no_api" || a.Why == "ew_inoperable") ? a.Why : StrikeAirMath.AircraftWhy(a); }
                    else
                    {
                        var other = GroupOf(uid);
                        string owner = OwnerOf == null ? null : OwnerOf(uid);
                        if (other != null && other != g) { code = "in_group"; why = other.Name; }
                        else if (owner != null && other == null) { code = "in_group"; why = owner; }
                        else if (other == null && Ownership != null) { string cl = Ownership.Claim(uid, "strike", g.Name); if (cl != null) { code = "in_group"; why = cl.StartsWith("in_group:", StringComparison.Ordinal) ? cl.Substring(9) : cl; } }
                    }
                }
                if (code != null)
                {
                    if (firstCode == null) firstCode = code + (why == null ? "" : "|" + why);
                    rejected.Add("{\"uid\":" + uid.ToString(CultureInfo.InvariantCulture) + ",\"why\":" + Q(code + (why == null ? "" : ":" + why)) + "}");
                    _log("[Strike] strike group add rejected uid=" + uid.ToString(CultureInfo.InvariantCulture) + " why=" + code + (why == null ? "" : ":" + why));
                    continue;
                }
                if (!g.Members.Contains(uid)) g.Members.Add(uid);
                if (Ownership != null) Ownership.Claim(uid, "strike", g.Name);
                added.Add(uid.ToString(CultureInfo.InvariantCulture));
            }
            var addedKeys = new List<string>();
            foreach (string key in keys)
            {
                string code = null, why = null;
                var items = _g.DeckItems();
                StrikeDeckItem it = null;
                if (items != null) foreach (var x in items) if (x != null && x.Key == key) { it = x; break; }
                var holder = _groups.FirstOrDefault(x => x != g && x.DeckKeys.Contains(key));
                if (it == null) code = "no_aircraft";
                else if (it.Why != null) { code = "not_striker"; why = it.Why; }
                else if (holder != null) { code = "deck_busy"; why = holder.Name; }
                if (code != null)
                {
                    if (firstCode == null) firstCode = code + (why == null ? "" : "|" + why);
                    rejected.Add("{\"key\":" + Q(key) + ",\"why\":" + Q(code + (why == null ? "" : ":" + why)) + "}");
                    _log("[Strike] strike group add rejected key=" + key + " why=" + code + (why == null ? "" : ":" + why));
                    continue;
                }
                if (!g.DeckKeys.Contains(key)) g.DeckKeys.Add(key);
                _deckInfo[key] = it;
                addedKeys.Add(Q(key));
            }
            if (added.Count == 0 && addedKeys.Count == 0) return "!" + firstCode;
            if (g.Timeline != null) MarkStale(g, "aircraft_changed");
            if (EffectiveRoe == "hold") ApplyRoe(g);
            RecomputeWait(g, "add");
            return Ack(g.Name, "\"added\":[" + string.Join(",", added) + "],\"added_keys\":[" + string.Join(",", addedKeys) + "],\"rejected\":[" + string.Join(",", rejected) + "]");
        }

        private string Remove(string group, List<int> uids, List<string> keys)
        {
            var g = Find(group);
            if (g == null) return Fail("no_group");
            var removed = new List<string>();
            foreach (int uid in uids)
                if (g.Members.Remove(uid))
                {
                    removed.Add(uid.ToString(CultureInfo.InvariantCulture));
                    EwDrop(g, uid);
                    if (Ownership != null) Ownership.Release(uid);
                    _ms.Remove(uid);
                    RoeBack(uid);
                    RestoreUidSpeed(uid);
                    ClearDrop(g, uid);
                }
            foreach (string key in keys)
                if (g.DeckKeys.Remove(key))
                {
                    removed.Add(Q(key)); ForgetDeckKey(key);
                    var oo = g.DeckOrders.FirstOrDefault(x => x.Key == key);
                    if (oo != null && oo.Live) oo.State = "cancelled";
                }
            if (removed.Count > 0 && g.Timeline != null) MarkStale(g, "aircraft_changed");
            if (removed.Count > 0) RecomputeWait(g, "remove");
            return Ack(g.Name, "\"removed\":[" + string.Join(",", removed) + "]");
        }

        private string Disband(string group)
        {
            var g = Find(group);
            if (g == null) return Fail("no_group");
            DeckAbortAll(g, "disband");
            ClearEnd(g, "disband"); DropQueued(g);
            ReleaseGroupClaims(g);
            _groups.Remove(g);
            _log("[Strike] strike group disband " + g.Name);
            return Ack(g.Name, null);
        }

        private string Rename(string group, string name)
        {
            var g = Find(group);
            if (g == null) return Fail("no_group");
            if (name == null) return Fail("bad_args", "name");
            name = name.Trim();
            if (name.Length == 0) name = AswGroupNames.Auto(GroupPrefix, g.Seq);   // "" = the automatic name
            string bad = AswGroupNames.Validate(name, t => t != g.Name && Find(t) != null);
            if (bad == "name_taken") return Fail("group_exists");
            if (bad != null) return Fail("bad_args", bad);
            string old = g.Name;
            g.Name = name;
            if (Ownership != null) { Ownership.RenameClaims("strike", old, name); Ownership.RenameSeq("strike", old, name); }
            return Ack(name, null);
        }

        /// <summary>T-342: group, goal breach|sink. Not while the strike runs; a plan that exists becomes stale (stale_why = goal) and is recounted on the button (the rule of v1). Answer {group, goal, plan_status}.</summary>
        public string SetGoal(Dictionary<string, object> cmd)
        {
            string group = Str(cmd, "group");
            if (string.IsNullOrEmpty(group)) return Fail("bad_args", "group");
            var g = Find(group);
            if (g == null) return Fail("no_group");
            string goal = Str(cmd, "goal");
            if (goal != "breach" && goal != "sink") return Fail("bad_args", "goal");
            if (ExecRunning(g)) return Fail("executing");
            if (g.Goal != goal)
            {
                g.Goal = goal;
                MarkStale(g, "goal");
                _log("[Strike] strike goal " + g.Name + " goal=" + goal);
            }
            string st = g.Timeline == null ? "none" : g.Timeline.Status;
            return Ack(g.Name, "\"goal\":" + Q(goal) + ",\"plan_status\":" + Q(st));
        }

        /// <summary>group, mode single|spread, target_id (the main target), track_ids (the ships of a spread). Only valid targets (hostile, identified, not civilian).</summary>
        public string Target(Dictionary<string, object> cmd)
        {
            string group = Str(cmd, "group");
            if (string.IsNullOrEmpty(group)) return Fail("bad_args", "group");
            var g = Find(group);
            if (g == null) return Fail("no_group");
            string mode = Str(cmd, "mode") ?? "single";
            if (mode != "single" && mode != "spread") return Fail("bad_args", "mode");
            object o;
            if (!cmd.TryGetValue("target_id", out o) || !(o is double) || !IsInt((double)o)) return Fail("bad_args", "target_id");
            int main = (int)(double)o;
            var extra = new List<int>();
            if (cmd.TryGetValue("track_ids", out o))
            {
                var l = o as List<object>;
                if (l == null) return Fail("bad_args", "track_ids");
                foreach (object x in l)
                {
                    if (!(x is double) || !IsInt((double)x)) return Fail("bad_args", "track_ids");
                    extra.Add((int)(double)x);
                }
            }
            var ms = Ship(main);
            if (ms == null) return Fail("no_target");
            string why = StrikePlanMath.Exclusion(ms);
            if (why != null) return Fail("target_excluded", why);
            var ships = new List<int>();
            var rejected = new List<string>();
            if (mode == "spread")
            {
                var ok = new List<StrikeShip> { ms };
                foreach (int id in extra.Distinct())
                {
                    if (id == main) continue;
                    var s = Ship(id);
                    string w = s == null ? "no_target" : StrikePlanMath.Exclusion(s);
                    if (w != null) { rejected.Add("{\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"why\":" + Q(w) + "}"); continue; }
                    ok.Add(s);
                }
                foreach (var s in StrikePlanMath.Prioritize(ok, StrikeCalib.TonBandFrac)) ships.Add(s.TrackId);
            }
            else ships.Add(main);
            int oldMain = g.Target == null ? 0 : g.Target.MainId;
            bool changed = oldMain != main || g.Target == null || g.Target.Ships.Count != ships.Count || g.Target.Ships.Except(ships).Any() || g.Target.Mode != mode;
            g.Target = new StrikeTargetSet { Mode = mode, MainId = main };
            g.Target.Ships.AddRange(ships);
            if (oldMain != main) DeckTargetChanged(g);
            if (changed)
            {
                // T-342 C: a new target of the player ends the auto and the clearing of the old one: the rows not issued yet are taken back, nothing is launched at the new target by itself
                g.Auto.Waves = 0;
                if (g.Auto.On)
                {
                    if (g.Timeline != null && g.Timeline.Status == "executing") CancelExec(g, "target_changed");
                    else { g.Variants = new List<StrikeVariant>(); g.Chosen = -1; if (g.Timeline != null && !ExecRunning(g)) g.Timeline = null; }
                    AutoStop(g, "target_changed");
                    ClearEnd(g, "target_changed"); DropQueued(g);
                }
                else if (g.Clear != null) { ClearEnd(g, "target_changed"); DropQueued(g); }
            }
            if (g.Timeline != null) MarkStale(g, "target_changed");
            RecomputeWait(g, "target");
            _log("[Strike] strike target " + g.Name + " mode=" + mode + " main=" + main.ToString(CultureInfo.InvariantCulture) + " n=" + ships.Count.ToString(CultureInfo.InvariantCulture));
            return Ack(g.Name, "\"mode\":" + Q(mode) + ",\"main\":" + main.ToString(CultureInfo.InvariantCulture) + ",\"ships\":[" + string.Join(",", ships.Select(x => x.ToString(CultureInfo.InvariantCulture))) + "],\"rejected\":[" + string.Join(",", rejected) + "]");
        }
    }
}
