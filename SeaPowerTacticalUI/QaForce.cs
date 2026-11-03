using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using VoiceBotBridge.Ui;
using J = VoiceBotBridge.Json;

namespace VoiceBotBridge
{
    // T-361 (ADR-0136): QA scenarios by REAL game orders, for the dev `force` command (link command qa_force). Dev only: refused unless the diag slot of the dev mod is registered,
    // and only inside a mission. The mod's own state is never faked (no _ctlMem / tracker / selection calls, no position writes): the game gets a waypoint, a buoy drop or a
    // weapon status, the mod then reacts the way it does to the AI or the player. Every call is one "[QaForce] op=... -> ok|<error>" line in the log.
    internal sealed partial class GameApi
    {
        private static readonly string[] QaOps = { "buoy_on_sub", "route_shift", "divert", "ff_lead", "steer_hostile" };

        /// <summary>T-361: QA scenarios by real game orders. Refused unless the diag slot is registered (the dev mod is installed) and a mission runs.
        /// Errors: forbidden, not_in_session, bad_args, unit_not_found, not_air_unit, no_enemy_sub, no_enemy_air, no_active_buoy, no_waypoints, not_in_ai_window, no_track, no_position, order_failed:&lt;detail&gt;.</summary>
        internal string QaForceCmd(string id, Dictionary<string, object> cmd)
        {
            string op = cmd.ContainsKey("op") ? cmd["op"] as string : null;
            string res;
            if (AppDomain.CurrentDomain.GetData(DiagHost.SlotKey) == null) res = Err(id, "forbidden", "needs the dev mod");
            else if (!InMission()) res = Err(id, "not_in_session", null);
            else if (op == null || Array.IndexOf(QaOps, op) < 0) res = Err(id, "bad_args", "op: " + string.Join("|", QaOps));
            else
            {
                try
                {
                    switch (op)
                    {
                        case "buoy_on_sub": res = ForceBuoyOnSub(id, cmd); break;
                        case "route_shift": res = ForceRouteShift(id, cmd); break;
                        case "divert": res = ForceDivert(id, cmd); break;
                        case "ff_lead": res = ForceFfLead(id, cmd); break;
                        default: res = ForceSteerHostile(id, cmd); break;
                    }
                }
                catch (Exception e) { var x = e.InnerException ?? e; res = Err(id, "order_failed:" + x.GetType().Name, x.Message); }
            }
            QaLog(op, cmd, res);
            return res;
        }

        private void QaLog(string op, Dictionary<string, object> cmd, string res)
        {
            string outcome = "ok";
            if (res == null || res.IndexOf("\"ok\":true", StringComparison.Ordinal) < 0)
            {
                var d = res == null ? null : J.Parse(res) as Dictionary<string, object>;
                outcome = d != null && d.ContainsKey("error") ? d["error"] as string : "error";
            }
            string uid = cmd.ContainsKey("uid") ? J.Num(D(cmd["uid"]) ?? -1) : cmd.ContainsKey("to_uid") ? J.Num(D(cmd["to_uid"]) ?? -1) : "-";
            string line = "[QaForce] op=" + (op ?? "-") + " uid=" + uid + " -> " + outcome;
            try { var s = AswLogSink; if (s != null) s(line); else UnityEngine.Debug.Log(line); } catch { }
        }

        private static string QaIsOk(string ack) { return ack != null && ack.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0 ? null : ack; }

        private static double QaNum(Dictionary<string, object> cmd, string k, double dflt)
        {
            double? v = cmd.ContainsKey(k) ? D(cmd[k]) : null;
            return v.HasValue ? v.Value : dflt;
        }

        private static bool QaBool(Dictionary<string, object> cmd, string k, bool dflt)
        {
            return cmd.ContainsKey(k) && cmd[k] is bool ? (bool)cmd[k] : dflt;
        }

        private static string QaPt(GeoPt p) { return "\"lat\":" + J.Num(Math.Round(p.Lat, 5)) + ",\"lon\":" + J.Num(Math.Round(p.Lon, 5)); }

        private bool QaPos(object u, out GeoPt p)
        {
            double lat, lon, h;
            p = default(GeoPt);
            if (u == null || !OwnPos(u, out lat, out lon, out h)) return false;
            p = new GeoPt(lat, lon);
            return true;
        }

