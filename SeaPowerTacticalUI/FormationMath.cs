using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-243: pure rules of the radial "return to formation" sector. "On station" = the unit stands at its slot of the game's formation:
    /// not acting independently, close enough to the slot point, and (when the slot is not on top of the leader) on the slot's bearing from the leader.
    /// The constants are first guesses, tuned after the live check (plan R2); no settings keys.
    /// </summary>
    internal static class FormationMath
    {
        public const double TolMinNm = 0.25, TolRelPct = 0.15, TolDeg = 10, BearingMinRangeNm = 0.5;
        private const double EarthNm = 3440.065;

        /// <summary>Great-circle distance (haversine), nautical miles.</summary>
        public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180;
            double dp = p2 - p1, dl = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            return 2 * EarthNm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        /// <summary>Initial bearing from point 1 to point 2, degrees 0..360.</summary>
        public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
        {
            double p1 = lat1 * Math.PI / 180, p2 = lat2 * Math.PI / 180, dl = (lon2 - lon1) * Math.PI / 180;
            double y = Math.Sin(dl) * Math.Cos(p2);
            double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
            double b = Math.Atan2(y, x) * 180 / Math.PI;
            return (b % 360 + 360) % 360;
        }

        /// <summary>T-354: the metric of the game. globe = the great circle above (beta), otherwise the flat world of stable (1 deg = 60 nm).</summary>
        public static double DistanceNm(double lat1, double lon1, double lat2, double lon2, bool globe)
        { return globe ? DistanceNm(lat1, lon1, lat2, lon2) : WorldGeo.FlatDistanceNm(lat1, lon1, lat2, lon2); }

        public static double BearingDeg(double lat1, double lon1, double lat2, double lon2, bool globe)
        { return globe ? BearingDeg(lat1, lon1, lat2, lon2) : WorldGeo.FlatBearing(lat1, lon1, lat2, lon2); }

        /// <summary>Smallest angle between two bearings, 0..180.</summary>
        public static double AngleDiffDeg(double a, double b)
        {
            double d = Math.Abs(a - b) % 360;
            return d > 180 ? 360 - d : d;
        }

        /// <summary>The rule of the plan section 3. offNm = unit to slot, offDeg = bearing deviation seen from the leader (both 0 when unknown).</summary>
        public static bool OnStation(Ui.FormationFacts f, out double offNm, out double offDeg)
        {
            offNm = offDeg = 0;
            if (f == null || !f.InFormation || f.Independent || !f.GeoOk) return false;
            offNm = DistanceNm(f.UnitLat, f.UnitLon, f.SlotLat, f.SlotLon, f.Globe);   // T-354: the game's metric
            double r = DistanceNm(f.LeaderLat, f.LeaderLon, f.SlotLat, f.SlotLon, f.Globe);
            offDeg = AngleDiffDeg(BearingDeg(f.LeaderLat, f.LeaderLon, f.UnitLat, f.UnitLon, f.Globe), BearingDeg(f.LeaderLat, f.LeaderLon, f.SlotLat, f.SlotLon, f.Globe));
            if (offNm > Math.Max(TolMinNm, TolRelPct * r)) return false;
            return r < BearingMinRangeNm || offDeg <= TolDeg;
        }

        /// <summary>Ring state of the formation sector: null = hidden (not in a formation, or the leader), "on_station" = grey, "ready" = active.</summary>
        public static string RingState(Ui.FormationFacts f)
        {
            if (f == null || !f.InFormation || f.IsLeader) return null;
            double nm, deg;
            return OnStation(f, out nm, out deg) ? "on_station" : "ready";
        }

        private static Ui.FormationFacts Case(double slotE, double slotN, double unitE, double unitN)
        {
            // the leader at 0/0; one minute of latitude = one nautical mile, near the equator the same for longitude
            return new Ui.FormationFacts
            {
                InFormation = true, GeoOk = true,
                LeaderLat = 0, LeaderLon = 0,
                SlotLat = slotN / 60, SlotLon = slotE / 60,
                UnitLat = unitN / 60, UnitLon = unitE / 60,
            };
        }

        /// <summary>T-354: flat vs globe at 60 N: slot 2 miles east, unit 2.4 miles east (Case() is an equator helper, built by hand here).</summary>
        private static string WorldFlatTest()
        {
            foreach (bool globe in new[] { false, true })
            {
                var f = new Ui.FormationFacts
                {
                    InFormation = true, GeoOk = true, Globe = globe,
                    LeaderLat = 60, LeaderLon = 0, SlotLat = 60, SlotLon = 2.0 / 60, UnitLat = 60, UnitLon = 2.4 / 60,
                };
                string want = globe ? "on_station" : "ready";
                if (RingState(f) != want) return "worldflat: globe=" + globe + " ring state " + RingState(f) + ", want " + want;
            }
            foreach (double phi in new[] { 0.0, 45.0, 60.0, 70.0 })
            {
                if (DistanceNm(phi, 1, phi + 0.1, 2.5, true) != DistanceNm(phi, 1, phi + 0.1, 2.5)) return "worldflat: globe distance is not the sphere at " + phi;
                if (BearingDeg(phi, 1, phi + 0.1, 2.5, true) != BearingDeg(phi, 1, phi + 0.1, 2.5)) return "worldflat: globe bearing is not the sphere at " + phi;
                if (Math.Abs(DistanceNm(phi, 1, phi, 2, false) - 60.0) > 1e-9) return "worldflat: flat degree at " + phi;
            }
            return null;
        }

        public static string SelfTest()
        {
            double nm, deg;
            if (Math.Abs(DistanceNm(0, 0, 1.0 / 60, 0) - 1) > 0.01) return "distance of one arc minute is not one mile";
            if (Math.Abs(BearingDeg(0, 0, 0, 1) - 90) > 0.01 || Math.Abs(BearingDeg(0, 0, 1, 0)) > 0.01 || Math.Abs(BearingDeg(0, 0, -1, 0) - 180) > 0.01) return "bearing";
            if (Math.Abs(AngleDiffDeg(350, 10) - 20) > 1e-9) return "angle difference across north";

            var on = Case(1, 0, 1.0, 0.05);
            if (!OnStation(on, out nm, out deg)) return "unit at its slot (d=0.05, db=3) is not on station";
            if (RingState(on) != "on_station") return "ring state on station";
            if (OnStation(Case(1, 0, 1.5, 0), out nm, out deg)) return "unit 0.5 mile from the slot is on station";
            if (Math.Abs(nm - 0.5) > 0.01) return "offNm " + nm;
            // 12 degrees off the slot bearing at the slot's range: close (0.21 mile) but on the wrong bearing
            double rad = 102 * Math.PI / 180;
            var wrong = Case(1, 0, Math.Sin(rad), Math.Cos(rad));
            if (OnStation(wrong, out nm, out deg)) return "wrong bearing is on station";
            if (nm >= TolMinNm || Math.Abs(deg - 12) > 0.2) return "wrong bearing case numbers d=" + nm + " db=" + deg;
            // the slot almost on the leader: the bearing means nothing
            if (!OnStation(Case(0.2, 0, 0.2, 0.15), out nm, out deg)) return "slot near the leader: bearing must be ignored";
            // the tolerance grows with the slot range: 0.4 mile off at 4 miles is within 15 percent (0.6)
            if (!OnStation(Case(4, 0, 4.4, 0), out nm, out deg)) return "relative tolerance";
            string wf = WorldFlatTest();   // T-354
            if (wf != null) return wf;
            var ind = Case(1, 0, 1, 0); ind.Independent = true;
            if (OnStation(ind, out nm, out deg)) return "independent unit is on station";
            if (RingState(ind) != "ready") return "independent unit: ring state must be ready";
            var nogeo = Case(1, 0, 1, 0); nogeo.GeoOk = false;
            if (OnStation(nogeo, out nm, out deg) || RingState(nogeo) != "ready") return "no geometry: not on station, button active";
            var lead = Case(1, 0, 1, 0); lead.IsLeader = true;
            if (RingState(lead) != null) return "leader must get no sector";
            if (RingState(new Ui.FormationFacts()) != null || RingState(null) != null) return "no formation: no sector";
            return null;
        }
    }
}
