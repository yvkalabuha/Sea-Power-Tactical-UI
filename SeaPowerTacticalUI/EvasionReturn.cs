using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-278 (ADR-0111): the way back to the player's route after an own torpedo evasion. EvasionFinish gives the route back (restore_control); if the straight
    // segment to the first route point crosses land or a shoal (the ray of ADR-0108), 1-2 detour points are put before the player's points and followed
    // until they are passed. The pure part is EvasionReturnGuard. No Harmony and no Unity types. Main thread only.
    internal sealed partial class GameApi
    {
        private readonly Dictionary<object, EvDetour> _evDetours = new Dictionary<object, EvDetour>(new RefEq());

        private static string EvF(double v, string fmt) { return double.IsNaN(v) ? "na" : double.IsInfinity(v) ? "inf" : v.ToString(fmt, CultureInfo.InvariantCulture); }

        private int EvasionSamples() { return _evHeights == null ? 0 : _evHeights.Misses; }

        /// <summary>The unit is steered along a detour of the mod (its first points are the mod's, not the player's).</summary>
        internal bool EvasionDetourActive(object u) { return u != null && _evDetours.ContainsKey(u); }

        /// <summary>Waypoints(u) without the mod's detour points still at the head of the list; the player's route as the other modules should see it.</summary>
        internal List<double[]> PlayerWaypoints(object u)
        {
            var wps = Waypoints(u);
            EvDetour d;
            if (u == null || !_evDetours.TryGetValue(u, out d)) return wps;
            return EvasionReturnGuard.StripDetour(wps, d);
        }

        private void EvasionReturnClear() { _evDetours.Clear(); }

        private void EvasionReturnDrop(object u, string reason)
        {
            EvDetour d;
            if (u == null || !_evDetours.TryGetValue(u, out d)) return;
            _evDetours.Remove(u);
            double? mt = Frame.MissionTimeS;
            try { Log("[Evasion] evasion return detour end uid=" + d.Uid + " reason=" + reason + " after=" + (mt.HasValue ? Math.Round(mt.Value - d.PlacedS).ToString(CultureInfo.InvariantCulture) : "na")); } catch { }
        }

        private bool EvasionReturnPlace(object u, List<double[]> pts)
        {
            bool ok = true;
            for (int k = 0; k < pts.Count; k++)
            {
                try
                {
                    string r = PlaceWaypoint("0", u, pts[k][0], pts[k][1], k > 0, "");
                    if (!r.Contains("\"ok\":true")) ok = false;
                }
                catch { ok = false; }
            }
            return ok;
        }

        /// <summary>From EvasionFinish after restore_control: checks the segment to the first player's point and places a detour when it is blocked. Never throws.</summary>
        private void EvasionReturnAfterRestore(object u, double now, string ack)
        {
            int uid = 0;
            try
            {
                uid = UidOf(u);
                EvasionReturnDrop(u, "evasion");
                string action = "na";
                int ai = ack == null ? -1 : ack.IndexOf("\"route\":{\"action\":\"", StringComparison.Ordinal);
                if (ai >= 0)
                {
                    int s0 = ai + "\"route\":{\"action\":\"".Length;
                    int e0 = ack.IndexOf('"', s0);
                    if (e0 > s0) action = ack.Substring(s0, e0 - s0);
                }
                if (action != "restore")
                {
                    if (EvasionDebug) Log("[Evasion] evasion return skip uid=" + uid + " route=" + action);
                    return;
                }
                double lat0, lon0, h;
                if (!OwnPos(u, out lat0, out lon0, out h)) return;
                var own = new GeoPt(lat0, lon0);
                var wps = Waypoints(u);
                int i = EvasionReturnGuard.TargetIndex(own, wps);
                if (i < 0) return;
                var target = new GeoPt(wps[i][0], wps[i][1]);
                int missBefore = EvasionSamples();
                EvReturnPlan plan = EvasionReturnGuard.Plan(own, target, EvasionHeightM);
                int samples = EvasionSamples() - missBefore;
                string head = " uid=" + uid + " wp=" + i;
                if (plan.Why == "clear")
                {
                    Log("[Evasion] evasion return clear" + head + " direct_nm=" + EvF(plan.DirectNm, "0.0") + " probes=" + plan.Probes + " samples=" + samples);
                    return;
                }
                if (plan.Why != "detour")
                {
                    Log("[Evasion] evasion return land" + head + " why=best_effort block_nm=" + EvF(plan.BlockNm, "0.00") + " alt=" + EvF(plan.BlockAltM, "0") +
                        " probes=" + plan.Probes + " samples=" + samples + " budget=" + (plan.Budget ? 1 : 0));
                    return;
                }
                var player = new List<double[]>();
                for (int j = i; j < wps.Count; j++) player.Add(wps[j]);
                var all = new List<double[]>();
                foreach (var g in plan.Points) all.Add(new[] { g.Lat, g.Lon });
                all.AddRange(player);
                if (!EvasionReturnPlace(u, all))
                {
                    EvasionReturnPlace(u, wps);   // back to the route as restore_control gave it
                    Log("[Evasion] evasion return error" + head + " place failed, route kept");
                    return;
                }
                var d = new EvDetour { Uid = uid, PlacedS = now, NextCheckS = now + EvasionReturnGuard.RecheckS, NextTickS = now + 1, PlayerRoute = player };
                double? kn = null; try { kn = D(Call(u, EvasionSpecs.ObjVelocityKn)); } catch { }
                d.TimeoutS = EvasionReturnGuard.TimeoutFor(plan.PathNm, kn.HasValue ? kn.Value : double.NaN);
                foreach (var g in plan.Points) d.Points.Add(new[] { g.Lat, g.Lon });
                _evDetours[u] = d;
                Log("[Evasion] evasion return detour" + head + " pts=" + plan.Points.Count + " at=" + plan.Points[0].Lon.ToString("0.0000", CultureInfo.InvariantCulture) + "/" +
                    plan.Points[0].Lat.ToString("0.0000", CultureInfo.InvariantCulture) + " block_nm=" + EvF(plan.BlockNm, "0.00") + " alt=" + EvF(plan.BlockAltM, "0") +
                    " direct_nm=" + EvF(plan.DirectNm, "0.0") + " path_nm=" + EvF(plan.PathNm, "0.0") + " probes=" + plan.Probes + " samples=" + samples);
            }
            catch (Exception e) { try { Log("[Evasion] evasion return error uid=" + uid + " " + e.GetType().Name + ": " + e.Message); } catch { } }
        }

        /// <summary>From AutoEvadeHook before EvasionTick: follows the running detour of the ship (1 s of mission time). Never throws.</summary>
        private void EvasionReturnTick(object u, double realNow)
        {
            EvDetour d;
            if (_evDetours.Count == 0 || !_evDetours.TryGetValue(u, out d)) return;
            try
            {
                double? mt = Frame.MissionTimeS;
                if (!mt.HasValue) return;
                double now = mt.Value;
                if (now < d.PlacedS - 0.5) { EvasionReturnDrop(u, "load"); return; }
                if (now < d.NextTickS) return;
                d.NextTickS = now + 1;
                if (!IsAlive(u)) { _evDetours.Remove(u); return; }
                if (_evRuns.ContainsKey(u)) { EvasionReturnDrop(u, "evasion"); return; }
                double lat0, lon0, h;
                if (!OwnPos(u, out lat0, out lon0, out h)) return;
                var own = new GeoPt(lat0, lon0);
                var wps = Waypoints(u);
                string s = EvasionReturnGuard.Step(d, own, wps, now, (a, b) =>
                {
                    double alt; int pr;
                    return double.IsPositiveInfinity(EvasionReturnGuard.SegmentBlockNm(a, b, EvasionHeightM, out alt, out pr));
                });
                switch (s)
                {
                    case "keep":
                    case "advance":
                        return;
                    case "restack":
                    {
                        var rest = new List<double[]>(d.Points.GetRange(1, d.Points.Count - 1));
                        var strip = EvasionReturnGuard.StripDetour(wps, d);
                        rest.AddRange(strip);
                        if (!EvasionReturnPlace(u, rest)) EvasionReturnPlace(u, rest);   // RemoveWaypoints came first: one retry so the route is not left cut
                        d.Points.RemoveAt(0);
                        return;
                    }
                    case "shortcut":
                    case "shortcut:passed":
                    {
                        var strip = EvasionReturnGuard.StripDetour(wps, d);
                        if (strip.Count == 0)
                        {
                            int ti = EvasionReturnGuard.TargetIndex(own, d.PlayerRoute);
                            if (ti >= 0) strip = d.PlayerRoute.GetRange(ti, d.PlayerRoute.Count - ti);
                        }
                        if (strip.Count > 0 && !EvasionReturnPlace(u, strip)) EvasionReturnPlace(u, strip);
                        EvasionReturnDrop(u, s == "shortcut" ? "clear" : "passed");
                        return;
                    }
                    default:
                        EvasionReturnDrop(u, s.StartsWith("end:", StringComparison.Ordinal) ? s.Substring(4) : s);
                        return;
                }
            }
            catch (Exception e) { try { Log("[Evasion] evasion return tick error uid=" + d.Uid + " " + e.GetType().Name + ": " + e.Message); } catch { } _evDetours.Remove(u); }
        }
    }
}