        /// <summary>The own unit of the command argument; err = the ack error when it is missing or (needAir) not an aircraft.</summary>
        private object QaOwn(string id, Dictionary<string, object> cmd, string key, bool needAir, out string err)
        {
            err = null;
            double? v = cmd.ContainsKey(key) ? D(cmd[key]) : null;
            if (!v.HasValue) { err = Err(id, "bad_args", key + " is required"); return null; }
            var u = FindOwnUnit((int)v.Value);
            if (u == null) { err = Err(id, "unit_not_found", key + "=" + (int)v.Value); return null; }
            if (needAir && !IsAirUnit(u)) { err = Err(id, "not_air_unit", key + "=" + (int)v.Value); return null; }
            return u;
        }

        // ---------- the enemy side (the truth of the game: a cheat of the dev channel) ----------

        private IEnumerable<object> EnemyUnits(MemberSpec[] lists)
        {
            var tf = GetStatic(QaForceSpecs.EnemyTaskforce);
            if (tf == null) yield break;
            foreach (var spec in lists)
            {
                var list = Get(tf, spec) as IEnumerable;
                if (list == null) continue;
                foreach (var u in list)
                    if (u != null && IsAlive(u) && !BoolOf(Get(u, ContactInfoSpecs.ObjIsDestroyed))) yield return u;
            }
        }

        private object FindEnemyUnit(int uid, MemberSpec[] lists)
        {
            foreach (var u in EnemyUnits(lists))
            {
                var v = D(Get(u, QaForceSpecs.ObjUniqueId));
                if (v.HasValue && (int)v.Value == uid) return u;
            }
            return null;
        }

        /// <summary>The enemy unit with the uid, else the one nearest to from (null = none).</summary>
        private object QaEnemyNear(MemberSpec[] lists, double? uid, GeoPt from, out double distNm)
        {
            distNm = 0;
            if (uid.HasValue) { var f = FindEnemyUnit((int)uid.Value, lists); GeoPt fp; if (f != null && QaPos(f, out fp)) distNm = AswMath.DistNm(from, fp); return f; }
            object best = null; double bd = double.MaxValue;
            foreach (var u in EnemyUnits(lists))
            {
                GeoPt p;
                if (!QaPos(u, out p)) continue;
                double d = AswMath.DistNm(from, p);
                if (d < bd) { bd = d; best = u; }
            }
            distNm = best == null ? 0 : bd;
            return best;
        }

        // ---------- operations ----------

        /// <summary>T-316: an active buoy dropped by the own ASW aircraft near the TRUE position of an enemy submarine (the buoy and the game then make the live contact). data: uid, sub_uid, sub_name, sub_dist_nm, ammo, lat, lon, queued.</summary>
        private string ForceBuoyOnSub(string id, Dictionary<string, object> cmd)
        {
            string err; var u = QaOwn(id, cmd, "uid", true, out err);
            if (u == null) return err;
            GeoPt me;
            if (!QaPos(u, out me)) return Err(id, "no_position", null);
            double dist = QaNum(cmd, "dist_nm", 0.3);
            double sd;
            var sub = QaEnemyNear(new[] { QaForceSpecs.EnemySubmarines }, cmd.ContainsKey("target_uid") ? D(cmd["target_uid"]) : null, me, out sd);
            GeoPt sp;
            if (sub == null) return Err(id, "no_enemy_sub", null);
            if (!QaPos(sub, out sp)) return Err(id, "no_position", "submarine");
            string ammo = cmd.ContainsKey("ammo") ? cmd["ammo"] as string : null;
            if (string.IsNullOrEmpty(ammo)) ammo = AswMath.PickBuoy(BuoyTypesOn(u), true, _aswParams);
            if (string.IsNullOrEmpty(ammo)) return Err(id, "no_active_buoy", null);
            var pt = QaForceMath.Offset(sp, AswMath.BearingDeg(sp, me), dist);   // on the side of the aircraft: it flies over the point
            int queued;
            string e = DropSonobuoys(u, ammo, new List<GeoPt> { pt }, out queued);
            if (e != null) return Err(id, "order_failed:" + e, null);
            return Ok(id, "\"uid\":" + J.Num(UidOf(u)) + ",\"sub_uid\":" + J.Num(D(Get(sub, QaForceSpecs.ObjUniqueId)) ?? -1) + ",\"sub_name\":" + J.Str(NameOf(sub) ?? "")
                + ",\"sub_dist_nm\":" + J.Num(Math.Round(sd, 1)) + ",\"ammo\":" + J.Str(ammo) + "," + QaPt(pt) + ",\"queued\":" + queued);
        }

