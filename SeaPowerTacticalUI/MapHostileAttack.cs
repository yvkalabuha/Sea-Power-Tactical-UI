using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // 0.27.10: attack on a track the main map paints red while its relationship still reads "unknown" (live: ASW contact
    // side=unknown attack_ok=true -> proposal -> Yes -> Common Attack refused with not_hostile). The shared Attack in Common/
    // checks CurrentRelationship only and is not changed; the UI mod decides hostility by AttackHostility and, for the "map"
    // route, runs the same steps through Common helpers only (no game member is named here, T-120): ReadTrack (side + map
    // posture), NeutralGuardError, WeaponRows (asw ammo, count, operable mount, launch range), EngageAntiTorpedo (the plain
    // EngageTask + AddEngageTask, no hostile check of its own).
    internal sealed partial class GameApi
    {
        /// <summary>"relation" | "map" | "not_hostile" | "unknown_target" for a track of the player's plotting table.</summary>
        internal string AswAttackRoute(int contactId, bool unknownOk = false)
        {
            try
            {
                var v = FindContact(contactId);
                if (v == null || IsGone(v)) return "unknown_target";
                return RouteOf(v, unknownOk);
            }
            catch (Exception) { return "not_hostile"; }
        }

        private string RouteOf(object v, bool unknownOk = false)
        {
            var t = ReadTrack(v, true, Clock());
            string r = AttackHostility.Route(t.Side, AttackHostility.MapHostile(t.MapPosture, t.VmPosture));
            // T-173 (ADR-0074): an unidentified track the player did not mark neutral / friendly may be attacked by the strike group through the same engage path
            if (r == "not_hostile" && unknownOk && AttackHostility.StrikeOk(t.Side, t.MapPosture, t.VmPosture, "hostile_unknown") == null) return "map";
            return r;
        }

        /// <summary>The main map shows this track red (diag "map_hostile").</summary>
        internal bool MapHostileOf(object v)
        {
            try { var t = ReadTrack(v, true, Clock()); return AttackHostility.MapHostile(t.MapPosture, t.VmPosture); }
            catch (Exception) { return false; }
        }

        /// <summary>null = the EngageTask is queued; otherwise the error code (same codes as Attack).</summary>
        internal string AttackMapHostile(Dictionary<string, object> cmd, int contactId, string category, bool unknownOk = false)
        {
            try
            {
                if (!Capabilities.Contains("attack")) return "no_capability";
                if (!AllowAttack) return "attack_disabled";
                if (!Frame.InMission) return "not_in_session";
                var uidD = cmd.ContainsKey("uid") ? D(cmd["uid"]) : null;
                var u = uidD.HasValue ? FindOwnUnit((int)uidD.Value) : null;
                if (u == null) return "unit_not_found";
                var v = FindContact(contactId);
                if (v == null || IsGone(v)) return "unknown_target";
                if (RouteOf(v, unknownOk) == "not_hostile") return "not_hostile";   // re-checked on the same frame as the order

                // neutral guard (T-079): unchanged rule, a neutral surface track in the seeker corridor refuses the order
                string neutralJson;
                string guardAck = NeutralGuardError("null", cmd, u, v, out neutralJson);
                if (guardAck != null)
                {
                    var g = Json.Parse(guardAck) as Dictionary<string, object>;
                    return g != null && g.ContainsKey("error") ? g["error"] as string : "neutral_near";
                }

                double lat0, lon0, h;
                var tp = TrackPos(v);
                if (tp == null || !OwnPos(u, out lat0, out lon0, out h)) return "no_position";
                double dist = WorldDistanceNm(lat0, lon0, tp[0], tp[1]);

                // weapon rows: [file, display, type, target, category, count, rangeNm, canEngage, ...]
                string chosen = null; int have = 0; double maxRange = 0;
                var allRows = WeaponRows(u);
                // T-172: for an ASW attack a torpedo / ASROC is looked at before a depth charge / RBU (two passes over the rows)
                var ordered = new List<string[]>();
                if (category == "asw") { foreach (var r in allRows) if (r[2] == "Torpedo" || r[2] == "ASROC") ordered.Add(r); foreach (var r in allRows) if (!(r[2] == "Torpedo" || r[2] == "ASROC")) ordered.Add(r); }
                else ordered.AddRange(allRows);
                foreach (var r in ordered)
                {
                    if (r[4] != category) continue;
                    int count = int.Parse(r[5]);
                    have += count;
                    if (count <= 0 || r[7] != "true") continue;
                    double rng;
                    if (!double.TryParse(r[6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rng))
                    {
                        if (chosen == null) chosen = r[0];   // no launch range in the parameters: the game decides
                        continue;
                    }
                    maxRange = Math.Max(maxRange, rng);
                    if (rng >= dist && chosen == null) chosen = r[0];
                }
                if (have == 0) return "no_ammo";
                if (chosen == null) return maxRange > 0 && maxRange < dist ? "out_of_range" : "no_weapon";

                AtwShot shot; string err;
                if (!EngageAntiTorpedo(u, v, chosen, 1, out shot, out err)) return err == "no_ammo" ? "no_ammo" : "attack_failed";
                try { Log("attack map_hostile target=" + contactId + " rel=" + Relation(v) + " weapon=" + chosen + " dist_nm=" + Math.Round(dist, 2)); } catch { }
                return null;
            }
            catch (Exception e)
            {
                try { Log("attack map_hostile failed: " + e.Message); } catch { }
                return "attack_failed";
            }
        }
    }
}
