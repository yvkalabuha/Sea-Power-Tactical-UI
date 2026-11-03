using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace VoiceBotBridge.Dev
{
    /// <summary>
    /// QA cheat air_ready (dev mod only; customer request "переподготовка самолётов на авианосце, чтобы не ждать"): on the player's own flight decks
    /// sets the rest of the pending launch tasks (ready-up) and of the cool-down tasks (turnaround after landing) to 0, so the game finishes them
    /// on its next deck tick. Runs on the Unity main thread (DevPump). Reflection by DevAirReadySpecs only, BCL only (compiled by devtest too).
    /// </summary>
    internal sealed class DevAirReady
    {
        private readonly Func<string, Type> _findType;
        private readonly Dictionary<int, MemberInfo> _members = new Dictionary<int, MemberInfo>();
        private readonly Dictionary<string, Type> _types = new Dictionary<string, Type>();

        public DevAirReady(Func<string, Type> findType = null) { _findType = findType ?? FindInDomain; }

        private static Type FindInDomain(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(fullName, false); } catch { t = null; }
                if (t != null) return t;
            }
            return null;
        }

        private MemberInfo Member(MemberSpec s)
        {
            MemberInfo m;
            if (_members.TryGetValue(s.Index, out m)) return m;
            Type t;
            if (!_types.TryGetValue(s.Type, out t)) { t = _findType(s.Type); _types[s.Type] = t; }
            string why;
            m = MemberResolver.Resolve(t, s, out why);
            if (m != null) _members[s.Index] = m;   // a miss is retried: the game assembly may load after the first call
            return m;
        }

        private object Get(object o, MemberSpec s)
        {
            if (o == null && !s.Static) return null;
            var m = Member(s);
            try
            {
                var f = m as FieldInfo;
                if (f != null) return f.GetValue(s.Static ? null : o);
                var p = m as PropertyInfo;
                if (p != null) return p.GetValue(s.Static ? null : o, null);
            }
            catch { }
            return null;
        }

        private static double? Num(object v)
        {
            if (v == null) return null;
            if (v is IConvertible && !(v is string))
            {
                try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
            }
            try
            {
                var p = v.GetType().GetProperty("Value");   // UniRx ReactiveProperty<T>
                return p == null ? (double?)null : Num(p.GetValue(v, null));
            }
            catch { return null; }
        }

        /// <summary>null = ok (data filled); otherwise "unsupported" | "no_mission" | "unit_not_found" | "no_flight_deck" | "deck_task_not_found".</summary>
        public string Run(Dictionary<string, object> a, out Dictionary<string, object> data, out string detail)
        {
            data = null; detail = null;
            var missing = new List<string>();
            foreach (var s in DevAirReadySpecs.Core) if (Member(s) == null) missing.Add(s.Key);
            if (missing.Count > 0) { detail = string.Join(", ", missing.ToArray()); return "unsupported"; }
            int shipUid = a != null && a.ContainsKey("ship_uid") ? (int)(double)a["ship_uid"] : -1;
            string key = a != null && a.ContainsKey("key") ? a["key"] as string : null;

            var vessels = Get(Get(null, DevAirReadySpecs.PlayerTaskforce), DevAirReadySpecs.TfVessels) as IEnumerable;
            if (vessels == null) return "no_mission";
            var rows = new List<object>();
            int changed = 0, decks = 0;
            bool shipFound = false;
            var dur = Member(DevAirReadySpecs.TaskDuration) as FieldInfo;
            foreach (var ship in vessels)
            {
                if (ship == null) continue;
                var id = Num(Get(ship, DevAirReadySpecs.ObjUniqueId));
                if (!id.HasValue) continue;
                int uid = (int)id.Value;
                if (shipUid >= 0 && uid != shipUid) continue;
                shipFound = true;
                var deck = Get(Get(ship, DevAirReadySpecs.ObjObp), DevAirReadySpecs.ObpFlightDeck);
                if (deck == null) continue;
                decks++;
                var tasks = Get(deck, DevAirReadySpecs.DeckTasks) as IEnumerable;
                if (tasks == null) continue;
                foreach (var t in tasks)
                {
                    if (t == null) continue;
                    string tn = t.GetType().Name;
                    if (tn != "PendingLaunchTask" && tn != "CoolDownTask") continue;
                    string k = (tn == "PendingLaunchTask" ? "task:" : "cool:") + Get(t, DevAirReadySpecs.TaskUid);
                    if (key != null && k != key) continue;
                    double was = Num(Get(t, DevAirReadySpecs.TaskDuration)) ?? 0;
                    bool set = false;
                    if (was > 0)
                    {
                        try { dur.SetValue(t, Convert.ChangeType(0, dur.FieldType, CultureInfo.InvariantCulture)); set = true; changed++; }
                        catch { }
                    }
                    string state = null;
                    try { var st = Get(Get(t, DevAirReadySpecs.TaskStateMachine), DevAirReadySpecs.MachineState); if (st != null) state = st.GetType().Name; } catch { }
                    rows.Add(new Dictionary<string, object>
                    {
                        { "ship_uid", uid }, { "key", k }, { "type", Get(Get(t, DevAirReadySpecs.TaskVehicleType), DevAirReadySpecs.VtFileName) as string },
                        { "state", state }, { "duration_was", Math.Round(was, 1) }, { "set", set },
                    });
                }
            }
            if (shipUid >= 0 && !shipFound) return "unit_not_found";
            if (shipUid >= 0 && decks == 0) return "no_flight_deck";
            if (key != null && rows.Count == 0) return "deck_task_not_found";
            data = new Dictionary<string, object> { { "changed", changed }, { "decks", decks }, { "tasks", rows } };
            return null;
        }

        /// <summary>The journal line of every call (tests rely on the prefix).</summary>
        public static string Line(Dictionary<string, object> a, string result)
        {
            object v;
            string ship = a != null && a.TryGetValue("ship_uid", out v) && v is double ? ((double)v).ToString(CultureInfo.InvariantCulture) : "-";
            string key = a != null && a.TryGetValue("key", out v) && v is string ? (string)v : "-";
            bool all = a != null && a.TryGetValue("all", out v) && v is bool && (bool)v;
            return "[Dev][AIR] air_ready ship=" + ship + " key=" + key + " all=" + (all ? "true" : "false") + " → " + result;
        }
    }
}
