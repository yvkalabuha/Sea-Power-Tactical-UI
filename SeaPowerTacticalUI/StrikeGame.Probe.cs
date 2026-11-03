using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 section 10: the game side of the dev probe (strike_probe) and the veto participant "strike". The veto touches only a shooter the probe put under watch
    // (phase A; the groups come in phase C), only an automatic attack (Common/Veto.cs filters that before the chain), only an anti-ship missile that the mod did not plan (StrikeLaunchBook).
    // fire is a REAL launch through the shared Attack (hostile targets only, confirm:true, neutral corridor checked by it): the dev group "fire" of the dev table.
    internal sealed partial class GameApi
    {
        internal StrikeController Strike;                                   // set by StrikeFeature
        internal Ui.IStrikeWindow StrikeWindow;                             // set by StrikeFeature (dev command window {name: strike})
        internal IStrikeGame StrikeGameHandle;                              // the game layer, for the probe ops
        internal readonly StrikeProbe StrikeProbeBook = new StrikeProbe();
        internal readonly StrikeLaunchBook StrikeBook = new StrikeLaunchBook();
        private string _strikeVetoKey;
        private double _strikeVetoAt = -1e9;

        private void StrikeLog(string line)
        {
            try { var s = AswLogSink; if (s != null) s(line); else UnityEngine.Debug.Log(line); } catch { }
        }

        /// <summary>The participant "strike" of the engage veto chain: true = the game does not add this automatic task. Never throws.</summary>
        internal bool StrikeVeto(object task)
        {
            try
            {
                var g = StrikeGameHandle;
                var ctl = Strike;
                bool probe = StrikeProbeBook.WatchedCount > 0, groups = ctl != null && ctl.Groups.Count > 0;
                if (task == null || g == null || (!probe && !groups)) return false;
                var shooter = Get(task, StrikeSpecs.TaskBase);
                if (shooter == null) return false;
                int uid = UidOf(shooter);
                string grp;
                // phase B (ADR-0132 section 6): every member of a strike group is watched, the dev probe adds single aircraft
                if (!(probe && StrikeProbeBook.Watched(uid)) && !(groups && ctl.IsMember(uid, out grp))) return false;
                string ammo = Get(task, StrikeSpecs.TaskAmmoId) as string;
                StrikeMissile m = null;
                foreach (var a in g.Aircraft())
                    if (a != null && a.Uid == uid) { m = a.Missiles.FirstOrDefault(x => x.Ammo == ammo); break; }
                bool isAsm = m != null && StrikeAirMath.IsStrikeMissile(m);
                var target = Get(task, StrikeSpecs.TaskTarget);
                int tid = target != null && IsAlive(target) ? RrMenuTrack(target) : -1;
                double now = g.Now;
                if (!StrikeLaunchBook.Veto(true, true, isAsm, StrikeBook.Expected(uid, tid, ammo, now))) return false;
                string key = uid.ToString(CultureInfo.InvariantCulture) + "|" + ammo + "|" + tid.ToString(CultureInfo.InvariantCulture);
                if (key != _strikeVetoKey || now - _strikeVetoAt >= 5.0 || now < _strikeVetoAt)
                {
                    _strikeVetoKey = key; _strikeVetoAt = now;
                    StrikeLog("[Strike] strike veto uid=" + uid.ToString(CultureInfo.InvariantCulture) + " ammo=" + ammo + " target=" + tid.ToString(CultureInfo.InvariantCulture));
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        private static int Int(Dictionary<string, object> c, string k, int dflt)
        {
            object v;
            return c != null && c.TryGetValue(k, out v) && v is double && Math.Abs((double)v - Math.Round((double)v)) < 1e-9 ? (int)(double)v : dflt;
        }

        private static string StrOf(Dictionary<string, object> c, string k) { object v; return c != null && c.TryGetValue(k, out v) ? v as string : null; }

        /// <summary>strike_probe: op veto | fire | tof | status | clear. Main thread. Answer: ack json or "!code[|detail]".</summary>
        internal string StrikeProbeOp(Dictionary<string, object> cmd)
        {
            var g = StrikeGameHandle;
            if (g == null) return "!unsupported";
            string op = StrOf(cmd, "op");
            int uid = Int(cmd, "uid", -1), tid = Int(cmd, "target_id", -1);
            string ammo = StrOf(cmd, "ammo");
            try
            {
                switch (op)
                {
                    case "veto":
                    {
                        if (uid <= 0) return "!bad_args|uid";
                        object onv; bool on = !(cmd.TryGetValue("on", out onv) && onv is bool) || (bool)onv;
                        if (on && g.Aircraft().All(a => a.Uid != uid)) return "!no_aircraft";
                        if (on) StrikeProbeBook.Arm(uid); else StrikeProbeBook.Disarm(uid);
                        StrikeLog("[Strike] strike probe veto uid=" + uid.ToString(CultureInfo.InvariantCulture) + " on=" + on);
                        return "{\"ok\":true,\"op\":\"veto\",\"uid\":" + uid.ToString(CultureInfo.InvariantCulture) + ",\"on\":" + Json.Bool(on) + "}";
                    }
                    case "fire": return ProbeFire(g, uid, tid, ammo, Int(cmd, "salvo", 1));
                    case "tof": return ProbeTof(g, uid, tid, ammo);
                    case "limit": return ProbeLimit(tid);
                    case "ew":
                    {
                        object onv; bool? onEw = cmd.TryGetValue("on", out onv) && onv is bool ? (bool?)(bool)onv : null;
                        object objv; bool byObj = cmd.TryGetValue("obj", out objv) && objv is bool && (bool)objv;
                        return ProbeEw(g, uid, tid, onEw, null, byObj);
                    }
                    case "status":
                        return "{\"ok\":true,\"op\":\"status\",\"probe\":" + StrikeProbeBook.Json() + ",\"expected\":" + StrikeBook.Count.ToString(CultureInfo.InvariantCulture) + ",\"veto_chain\":[" + string.Join(",", EngageVetos.Names.Select(Json.Str)) + "]}";
                    case "clear":
                        StrikeProbeBook.Clear(); StrikeBook.Clear();
                        return "{\"ok\":true,\"op\":\"clear\"}";
                    default: return "!bad_args|op";
                }
            }
            catch (Exception e) { return "!probe_failed|" + e.GetType().Name; }
        }

        /// <summary>T-342 (DEV ONLY, E1 calibration): the sinking state of a foreign ship behind a track: _limitIntegrity / _maxLimitIntegrity / _lastInflictedDamage of its Compartments. The player never sees it.</summary>
        private string ProbeLimit(int tid)
        {
            string bad = StrikeProbe.LimitArgs(tid);
            if (bad != null) return bad;
            var cv = FindContact(tid);
            var obj = cv == null ? null : Get(cv, CoreSpecs.VehObject);
            if (obj == null) return "!no_target";
            double? limit = null, max = null, last = null; bool destroyed = false;
            try
            {
                var comp = Get(obj, EvasionSpecs.ObjCompartments);
                if (comp != null)
                {
                    if (Present(StrikeSpecs.CompLimit)) limit = D(Get(comp, StrikeSpecs.CompLimit));
                    if (Present(StrikeSpecs.CompMaxLimit)) max = D(Get(comp, StrikeSpecs.CompMaxLimit));
                    if (Present(StrikeSpecs.CompLastDamage)) last = D(Get(comp, StrikeSpecs.CompLastDamage));
                }
                destroyed = BoolOf(Get(obj, ContactInfoSpecs.ObjIsDestroyed));
            }
            catch (Exception) { }
            double? frac = limit.HasValue && max.HasValue && max.Value > 0 ? (double?)(limit.Value / max.Value) : null;
            Func<double?, string> n = v => v.HasValue ? Json.Num(Math.Round(v.Value, 3)) : "null";
            StrikeLog("[Strike] strike probe limit tgt=" + tid.ToString(CultureInfo.InvariantCulture) + " limit=" + (limit.HasValue ? Math.Round(limit.Value, 1).ToString(CultureInfo.InvariantCulture) : "-") + " max=" + (max.HasValue ? Math.Round(max.Value, 1).ToString(CultureInfo.InvariantCulture) : "-")
                      + " last=" + (last.HasValue ? Math.Round(last.Value, 1).ToString(CultureInfo.InvariantCulture) : "-") + " frac=" + (frac.HasValue ? Math.Round(frac.Value, 3).ToString(CultureInfo.InvariantCulture) : "-"));
            return "{\"ok\":true,\"op\":\"limit\",\"tgt\":" + tid.ToString(CultureInfo.InvariantCulture) + ",\"limit\":" + n(limit) + ",\"max_limit\":" + n(max) + ",\"last_dmg\":" + n(last) + ",\"frac\":" + n(frac) + ",\"destroyed\":" + Json.Bool(destroyed) + ",\"src\":\"dev\"}";
        }

        private string ProbeFire(IStrikeGame g, int uid, int tid, string ammo, int salvo)
        {
            if (uid <= 0) return "!bad_args|uid";
            if (tid < 0) return "!bad_args|target_id";
            if (string.IsNullOrEmpty(ammo)) return "!bad_args|ammo";
            var a = g.Aircraft().FirstOrDefault(x => x.Uid == uid);
            if (a == null) return "!no_aircraft";
            var m = a.Missiles.FirstOrDefault(x => x.Ammo == ammo);
            if (m == null || m.Count <= 0) return "!no_ammo";
            if (!StrikeAirMath.IsStrikeMissile(m)) return "!not_asm|" + StrikeAirMath.ClassOf(m);
            var ship = g.Ships().FirstOrDefault(x => x.TrackId == tid);   // review T-341: a real launch only at a hostile, identified, non-civilian surface track
            if (ship == null) return "!no_target";
            string excl = StrikePlanMath.Exclusion(ship);
            if (excl != null) return "!target_excluded|" + excl;
            int n = Math.Max(1, Math.Min(Math.Min(8, salvo), m.Count));
            var c = new Dictionary<string, object> { { "uid", (double)uid }, { "target_id", (double)tid }, { "weapon", ammo }, { "salvo", (double)n }, { "confirm", true } };
            double now = g.Now;
            StrikeBook.Expect(uid, tid, ammo, now);
            string ack = Attack("null", c, false);
            bool ok = ack != null && ack.Contains("\"ok\":true");
            string result = "ok";
            if (!ok)
            {
                StrikeBook.Clear(uid);
                string code = "attack_failed", detail = null;
                try { var d = Json.Parse(ack) as Dictionary<string, object>; if (d != null) { if (d.ContainsKey("error")) code = d["error"] as string ?? code; if (d.ContainsKey("detail")) detail = d["detail"] as string; } } catch (Exception) { }
                result = code;
                StrikeLog("[Strike] strike probe fire uid=" + uid.ToString(CultureInfo.InvariantCulture) + " target=" + tid.ToString(CultureInfo.InvariantCulture) + " ammo=" + ammo + " salvo=" + n.ToString(CultureInfo.InvariantCulture) + " -> " + result);
                return "!" + code + (detail == null ? "" : "|" + detail);
            }
            StrikeProbeBook.Order(uid, tid, ammo, n, m.Count, now);
            StrikeLog("[Strike] strike probe fire uid=" + uid.ToString(CultureInfo.InvariantCulture) + " target=" + tid.ToString(CultureInfo.InvariantCulture) + " ammo=" + ammo + " salvo=" + n.ToString(CultureInfo.InvariantCulture) + " -> " + result);
            return "{\"ok\":true,\"op\":\"fire\",\"uid\":" + uid.ToString(CultureInfo.InvariantCulture) + ",\"salvo\":" + n.ToString(CultureInfo.InvariantCulture) + ",\"count\":" + m.Count.ToString(CultureInfo.InvariantCulture) + ",\"t\":" + Json.Num(Math.Round(now, 1)) + "}";
        }

        private string ProbeTof(IStrikeGame g, int uid, int tid, string ammo)
        {
            if (uid <= 0) return "!bad_args|uid";
            var a = g.Aircraft().FirstOrDefault(x => x.Uid == uid);
            if (a == null) return "!no_aircraft";
            var m = string.IsNullOrEmpty(ammo) ? a.Missiles.FirstOrDefault(StrikeAirMath.IsStrikeMissile) : a.Missiles.FirstOrDefault(x => x.Ammo == ammo);
            if (m == null) return "!no_ammo";
            var s = g.Ships().FirstOrDefault(x => x.TrackId == tid);
            if (s == null) return "!no_target";
            double range = AswMath.DistNm(new GeoPt(a.Lat, a.Lon), new GeoPt(s.Lat, s.Lon));
            double rough = StrikeTotMath.RoughTofS(range, m.SpeedKn > 0 ? m.SpeedKn : 540), sim;
            bool ok = g.Tof(uid, m, a.Lat, a.Lon, a.AltFt, a.CruiseKn, s, s.Lat, s.Lon, out sim);
            return "{\"ok\":true,\"op\":\"tof\",\"tof_sim_s\":" + (ok ? Json.Num(Math.Round(sim, 1)) : "null") + ",\"tof_rough_s\":" + Json.Num(Math.Round(rough, 1)) + ",\"range_nm\":" + Json.Num(Math.Round(range, 2)) + ",\"src\":\"" + (ok ? "sim" : "rough") + "\"}";
        }

        /// <summary>The probe watch (about twice a second of real time, only while the probe is active): the ammunition of the ordered shooters (a launch) and their missiles in flight.</summary>
        internal void StrikeProbeTick()
        {
            var book = StrikeProbeBook; var g = StrikeGameHandle;
            if (g == null || !book.Active) return;
            try
            {
                double now = g.Now;
                var air = g.Aircraft();
                var alive = new HashSet<int>();
                foreach (int uid in book.OrderUids())
                {
                    var a = air.FirstOrDefault(x => x.Uid == uid);
                    if (a == null) continue;
                    bool pending;
                    string ammo = book.OrderAmmo(uid, out pending);
                    string line;
                    if (pending && ammo != null && book.Observe(uid, a.Missiles.Where(x => x.Ammo == ammo).Sum(x => x.Count), now, out line)) StrikeLog("[Strike] " + line);
                    var u = FindOwnUnit(uid);
                    if (u != null) ProbeMissiles(u, now, alive);
                }
                book.ForgetMissiles(alive);
            }
            catch (Exception) { }
        }

        private void ProbeMissiles(object unit, double now, HashSet<int> alive)
        {
            var systems = Get(Get(unit, AirOpsSpecs.ObjObp), StrikeSpecs.ObpWeaponSystems) as IEnumerable;
            if (systems == null) return;
            foreach (var ws in systems)
            {
                var list = ws == null ? null : Get(ws, StrikeSpecs.WsLaunched) as IEnumerable;
                if (list == null) continue;
                foreach (var w in list)
                {
                    if (w == null || !IsAlive(w)) continue;
                    int id = RuntimeHelpers.GetHashCode(w);
                    alive.Add(id);
                    var init = Get(w, StrikeSpecs.WbInitTarget); var last = Get(w, StrikeSpecs.WbLastTarget);
                    int ti = init != null && IsAlive(init) ? RrMenuTrack(init) : -1, tl = last != null && IsAlive(last) ? RrMenuTrack(last) : -1;
                    string line;
                    if (StrikeProbeBook.Missile(id, ti, tl, now, out line)) StrikeLog("[Strike] " + line);
                }
            }
        }
    }
}
