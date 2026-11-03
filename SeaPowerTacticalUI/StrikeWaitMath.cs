using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 phase B: the wait point of a strike group (over the sea, outside every known hostile SAM zone plus a margin), the shift of a launch point off the land,
    // and the fuel rule of the group (the port of the T-337 rule of the CAP: the spare path in miles). Pure: no Unity, no game objects.
    internal static class StrikeWaitMath
    {
        private const double SearchMaxNm = 400, HardFloorFrac = 0.10;

        private static GeoPt Pos(StrikeShip s) { return new GeoPt(s.Lat, s.Lon); }

        /// <summary>Every known hostile ship keeps the wait point away: a ship with a SAM by its zone (SamMaxNm), a ship without one (a missile boat, an unidentified contact) by the margin alone.</summary>
        private static List<StrikeShip> Rings(IList<StrikeShip> hostiles)
        {
            var res = new List<StrikeShip>();
            if (hostiles != null) foreach (var s in hostiles) if (s != null) res.Add(s);
            return res;
        }

        private static double Ring(StrikeShip s) { return Math.Max(0, s.SamMaxNm); }

        /// <summary>The first point on the reverse axis from the anchor that is outside every ring (SamMaxNm + standoff + orbit) of the ships; true = found within the search range.</summary>
        private static bool Along(GeoPt anchor, double axisDeg, IList<StrikeShip> sams, StrikeParams p, out double dNm)
        {
            double margin = p.StandoffNm + p.WaitOrbitNm;
            for (double d = 0; d <= SearchMaxNm; d += 1)
            {
                var pt = AswMath.Offset(anchor, axisDeg + 180, d);
                bool ok = true;
                foreach (var s in sams) if (AswMath.DistNm(pt, Pos(s)) < Ring(s) + margin) { ok = false; break; }
                if (ok) { dNm = d; return true; }
            }
            dNm = SearchMaxNm;
            return false;
        }

        /// <summary>
        /// The wait point: on the reverse axis from the target (from the centre of the hostile ships when there is no target) so that every known hostile ship is at least
        /// SamMaxNm (0 without a SAM) + standoff + orbit away. Over land the point goes farther along the axis (up to LandMaxShiftNm), then the axis turns by +-15..+-90 degrees.
        /// why: ok | land_shifted | rotated | no_sea (the first point outside the zones is returned) | no_point (no point outside the zones within the search range: = from) | no_hostiles (= from).
        /// </summary>
        public static GeoPt WaitPoint(IList<StrikeShip> hostiles, GeoPt? target, GeoPt from, double axisDeg, StrikeParams p, Func<GeoPt, bool> isLand, out string why)
        {
            why = "no_hostiles";
            if (hostiles == null || hostiles.Count == 0) return from;
            GeoPt anchor;
            if (target.HasValue) anchor = target.Value;
            else
            {
                double la = 0, lo = 0;
                foreach (var s in hostiles) { la += s.Lat; lo += s.Lon; }
                anchor = new GeoPt(la / hostiles.Count, lo / hostiles.Count);
            }
            var sams = Rings(hostiles);
            double d0;
            if (!Along(anchor, axisDeg, sams, p, out d0)) { why = "no_point"; return from; }   // review of phase B: no solution within the search range is not a point
            var first = AswMath.Offset(anchor, axisDeg + 180, d0);
            why = "ok";
            if (isLand == null || !isLand(first)) return first;
            // over land: farther along the axis, then turn the axis
            double[] turns = { 0, 15, -15, 30, -30, 45, -45, 60, -60, 75, -75, 90, -90 };
            foreach (double turn in turns)
            {
                double ax = AswMath.Norm360(axisDeg + turn), d;
                if (!Along(anchor, ax, sams, p, out d)) continue;
                for (double k = 0; k <= Math.Max(0, p.LandMaxShiftNm); k += Math.Max(0.5, p.LandStepNm))
                {
                    var pt = AswMath.Offset(anchor, ax + 180, d + k);
                    if (!isLand(pt)) { why = turn == 0 ? "land_shifted" : "rotated"; return pt; }
                }
            }
            why = "no_sea";
            return first;
        }

        /// <summary>A hostile SAM (a new ship or one that came closer) now covers the wait point: SamMaxNm + standoff (the orbit margin of the point is the hysteresis).</summary>
        public static bool WaitUnsafe(GeoPt wait, IList<StrikeShip> hostiles, StrikeParams p)
        {
            foreach (var s in Rings(hostiles)) if (AswMath.DistNm(wait, Pos(s)) < Ring(s) + p.StandoffNm) return true;
            return false;
        }

        /// <summary>The launch point over land goes toward the target along the axis, one step at a time, not closer than the minimum range of the missile. stillLand = no sea point found (the point is kept).</summary>
        public static GeoPt LaunchOffLand(GeoPt launch, GeoPt target, double minNm, Func<GeoPt, bool> isLand, double stepNm, out bool moved, out bool stillLand)
        {
            moved = false; stillLand = false;
            if (isLand == null || !isLand(launch)) return launch;
            double total = AswMath.DistNm(launch, target), brg = AswMath.BearingDeg(launch, target);
            for (double d = Math.Max(0.25, stepNm); total - d >= Math.Max(0, minNm); d += Math.Max(0.25, stepNm))
            {
                var pt = AswMath.Offset(launch, brg, d);
                if (!isLand(pt)) { moved = true; return pt; }
            }
            stillLand = true;
            return launch;
        }

        /// <summary>
        /// null | fuel_margin | fuel_floor | fuel_hard: the T-337 rule of the CAP in miles. A live game fraction: the floor (if configured), then the spare path against FuelMarginNm
        /// (an unknown path: the hard floor of 10 %); no live fraction: the path against the larger of FuelMarginNm and 5 % of the full range; nothing known = null.
        /// </summary>
        public static string FuelLeave(StrikeAircraft a, StrikeParams p)
        {
            if (a == null) return null;
            bool frac = a.FuelFracLive && !double.IsNaN(a.FuelFrac) && a.FuelFrac >= 0 && a.FuelFrac <= 1;
            bool reserve = a.MaxRangeNm > 0 && !double.IsNaN(a.MaxRangeNm) && a.ReserveNm >= 0 && !double.IsNaN(a.ReserveNm) && !double.IsInfinity(a.ReserveNm);
            if (frac)
            {
                if (p.FuelFloorPct > 0 && a.FuelFrac <= p.FuelFloorPct) return "fuel_floor";
                if (reserve) return a.ReserveNm <= p.FuelMarginNm ? "fuel_margin" : (a.FuelFrac <= 0 ? "fuel_floor" : null);
                return a.FuelFrac <= HardFloorFrac ? "fuel_hard" : null;
            }
            if (reserve) return a.ReserveNm <= Math.Max(p.FuelMarginNm, 0.05 * a.MaxRangeNm) ? "fuel_margin" : null;
            return null;
        }

        private static double Wrap180(double a) { a = AswMath.Norm360(a); return a > 180 ? a - 360 : a; }

        public static string SelfTest()
        {
            var p = new StrikeParams();
            var from = new GeoPt(39, 10);
            var a = new StrikeShip { TrackId = 1, Lat = 41, Lon = 10, SamMaxNm = 25, Side = "hostile", Hostile = true };
            var b = new StrikeShip { TrackId = 2, Lat = 41, Lon = 10.5, SamMaxNm = 40, Side = "hostile", Hostile = true };
            string why;
            var ships = new List<StrikeShip> { a, b };
            var target = new GeoPt(a.Lat, a.Lon);
            double axis = StrikeGeoMath.AxisDeg(from, target);
            var w = WaitPoint(ships, target, from, axis, p, null, out why);
            if (why != "ok") return "wait why " + why;
            if (AswMath.DistNm(w, new GeoPt(a.Lat, a.Lon)) < 25 + 10 + 5 - 0.01 || AswMath.DistNm(w, new GeoPt(b.Lat, b.Lon)) < 40 + 10 + 5 - 0.01) return "wait point inside a zone";
            if (AswMath.DistNm(w, target) > 120) return "wait point too far " + AswMath.DistNm(w, target);
            // a neighbour off the axis counts as well: a cruiser to the side with a long SAM
            var side = new StrikeShip { TrackId = 3, Lat = 40.3, Lon = 9.4, SamMaxNm = 60, Side = "hostile", Hostile = true };
            ships.Add(side);
            w = WaitPoint(ships, target, from, axis, p, null, out why);
            foreach (var s in ships) if (AswMath.DistNm(w, new GeoPt(s.Lat, s.Lon)) < s.SamMaxNm + 15 - 0.01) return "wait point inside the zone of ship " + s.TrackId;
            // a ship without a SAM (a missile boat) on the axis still keeps the point away by the margin alone
            var boat = new StrikeShip { TrackId = 4, Lat = w.Lat + 0.02, Lon = w.Lon, SamMaxNm = 0, Side = "hostile", Hostile = true };
            var withBoat = new List<StrikeShip>(ships) { boat };
            var wb = WaitPoint(withBoat, target, from, axis, p, null, out why);
            if (AswMath.DistNm(wb, new GeoPt(boat.Lat, boat.Lon)) < 15 - 0.01) return "wait point too close to a ship without a SAM " + AswMath.DistNm(wb, new GeoPt(boat.Lat, boat.Lon));
            if (!WaitUnsafe(new GeoPt(boat.Lat, boat.Lon + 0.05), new List<StrikeShip> { boat }, p) || WaitUnsafe(new GeoPt(boat.Lat, boat.Lon + 0.4), new List<StrikeShip> { boat }, p)) return "WaitUnsafe for a ship without a SAM";
            // only ships without a SAM: the point is the margin away from the nearest of them
            var only = WaitPoint(new List<StrikeShip> { boat }, new GeoPt(boat.Lat, boat.Lon), from, axis, p, null, out why);
            if (why != "ok" || Math.Abs(AswMath.DistNm(only, new GeoPt(boat.Lat, boat.Lon)) - 15) > 1.01) return "ships without a SAM only " + why + " " + AswMath.DistNm(only, new GeoPt(boat.Lat, boat.Lon));
            // no target: the centre of the hostile ships
            var wc = WaitPoint(ships, null, from, axis, p, null, out why);
            foreach (var s in ships) if (AswMath.DistNm(wc, new GeoPt(s.Lat, s.Lon)) < s.SamMaxNm + 15 - 0.01) return "wait point (no target) inside a zone";
            // no hostile ships
            var wn = WaitPoint(new List<StrikeShip>(), null, from, axis, p, null, out why);
            if (why != "no_hostiles" || wn.Lat != from.Lat || wn.Lon != from.Lon) return "no hostiles";
            // land on the axis: shifted farther along it
            var w0 = WaitPoint(ships, target, from, axis, p, null, out why);
            Func<GeoPt, bool> landNear = pt => AswMath.DistNm(pt, w0) < 3;
            var w1 = WaitPoint(ships, target, from, axis, p, landNear, out why);
            if (why != "land_shifted" || AswMath.DistNm(w1, w0) < 2.9 || landNear(w1)) return "land shifted " + why;
            // all the axis is land: the axis turns
            Func<GeoPt, bool> landBand = pt => Math.Abs(Wrap180(AswMath.BearingDeg(target, pt) - (axis + 180))) < 8 && AswMath.DistNm(pt, target) > 20;
            var w2 = WaitPoint(ships, target, from, axis, p, landBand, out why);
            if (why != "rotated" || landBand(w2)) return "rotated " + why;
            // everything is land
            WaitPoint(ships, target, from, axis, p, pt => true, out why);
            if (why != "no_sea") return "no sea " + why;
            // no point outside the zones within the search range: a huge SAM
            var huge = new StrikeShip { TrackId = 5, Lat = 41, Lon = 10, SamMaxNm = 500, Side = "hostile", Hostile = true };
            var wh = WaitPoint(new List<StrikeShip> { huge }, target, from, axis, p, null, out why);
            if (why != "no_point" || wh.Lat != from.Lat || wh.Lon != from.Lon) return "no point " + why;
            // unsafe: a new SAM covers the point
            if (WaitUnsafe(w0, ships, p)) return "unsafe without a new ship";
            var fresh = new StrikeShip { TrackId = 9, Lat = w0.Lat + 0.2, Lon = w0.Lon, SamMaxNm = 30, Side = "hostile", Hostile = true };
            if (!WaitUnsafe(w0, new List<StrikeShip>(ships) { fresh }, p)) return "unsafe with a new ship";
            // the launch point off the land
            var tg = new GeoPt(41, 10);
            var lp = StrikeGeoMath.LaunchPt(tg, 0, 60);
            bool moved, still;
            var lm = LaunchOffLand(lp, tg, 3.5, pt => AswMath.DistNm(pt, lp) < 4.5, 1, out moved, out still);
            if (!moved || still || AswMath.DistNm(lm, lp) < 4.4 || AswMath.DistNm(lm, tg) < 3.5) return "launch off land " + moved + " " + still;
            LaunchOffLand(lp, tg, 3.5, pt => true, 1, out moved, out still);
            if (moved || !still) return "launch all land";
            var same = LaunchOffLand(lp, tg, 3.5, null, 1, out moved, out still);
            if (moved || still || same.Lat != lp.Lat) return "launch at sea";
            // the minimum range stops the shift: land up to 59 nm toward the target, the missile needs 3.5 nm
            LaunchOffLand(lp, tg, 3.5, pt => AswMath.DistNm(pt, tg) > 2, 1, out moved, out still);
            if (!still) return "launch min range";
            // fuel
            var f = new StrikeAircraft { FuelFracLive = true, FuelFrac = 0.08, ReserveNm = -1, MaxRangeNm = 0 };
            if (FuelLeave(f, p) != "fuel_hard") return "fuel hard (live fraction 0.08, no path)";
            f = new StrikeAircraft { FuelFracLive = true, FuelFrac = 0.5, ReserveNm = 4, MaxRangeNm = 1800 };
            if (FuelLeave(f, p) != "fuel_margin") return "fuel margin 4 <= 5";
            f.ReserveNm = 5.01;
            if (FuelLeave(f, p) != null) return "fuel above the margin";
            f.ReserveNm = 9;
            if (FuelLeave(f, new StrikeParams { FuelMarginNm = 10 }) != "fuel_margin") return "fuel margin 9 <= 10";
            f = new StrikeAircraft { FuelFracLive = true, FuelFrac = 0.04, ReserveNm = 80, MaxRangeNm = 1800 };
            if (FuelLeave(f, new StrikeParams { FuelFloorPct = 0.05 }) != "fuel_floor") return "fuel floor";
            if (FuelLeave(f, p) != null) return "the floor is off by default";
            f = new StrikeAircraft { FuelFracLive = false, FuelFrac = 0.4, ReserveNm = 40, MaxRangeNm = 1864 };
            if (FuelLeave(f, p) != "fuel_margin") return "fuel ring";
            f.ReserveNm = 200;
            if (FuelLeave(f, p) != null) return "fuel ring above";
            if (FuelLeave(new StrikeAircraft { FuelFrac = 0.5 }, p) != null || FuelLeave(null, p) != null) return "fuel unknown";
            return null;
        }
    }
}
