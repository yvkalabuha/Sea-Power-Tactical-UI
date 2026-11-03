using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 section 7: when a plan is out of date, and what to do with a launch whose target contact is lost. Pure.
    internal static class StrikeStaleMath
    {
        private static double AngDiff(double a, double b)
        {
            double d = Math.Abs(AswMath.Norm360(a) - AswMath.Norm360(b));
            return d > 180 ? 360 - d : d;
        }

        /// <summary>null (fresh) | target_lost | target_moved | target_turned | new_ad_ship | aircraft_lost | missiles_lost. Checked in this order; now = mission time.</summary>
        public static string Why(StrikeSnapshot snap, IList<StrikeShip> ships, IList<StrikeAircraft> air, StrikeParams p, double now)
        {
            if (snap == null) return null;
            var byId = new Dictionary<int, StrikeShip>();
            if (ships != null) foreach (var s in ships) if (s != null) byId[s.TrackId] = s;
            double dt = Math.Max(0, now - snap.T);
            foreach (var t in snap.Targets)
            {
                StrikeShip cur;
                if (!byId.TryGetValue(t.Id, out cur) || cur.Excluded) return "target_lost";
            }
            foreach (var t in snap.Targets)
            {
                var cur = byId[t.Id];
                var pred = StrikeGeoMath.Lead(new StrikeShip { Lat = t.Lat, Lon = t.Lon, CourseDeg = t.CourseDeg, SpeedKn = t.SpeedKn }, dt);
                if (AswMath.DistNm(pred, new GeoPt(cur.Lat, cur.Lon)) > p.StaleNm) return "target_moved";
                if (cur.SpeedKn > 3 && t.SpeedKn > 3 && AngDiff(cur.CourseDeg, t.CourseDeg) > p.StaleTurnDeg) return "target_turned";
            }
            if (ships != null && snap.Targets.Count > 0)
            {
                var main = snap.Targets[0];
                foreach (var s in ships)
                {
                    if (s == null || s.Excluded || s.Civilian || !s.Hostile || !s.Identified || s.Ad.Ksub <= 0) continue;
                    if (snap.AdShipIds.Contains(s.TrackId) || SnapHas(snap, s.TrackId)) continue;
                    if (AswMath.DistNm(new GeoPt(main.Lat, main.Lon), new GeoPt(s.Lat, s.Lon)) <= Math.Max(s.SamMaxNm, 30) + p.StandoffNm) return "new_ad_ship";
                }
            }
            var airById = new Dictionary<int, StrikeAircraft>();
            if (air != null) foreach (var a in air) if (a != null) airById[a.Uid] = a;
            foreach (var sa in snap.Air)
            {
                StrikeAircraft a;
                if (!airById.TryGetValue(sa.Uid, out a) || !a.Airborne) return "aircraft_lost";
            }
            foreach (var sa in snap.Air)
                if (StrikeAirMath.UsableCount(airById[sa.Uid]) < sa.Missiles) return "missiles_lost";
            return null;
        }

        private static bool SnapHas(StrikeSnapshot snap, int id) { foreach (var t in snap.Targets) if (t.Id == id) return true; return false; }

        /// <summary>go (contact live) | wait (lost not long ago) | skip (lost for more than holdS), via AswStrikeMath.HeldLive.</summary>
        public static string Held(bool liveNow, double fixS, double nowS, double holdS)
        {
            if (liveNow) return "go";
            return AswStrikeMath.HeldLive(false, fixS, nowS, holdS) ? "wait" : "skip";
        }

        public static string SelfTest()
        {
            var p = new StrikeParams();
            var tgt = new StrikeShip { TrackId = 7, Lat = 41, Lon = 10, CourseDeg = 90, SpeedKn = 20, Identified = true, Hostile = true };
            var snap = new StrikeSnapshot { T = 1000 };
            snap.Targets.Add(new StrikeSnapTarget { Id = 7, Lat = 41, Lon = 10, CourseDeg = 90, SpeedKn = 20 });
            snap.Air.Add(new StrikeSnapAir { Uid = 1, Missiles = 4 });
            snap.Air.Add(new StrikeSnapAir { Uid = 2, Missiles = 2 });
            Func<StrikeShip> cur = () => new StrikeShip { TrackId = 7, Lat = 41, Lon = 10 + 20.0 / 3600 * 600 / 60, CourseDeg = 90, SpeedKn = 20, Identified = true, Hostile = true };   // 600 s on its course
            Func<int, int, StrikeAircraft> ac = (uid, n) =>
            {
                var a = new StrikeAircraft { Uid = uid, Airborne = true };
                a.Missiles.Add(new StrikeMissile { Ammo = "x", Type = "Missile", TargetType = "ASuW", Guidance = "arh", MaxNm = 60, Count = n, Operable = true });
                return a;
            };
            Func<IList<StrikeAircraft>> airs = () => new List<StrikeAircraft> { ac(1, 4), ac(2, 2) };
            var ships = new List<StrikeShip> { cur() };
            string w = Why(snap, ships, airs(), p, 1600);
            if (w != null) return "fresh plan is " + w;
            // the target went away more than 3 nm from the lead
            var moved = cur(); moved.Lat += 5.0 / 60;
            if ((w = Why(snap, new List<StrikeShip> { moved }, airs(), p, 1600)) != "target_moved") return "moved " + w;
            // the target turned more than 30 degrees (same position)
            var turned = cur(); turned.CourseDeg = 140;
            if ((w = Why(snap, new List<StrikeShip> { turned }, airs(), p, 1600)) != "target_turned") return "turned " + w;
            var slow = cur(); slow.CourseDeg = 200; slow.SpeedKn = 1;
            snap.Targets[0].SpeedKn = 20;
            if ((w = Why(snap, new List<StrikeShip> { slow }, airs(), p, 1600)) == "target_turned") return "a stopped ship has no course";
            // lost contact
            if ((w = Why(snap, new List<StrikeShip>(), airs(), p, 1600)) != "target_lost") return "lost " + w;
            var excluded = cur(); excluded.Excluded = true;
            if ((w = Why(snap, new List<StrikeShip> { excluded }, airs(), p, 1600)) != "target_lost") return "excluded " + w;
            // a new ship with AA near the target
            var nw = new StrikeShip { TrackId = 9, Lat = 41.1, Lon = 10.05, Identified = true, Hostile = true, SamMaxNm = 40, Ad = new StrikeAd { Ksub = 5, Ssub = 15, Source = "cache" } };
            if ((w = Why(snap, new List<StrikeShip> { cur(), nw }, airs(), p, 1600)) != "new_ad_ship") return "new ship " + w;
            snap.AdShipIds.Add(9);
            if ((w = Why(snap, new List<StrikeShip> { cur(), nw }, airs(), p, 1600)) != null) return "known ship " + w;
            var civ = new StrikeShip { TrackId = 11, Lat = 41.1, Lon = 10.05, Identified = true, Hostile = true, Civilian = true, SamMaxNm = 40, Ad = new StrikeAd { Ksub = 5, Ssub = 15 } };
            if ((w = Why(snap, new List<StrikeShip> { cur(), nw, civ }, airs(), p, 1600)) != null) return "civilian is no new ad ship " + w;
            // aircraft: gone / on the deck / missiles spent
            if ((w = Why(snap, ships, new List<StrikeAircraft> { ac(1, 4) }, p, 1600)) != "aircraft_lost") return "aircraft lost " + w;
            var down = airs(); down[1].Airborne = false;
            if ((w = Why(snap, ships, down, p, 1600)) != "aircraft_lost") return "aircraft landed " + w;
            if ((w = Why(snap, ships, new List<StrikeAircraft> { ac(1, 3), ac(2, 2) }, p, 1600)) != "missiles_lost") return "missiles " + w;
            if (Why(null, ships, airs(), p, 1600) != null) return "no snapshot";
            // held
            if (Held(true, -1, 500, 60) != "go") return "held live";
            if (Held(false, 470, 500, 60) != "wait") return "held wait";
            if (Held(false, 400, 500, 60) != "skip") return "held skip";
            if (Held(false, -1, 500, 60) != "skip") return "held unknown";
            return null;
        }
    }
}
