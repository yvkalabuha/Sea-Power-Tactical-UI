using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using J = VoiceBotBridge.Json;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 section 10 (strike_probe, dev only): the book of the early live probes. Which shooters the veto participant of the strike watches (Q-S5),
    // the orders the probe gave and when the ammunition of the shooter dropped (Q-S6: the delay "order -> launch"), the missiles in flight and what their seeker took (the first ship on the track).
    // Pure: the game side (StrikeGame.Probe.cs) feeds it.
    internal sealed class StrikeProbe
    {
        private sealed class Ord { public int Uid, TargetId, Salvo, CountBefore, Launched; public string Ammo; public double T, FirstT = -1; public bool Done; }
        private sealed class Msl { public int Id, Init, Last; public double T, DistNm; public bool Reported; }

        private readonly HashSet<int> _watched = new HashSet<int>();
        private readonly Dictionary<int, Ord> _orders = new Dictionary<int, Ord>();
        private readonly Dictionary<int, Msl> _missiles = new Dictionary<int, Msl>();

        public void Arm(int uid) { _watched.Add(uid); }
        public void Disarm(int uid) { _watched.Remove(uid); }
        public bool Watched(int uid) { return _watched.Contains(uid); }
        public int WatchedCount { get { return _watched.Count; } }

        /// <summary>Anything to observe: a watched shooter or an order that is not done (the game side reads nothing otherwise).</summary>
        public bool Active { get { return _watched.Count > 0 || _orders.Values.Any(o => !o.Done) || _missiles.Count > 0; } }

        /// <summary>Every uid that got an order (done or not): the game side watches their missiles in flight.</summary>
        public IList<int> OrderUids() { return _orders.Keys.OrderBy(x => x).ToList(); }

        /// <summary>The ammunition of the last order of the uid (null = none) and whether it still waits for its launch.</summary>
        public string OrderAmmo(int uid, out bool pending)
        {
            Ord o;
            pending = false;
            if (!_orders.TryGetValue(uid, out o)) return null;
            pending = !o.Done;
            return o.Ammo;
        }

        /// <summary>An uid with an order that still waits for its launch.</summary>
        public IList<int> Pending() { return _orders.Values.Where(o => !o.Done).Select(o => o.Uid).ToList(); }

        /// <summary>The probe gave the order: count = the ammunition of this type before it.</summary>
        public void Order(int uid, int targetId, string ammo, int salvo, int count, double t)
        {
            _orders[uid] = new Ord { Uid = uid, TargetId = targetId, Ammo = ammo, Salvo = Math.Max(1, salvo), CountBefore = count, T = t };
        }

        /// <summary>The ammunition of the shooter now. A drop below the last known count is a launch: line "strike launched uid= n= dt_order_s=" (dt from the order to the first drop).</summary>
        public bool Observe(int uid, int count, double t, out string line)
        {
            line = null;
            Ord o;
            if (!_orders.TryGetValue(uid, out o) || o.Done || count >= o.CountBefore) return false;
            int n = o.CountBefore - count;
            o.CountBefore = count; o.Launched += n;
            if (o.FirstT < 0) o.FirstT = t;
            if (o.Launched >= o.Salvo) o.Done = true;
            line = "strike launched uid=" + uid.ToString(CultureInfo.InvariantCulture) + " n=" + n.ToString(CultureInfo.InvariantCulture) + " dt_order_s=" + (t - o.T).ToString("0.0", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>A missile in flight: what the seeker was set to (init) and what it holds now (last); -1 = none / gone. A line when they differ (the seeker took another ship) or the target vanished.</summary>
        public bool Missile(int id, int init, int last, double t, out string line, double distNm = -1)
        {
            line = null;
            Msl m;
            if (!_missiles.TryGetValue(id, out m)) { m = new Msl { Id = id, Init = init }; _missiles[id] = m; }
            bool changed = m.Last != last || !m.Reported;
            m.Last = last; m.T = t; m.DistNm = distNm;
            if (init == last && last >= 0) return false;
            if (m.Reported && !changed) return false;
            m.Reported = true;
            line = "strike probe msl id=" + id.ToString(CultureInfo.InvariantCulture) + " init=" + init.ToString(CultureInfo.InvariantCulture) + " last=" + last.ToString(CultureInfo.InvariantCulture)
                   + " dist_nm=" + (distNm < 0 ? "?" : distNm.ToString("0.0", CultureInfo.InvariantCulture));
            return true;
        }

        public void ForgetMissiles(ICollection<int> alive)
        {
            foreach (int id in _missiles.Keys.Where(k => !alive.Contains(k)).ToList()) _missiles.Remove(id);
        }

        /// <summary>strike_probe op=limit: the arguments (a real track id is needed); null = ok, else the error code of the answer.</summary>
        public static string LimitArgs(int targetId) { return targetId < 0 ? "!bad_args|target_id" : null; }

        public void Clear() { _watched.Clear(); _orders.Clear(); _missiles.Clear(); }

        public string Json()
        {
            var sb = new StringBuilder("{\"watched\":[");
            sb.Append(string.Join(",", _watched.OrderBy(x => x).Select(x => x.ToString(CultureInfo.InvariantCulture))));
            sb.Append("],\"orders\":[");
            sb.Append(string.Join(",", _orders.Values.OrderBy(o => o.Uid).Select(o =>
                "{\"uid\":" + o.Uid.ToString(CultureInfo.InvariantCulture) + ",\"target\":" + o.TargetId.ToString(CultureInfo.InvariantCulture) + ",\"ammo\":" + J.Str(o.Ammo)
                + ",\"salvo\":" + o.Salvo.ToString(CultureInfo.InvariantCulture) + ",\"t\":" + J.Num(Math.Round(o.T, 1)) + ",\"launched\":" + o.Launched.ToString(CultureInfo.InvariantCulture)
                + ",\"dt_order_s\":" + (o.FirstT < 0 ? "null" : J.Num(Math.Round(o.FirstT - o.T, 1))) + ",\"done\":" + J.Bool(o.Done) + "}")));
            sb.Append("],\"missiles\":[");
            sb.Append(string.Join(",", _missiles.Values.OrderBy(m => m.Id).Select(m =>
                "{\"id\":" + m.Id.ToString(CultureInfo.InvariantCulture) + ",\"init\":" + m.Init.ToString(CultureInfo.InvariantCulture) + ",\"last\":" + m.Last.ToString(CultureInfo.InvariantCulture) + ",\"t\":" + J.Num(Math.Round(m.T, 1)) + "}")));
            return sb.Append("]}").ToString();
        }

        public static string SelfTest()
        {
            var p = new StrikeProbe();
            string line;
            if (p.Active || p.Watched(7)) return "empty";
            p.Arm(7);
            if (!p.Watched(7) || !p.Active || p.Watched(8) || p.WatchedCount != 1) return "arm";
            p.Disarm(7);
            if (p.Watched(7) || p.Active) return "disarm";
            // Q-S6: 2 of 4 launched 23 s after the order
            p.Order(7, 100, "usn_agm-84c", 4, 4, 1000);
            if (!p.Active || p.Pending().Count != 1) return "order is pending";
            if (p.Observe(7, 4, 1010, out line) || line != null) return "no drop, no line";
            if (!p.Observe(7, 2, 1023, out line) || line != "strike launched uid=7 n=2 dt_order_s=23.0") return "launched line " + line;
            if (p.Observe(7, 2, 1030, out line)) return "a repeat without a drop";
            if (p.Pending().Count != 1) return "still pending: 2 of 4 launched";
            if (!p.Observe(7, 0, 1025, out line) || line != "strike launched uid=7 n=2 dt_order_s=25.0") return "second part " + line;
            if (p.Pending().Count != 0 || p.Observe(7, 0, 1100, out line)) return "done";
            if (p.Observe(9, 0, 1100, out line)) return "unknown uid";
            string j = p.Json();
            var o = (Dictionary<string, object>)J.Parse(j);
            var orders = (List<object>)o["orders"];
            var o0 = (Dictionary<string, object>)orders[0];
            if (orders.Count != 1 || (double)o0["dt_order_s"] != 23 || (double)o0["launched"] != 4 || !(bool)o0["done"]) return "json orders " + j;
            // the seeker: the first ship on the track (init) vs what it holds
            if (p.Missile(55, 100, 100, 1010, out line)) return "same target is no event";
            if (!p.Missile(55, 100, 101, 1020, out line, 12.5) || line != "strike probe msl id=55 init=100 last=101 dist_nm=12.5") return "capture line " + line;
            if (p.Missile(55, 100, 101, 1021, out line)) return "reported once";
            if (!p.Missile(55, 100, -1, 1030, out line) || !line.Contains("last=-1")) return "target gone " + line;
            if (!p.Missile(56, 100, 101, 1040, out line) || !line.Contains("dist_nm=?")) return "unknown distance";
            p.ForgetMissiles(new HashSet<int> { 56 });
            var o2 = (Dictionary<string, object>)J.Parse(p.Json());
            if (((List<object>)o2["missiles"]).Count != 1) return "forget missiles";
            if (!(J.Parse(p.Json()) is Dictionary<string, object>)) return "json";
            p.Clear();
            if (p.Active || p.Json() != "{\"watched\":[],\"orders\":[],\"missiles\":[]}") return "clear " + p.Json();
            if (LimitArgs(-1) != "!bad_args|target_id" || LimitArgs(0) != null || LimitArgs(1004) != null) return "op=limit needs target_id";
            return null;
        }
    }
}
