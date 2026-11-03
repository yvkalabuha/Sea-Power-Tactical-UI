using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-079: attack orders refuse to fire when a neutral surface track lies in the seeker corridor of the target
    /// (geometry: TacticsMath.NeutralsNearAttack) unless the order carries confirm_neutral:true. Main thread only.
    /// </summary>
    internal sealed partial class GameApi
    {
        public double NeutralSeekerLegNm = 10.0; // Plugin.cs binds [Attack] NeutralSeekerLegNm

        private sealed class NeutralHit { public int Id; public string Cls; public string Side; public double DistNm; }

        private void SelfCheckNeutralGuard()
        {
            bool ok = Capabilities.Contains("attack") && Capabilities.Contains("contacts");
            ok &= Has("attack_neutral_guard", "SeaPower.Vehicle", "CurrentRelationship", false, true);
            ok &= Has("attack_neutral_guard", "SeaPower.Vehicle", "Object", false, true);
            ok &= Has("attack_neutral_guard", "SeaPower.Vehicle", "Id", false, true);
            if (ok) Capabilities.Add("attack_neutral_guard");
        }

        /// <summary>Neutral and unknown surface tracks of the player's table (not gone) except exceptVehicle: positions and vehicles.</summary>
        private void NeutralTracks(object exceptVehicle, List<double[]> pos, List<object> veh, List<string> side)
        {
            var vehicles = Vehicles();
            if (vehicles == null) return;
            foreach (var v in vehicles)
            {
                if (ReferenceEquals(v, exceptVehicle)) continue;
                string rel = Relation(v);
                if ((rel != "neutral" && rel != "unknown") || Kind(v) != "Surface" || IsGone(v)) continue;
                var p = TrackPos(v); if (p == null) continue;
                pos.Add(p); veh.Add(v); side.Add(rel);
            }
        }

        /// <summary>Like NeutralTracks, but surface and subsurface tracks, with the domain ("Surface"|"Subsurface") of each (T-102 I1, used by the interface mod's smart attack).
        /// Positions are the track positions of the player's table; exceptTrackId (-1 = none) is skipped by the track Id.</summary>
        internal void NeutralTracksDomain(int exceptTrackId, List<double[]> pos, List<object> veh, List<string> side, List<string> domain)
        {
            var vehicles = Vehicles();
            if (vehicles == null) return;
            foreach (var v in vehicles)
            {
                try
                {
                    if ((int)(D(Get(v, "Id")) ?? -1) == exceptTrackId) continue;
                    string rel = Relation(v);
                    if (rel != "neutral" && rel != "unknown") continue;
                    string kind = Kind(v);
                    if (kind != "Surface" && kind != "Submarine") continue;
                    if (IsGone(v)) continue;
                    var p = TrackPos(v); if (p == null) continue;
                    pos.Add(p); veh.Add(v); side.Add(rel); domain.Add(kind == "Surface" ? "Surface" : "Subsurface");
                }
                catch { }
            }
        }

        /// <summary>Neutral/unknown tracks near the attack corridor (sorted by distance to the target, at most 5). Air targets are not checked.
        /// failure = null when the check was done; "no_position" or an exception type name when it could not be (fail closed).</summary>
        private List<NeutralHit> NeutralNearHits(object shooter, object targetVehicle, out string failure)
        {
            failure = null;
            var hits = new List<NeutralHit>();
            try
            {
                if (shooter == null || targetVehicle == null || DomainOf(Get(targetVehicle, "Object")) == null) return hits;
                double lat, lon, h;
                if (!OwnPos(shooter, out lat, out lon, out h)) { failure = "no_position"; return hits; }
                var tp = TrackPos(targetVehicle); if (tp == null) { failure = "no_position"; return hits; }
                var pos = new List<double[]>(); var veh = new List<object>(); var side = new List<string>();
                NeutralTracks(targetVehicle, pos, veh, side);
                var rows = TacticsMath.NeutralsNearAttack(new[] { lat, lon }, tp, pos, VetoRadiusNm, NeutralSeekerLegNm, VetoRadiusNm);
                foreach (var r in rows)
                {
                    if (hits.Count >= 5) break;
                    int k = (int)r[0];
                    var v = veh[k];
                    hits.Add(new NeutralHit { Id = (int)(D(Get(v, "Id")) ?? -1), Cls = Get(Get(v, "Class"), "Value") as string, Side = side[k], DistNm = Math.Round(r[1], 2) });
                }
            }
            catch (Exception e) { failure = e.GetType().Name; Log("neutral guard failed: " + e); }
            return hits;
        }

        private static string NeutralHitsJson(List<NeutralHit> hits)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < hits.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"track_id\":").Append(hits[i].Id).Append(",\"class\":").Append(Json.Str(hits[i].Cls)).Append(",\"side\":").Append(Json.Str(hits[i].Side == "unknown" ? "unknown" : "neutral"))
                  .Append(",\"distance_nm\":").Append(Json.Num(hits[i].DistNm)).Append('}');
            }
            return sb.Append(']').ToString();
        }

        private static bool ConfirmedNeutral(Dictionary<string, object> cmd)
        {
            return cmd.ContainsKey("confirm_neutral") && cmd["confirm_neutral"] is bool && (bool)cmd["confirm_neutral"];
        }

        /// <summary>The neutral_near / neutral_check_failed error ack, or null when the order may proceed (clear, or confirm_neutral:true).</summary>
        private string NeutralGuardError(string id, Dictionary<string, object> cmd, object shooter, object targetVehicle, out string hitsJson)
        {
            string failure; var hits = NeutralNearHits(shooter, targetVehicle, out failure);
            hitsJson = NeutralHitsJson(hits);
            int tid = (int)(D(Get(targetVehicle, "Id")) ?? -1);
            if (failure != null) return ErrData(id, "neutral_check_failed", failure, "\"target_id\":" + tid);
            if (hits.Count == 0 || ConfirmedNeutral(cmd)) return null;
            string detail = (hits[0].Side == "unknown" ? "unknown " : "neutral ") + hits[0].Id + " " + Json.Num(hits[0].DistNm) + " nm from target " + tid;
            string data = "\"target_id\":" + tid + ",\"target_class\":" + Json.Str(Get(Get(targetVehicle, "Class"), "Value") as string) +
                          ",\"neutral_near\":" + NeutralHitsJson(hits) + ",\"veto_radius_nm\":" + Json.Num(VetoRadiusNm);
            return ErrData(id, "neutral_near", detail, data);
        }

        /// <summary>Nearest hostile surface track to the shooter without a neutral near its corridor (else the nearest); skippedIds = closer tracks rejected.</summary>
        private object PickNearestHostile(object shooter, double lat0, double lon0, out List<int> skippedIds)
        {
            skippedIds = new List<int>();
            try { return PickNearestHostileCore(lat0, lon0, skippedIds); }
            catch (Exception e)
            {
                Log("neutral guard failed (pick): " + e);
                skippedIds.Clear();
                return NearestHostileUnchecked(lat0, lon0);
            }
        }

        private object NearestHostileUnchecked(double lat0, double lon0)
        {
            object best = null; double bd = double.MaxValue;
            var vehicles = Vehicles();
            if (vehicles == null) return null;
            foreach (var c in vehicles)
            {
                if (Relation(c) != "hostile" || Kind(c) != "Surface" || IsGone(c)) continue;
                var p = TrackPos(c); if (p == null) continue;
                double d = (p[0] - lat0) * (p[0] - lat0) + (p[1] - lon0) * (p[1] - lon0);
                if (d < bd) { bd = d; best = c; }
            }
            return best;
        }

        private object PickNearestHostileCore(double lat0, double lon0, List<int> skippedIds)
        {
            var cands = new List<double[]>(); var cveh = new List<object>();
            var vehicles = Vehicles();
            if (vehicles != null)
                foreach (var c in vehicles)
                {
                    if (Relation(c) != "hostile" || Kind(c) != "Surface" || IsGone(c)) continue;
                    var p = TrackPos(c); if (p == null) continue;
                    cands.Add(p); cveh.Add(c);
                }
            if (cands.Count == 0) return null;
            var npos = new List<double[]>(); var nveh = new List<object>();
            var nside = new List<string>();
            NeutralTracks(null, npos, nveh, nside);
            var skipped = new List<int>();
            int idx = TacticsMath.PickNearestClear(new[] { lat0, lon0 }, cands, npos, VetoRadiusNm, NeutralSeekerLegNm, VetoRadiusNm, skipped);
            if (idx < 0) return null;
            foreach (int s in skipped) skippedIds.Add((int)(D(Get(cveh[s], "Id")) ?? -1));
            return cveh[idx];
        }
    }
}