        /// <summary>T-323: the LAST waypoint of the game's current route moved by a raw game order (no ControlMemory, no selection), while the AI fights. data: uid, wps, from, to, ai_state, in_window.</summary>
        private string ForceRouteShift(string id, Dictionary<string, object> cmd)
        {
            string err; var u = QaOwn(id, cmd, "uid", true, out err);
            if (u == null) return err;
            int uid = UidOf(u);
            string state = StateName(u, "CurrentStateName");
            bool inWindow = TakeTracker.InWindow(uid, Frame.MissionTimeS ?? 0);
            bool aiNow = CapTakeoverMath.AiCombatState(state);
            if (QaBool(cmd, "require_ai", true) && !inWindow && !aiNow) return Err(id, "not_in_ai_window", "state=" + state);
            var wps = Waypoints(u);
            var shifted = QaForceMath.ShiftLast(wps, QaNum(cmd, "bearing", 90), QaNum(cmd, "dist_nm", 2));
            if (shifted == null) return Err(id, "no_waypoints", null);
            for (int i = 0; i < shifted.Count; i++)
            {
                string bad = QaIsOk(PlaceWaypoint(id, u, shifted[i][0], shifted[i][1], i > 0, ""));
                if (bad != null) return bad;
            }
            var from = wps[wps.Count - 1]; var to = shifted[shifted.Count - 1];
            return Ok(id, "\"uid\":" + J.Num(uid) + ",\"wps\":" + wps.Count + ",\"from\":{" + QaPt(new GeoPt(from[0], from[1])) + "},\"to\":{" + QaPt(new GeoPt(to[0], to[1])) + "}"
                + ",\"ai_state\":" + J.Str(state ?? "") + ",\"in_window\":" + J.Bool(inWindow));
        }

        /// <summary>T-332: one waypoint instead of the way home (what the deck does at a cancelled landing). Default bearing: 90 deg right of the course the unit steers now. data: uid, lat, lon, state, rtb_order.</summary>
        private string ForceDivert(string id, Dictionary<string, object> cmd)
        {
            string err; var u = QaOwn(id, cmd, "uid", true, out err);
            if (u == null) return err;
            GeoPt me;
            if (!QaPos(u, out me)) return Err(id, "no_position", null);
            double brg;
            if (cmd.ContainsKey("bearing") && D(cmd["bearing"]).HasValue) brg = D(cmd["bearing"]).Value;
            else
            {
                double? course = DemandedCourse(u);
                if (!course.HasValue) { var w = Waypoints(u); course = w.Count > 0 ? AswMath.BearingDeg(me, new GeoPt(w[w.Count - 1][0], w[w.Count - 1][1])) : 0; }
                brg = course.Value + 90;
            }
            var pt = QaForceMath.Offset(me, brg, QaNum(cmd, "dist_nm", 20));
            string bad = QaIsOk(PlaceWaypoint(id, u, pt.Lat, pt.Lon, false, ""));
            if (bad != null) return bad;
            return Ok(id, "\"uid\":" + J.Num(UidOf(u)) + "," + QaPt(pt) + ",\"bearing\":" + J.Num(Math.Round(((brg % 360) + 360) % 360, 1))
                + ",\"state\":" + J.Str(StateName(u, "CurrentStateName") ?? "") + ",\"rtb_order\":" + J.Bool(QaOrderIs(u, "ReturnToBase")));
        }

        private bool QaOrderIs(object u, string orderType)
        {
            try
            {
                var o = RxGet(u, CapSpecs.ObjCurrentOrder);
                var t = o == null ? null : Get(o, CapSpecs.OrderType);
                return t != null && t.ToString() == orderType;
            }
            catch { return false; }
        }

        /// <summary>T-333: the own aircraft goes to a point on the shooter-to-target line (off_deg 0: inside the friendly-fire cone of the guard; 30: outside it). One order per call.
        /// data: uid, shooter, target_id, lat, lon, fr_target_nm, cone_deg.</summary>
        private string ForceFfLead(string id, Dictionary<string, object> cmd)
        {
            string err; var u = QaOwn(id, cmd, "uid", true, out err);
            if (u == null) return err;
            var sh = QaOwn(id, cmd, "shooter", true, out err);
            if (sh == null) return err;
            double? tid = cmd.ContainsKey("target_id") ? D(cmd["target_id"]) : null;
            if (!tid.HasValue) return Err(id, "bad_args", "target_id is required");
            RingPt tp; bool air;
            if (!RrTrackPose((int)tid.Value, out tp, out air)) return Err(id, "no_track", "target_id=" + (int)tid.Value);
            GeoPt s;
            if (!QaPos(sh, out s)) return Err(id, "no_position", "shooter");
            var target = new GeoPt(tp.Lat, tp.Lon);
            var pt = QaForceMath.LeadPoint(s, target, QaNum(cmd, "ahead_nm", 1.0), QaNum(cmd, "off_deg", 0));
            string bad = QaIsOk(PlaceWaypoint(id, u, pt.Lat, pt.Lon, false, ""));
            if (bad != null) return bad;
            double toTarget;
            double cone = QaForceMath.ConeDeg(s, target, pt, out toTarget);
            return Ok(id, "\"uid\":" + J.Num(UidOf(u)) + ",\"shooter\":" + J.Num(UidOf(sh)) + ",\"target_id\":" + J.Num((int)tid.Value) + "," + QaPt(pt)
                + ",\"fr_target_nm\":" + J.Num(Math.Round(toTarget, 2)) + ",\"cone_deg\":" + J.Num(Math.Round(cone, 1)));
        }

        /// <summary>T-318: a hostile aircraft flies to stop_nm from the own SAR helicopter (away=true: 60 nm away on the same line), with its weapons on hold unless hold_fire=false.
        /// data: uid, name, to_uid, dist_nm, lat, lon, tracked, hold_fire (ok|unsupported|off).</summary>
        private string ForceSteerHostile(string id, Dictionary<string, object> cmd)
        {
            string err; var helo = QaOwn(id, cmd, "to_uid", true, out err);
            if (helo == null) return err;
            GeoPt hp;
            if (!QaPos(helo, out hp)) return Err(id, "no_position", "to_uid");
            double dist;
            var enemy = QaEnemyNear(new[] { QaForceSpecs.EnemyAircraft, QaForceSpecs.EnemyHelicopters }, cmd.ContainsKey("uid") ? D(cmd["uid"]) : null, hp, out dist);
            GeoPt ep;
            if (enemy == null) return Err(id, "no_enemy_air", null);
            if (!QaPos(enemy, out ep)) return Err(id, "no_position", "hostile");
            var pt = QaForceMath.SteerPoint(hp, ep, QaNum(cmd, "stop_nm", 4), QaBool(cmd, "away", false));
            string hold = "off";
            if (QaBool(cmd, "hold_fire", true)) hold = QaHold(enemy);
            string bad = QaIsOk(PlaceWaypoint(id, enemy, pt.Lat, pt.Lon, false, ""));
            if (bad != null) return bad;
            return Ok(id, "\"uid\":" + J.Num(D(Get(enemy, QaForceSpecs.ObjUniqueId)) ?? -1) + ",\"name\":" + J.Str(NameOf(enemy) ?? "") + ",\"to_uid\":" + J.Num(UidOf(helo))
                + ",\"dist_nm\":" + J.Num(Math.Round(dist, 1)) + "," + QaPt(pt) + ",\"tracked\":" + J.Bool(RrMenuTrack(enemy) >= 0) + ",\"hold_fire\":" + J.Str(hold));
        }

        /// <summary>Weapons hold of a hostile aircraft: CeaseFire, then SetWeaponStatus(Hold) as RoeCmd does. "unsupported" = a member is missing or the call failed (the route order is given anyway).</summary>
        private string QaHold(object enemy)
        {
            try
            {
                var et = TypeOf(QaForceSpecs.WeaponStatusType);
                if (et == null || !Present(CapSpecs.ObjSetWeaponStatus)) return "unsupported";
                var hold = Enum.Parse(et, "Hold");
                if (Present(CapSpecs.ObjCeaseFire)) CallOpt(enemy, CapSpecs.ObjCeaseFire, false);
                CallOpt(enemy, CapSpecs.ObjSetWeaponStatus, hold, true);
                return "ok";
            }
            catch { return "unsupported"; }
        }
    }
}
