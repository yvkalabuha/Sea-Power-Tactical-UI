using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-169 / ADR-0067: the enemy weapon zones on the intercept route. Pure (no Unity, no game): the threats come from IInterceptGame.Threats.
    // Local flat plane in nm around the start of the route (1 nm = 1/60 degree north; east is x LonK on the beta globe, T-348, as the rest of the intercept), x = east, y = north.

    /// <summary>One weapon of the class reference (encyclopedia, variant 0). Target / Type are the game enum names (ASuW, ASW, AAW; Missile, Projectile, ...).</summary>
    internal struct IcWeapon
    {
        public string Name, Type, Primary, Secondary;
        public bool HasSecondary;
        public double MaxNm;
    }

    /// <summary>An identified hostile track with the range of its class against our domain. Position and velocity are the PLAYER'S track.</summary>
    internal sealed class IcThreat
    {
        public int TrackId;
        public string Name, Weapon, WeaponKind;   // WeaponKind: missile | gun | torpedo | asw | other
        public double Lat, Lon, VxKn, VyKn, RangeNm;
        public bool HasVel;
    }

    /// <summary>The route meets the zone of the threat: EntryS seconds from now (0 when InsideNow).</summary>
    internal sealed class IcZoneHit
    {
        public IcThreat Threat;
        public double EntryS;
        public bool InsideNow, IsTarget;
    }

    internal static class InterceptThreatMath
    {
        public const double CheckEveryS = 5, DefaultLeadS = 120, DefaultMarginNm = 2, ResumeFactor = 1.5;

        private static bool Bad(double v) { return double.IsNaN(v) || double.IsInfinity(v); }

        /// <summary>[Intercept] AvoidEnemyRanges: warn | detour | off (anything else = warn).</summary>
        public static string AvoidOf(string v)
        {
            string s = (v ?? "warn").Trim().ToLowerInvariant();
            return s == "detour" || s == "off" ? s : "warn";
        }

        /// <summary>[Intercept] OnZoneEntry: warn | hold | abort (anything else = warn).</summary>
        public static string OnEntryOf(string v)
        {
            string s = (v ?? "warn").Trim().ToLowerInvariant();
            return s == "hold" || s == "abort" ? s : "warn";
        }

        public static string KindOf(string ammoType)
        {
            switch (ammoType ?? "")
            {
                case "Missile": return "missile";
                case "Projectile": return "gun";
                case "Torpedo": return "torpedo";
                case "Rbu": case "Asroc": case "AirDepthCharge": return "asw";
                default: return "other";
            }
        }

        private static bool AswDelivery(string type) { return type == "Rbu" || type == "Asroc" || type == "AirDepthCharge"; }

        /// <summary>The longest weapon of the class against our domain: ASuW for a ship or a surfaced submarine, ASW (+ RBU/ASROC/depth charges)
        /// under water. false = the class has nothing against us.</summary>
        public static bool Fold(IList<IcWeapon> weapons, bool submerged, out double rangeNm, out string name, out string kind)
        {
            rangeNm = 0; name = null; kind = null;
            if (weapons == null) return false;
            string target = submerged ? "ASW" : "ASuW";
            foreach (IcWeapon w in weapons)
            {
                bool hits = w.Primary == target || (w.HasSecondary && w.Secondary == target) || (submerged && AswDelivery(w.Type));
                if (!hits || Bad(w.MaxNm) || !(w.MaxNm > rangeNm)) continue;
                rangeNm = w.MaxNm; name = w.Name; kind = KindOf(w.Type);
            }
            return rangeNm > 0;
        }

        private static void Local(GeoPt origin, double lat, double lon, out double x, out double y)
        {
            x = GeoLon.DLon(origin.Lon, lon) * 60.0 * AswMath.LonK(origin.Lat, lat); y = (lat - origin.Lat) * 60.0;
        }

        /// <summary>Seconds from now until the unit, moving from start along pts at kn, is first within RangeNm of the threat, which moves on with
        /// its track velocity. NaN = not within maxS / not along the route. kn &lt;= 0 or no points: the unit stands for maxS.</summary>
        public static double EntryS(GeoPt start, IList<GeoPt> pts, double kn, IcThreat t, double maxS, out bool insideNow)
        {
            insideNow = false;
            if (t == null || Bad(t.RangeNm) || !(t.RangeNm > 0) || Bad(t.Lat) || Bad(t.Lon)) return double.NaN;
            double px, py;
            Local(start, t.Lat, t.Lon, out px, out py);
            double r = t.RangeNm, r2 = r * r;
            if (px * px + py * py <= r2) { insideNow = true; return 0; }
            double vx = t.HasVel && !Bad(t.VxKn) ? t.VxKn / 3600.0 : 0, vy = t.HasVel && !Bad(t.VyKn) ? t.VyKn / 3600.0 : 0;
            if (Bad(maxS) || maxS <= 0) maxS = 0;
            var legs = new List<double[]>();   // ax, ay, ux, uy, T  (nm, nm/s, s)
            double ax = 0, ay = 0, acc = 0;
            if (pts != null && kn > 0 && !Bad(kn))
            {
                foreach (GeoPt b in pts)
                {
                    double bx, by;
                    Local(start, b.Lat, b.Lon, out bx, out by);
                    double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                    if (len < 1e-9) continue;
                    double tl = len / kn * 3600.0;
                    legs.Add(new[] { ax, ay, (bx - ax) / tl, (by - ay) / tl, tl });
                    ax = bx; ay = by; acc += tl;
                }
            }
            if (legs.Count == 0) legs.Add(new[] { 0.0, 0.0, 0.0, 0.0, maxS });
            double t0 = 0;
            foreach (double[] l in legs)
            {
                if (t0 >= maxS) break;
                double T = Math.Min(l[4], maxS - t0);
                double dx = l[0] - px - vx * t0, dy = l[1] - py - vy * t0, wx = l[2] - vx, wy = l[3] - vy;
                double A = wx * wx + wy * wy, B = 2 * (dx * wx + dy * wy), C = dx * dx + dy * dy - r2;
                if (C <= 0) return t0;
                if (A > 1e-18)
                {
                    double disc = B * B - 4 * A * C;
                    if (disc >= 0)
                    {
                        double t1 = (-B - Math.Sqrt(disc)) / (2 * A);
                        if (t1 >= 0 && t1 <= T) return t0 + t1;
                    }
                }
                t0 += l[4];
            }
            return double.NaN;
        }

        /// <summary>Every zone the route meets within maxS, the zones the unit is in first, then by the time of entry.</summary>
        public static List<IcZoneHit> Check(GeoPt start, IList<GeoPt> pts, double kn, IList<IcThreat> threats, int targetId, double maxS)
        {
            var res = new List<IcZoneHit>();
            if (threats == null) return res;
            foreach (IcThreat t in threats)
            {
                bool inside;
                double e = EntryS(start, pts, kn, t, maxS, out inside);
                if (double.IsNaN(e)) continue;
                res.Add(new IcZoneHit { Threat = t, EntryS = e, InsideNow = inside, IsTarget = t.TrackId == targetId });
            }
            res.Sort((a, b) => a.InsideNow != b.InsideNow ? (a.InsideNow ? -1 : 1) : a.EntryS.CompareTo(b.EntryS));
            return res;
        }

        /// <summary>Route time (s) of start + pts at kn; NaN when kn &lt;= 0.</summary>
        public static double RouteS(GeoPt start, IList<GeoPt> pts, double kn)
        {
            if (!(kn > 0) || Bad(kn) || pts == null) return double.NaN;
            double len = 0; GeoPt a = start;
            foreach (GeoPt b in pts) { len += FlatNm(a, b); a = b; }
            return len / kn * 3600.0;
        }

        public static double FlatNm(GeoPt a, GeoPt b)
        {
            double dn = (b.Lat - a.Lat) * 60.0, de = GeoLon.DLon(a.Lon, b.Lon) * 60.0 * AswMath.LonK(a.Lat, b.Lat);
            return Math.Sqrt(dn * dn + de * de);
        }

        /// <summary>The point lies within RangeNm + marginNm of a zone (the threat where it is now: the detour treats the circle as fixed).</summary>
        public static bool Blocked(GeoPt g, IList<IcThreat> zones, double marginNm)
        {
            if (zones == null) return false;
            foreach (IcThreat t in zones)
                if (FlatNm(g, new GeoPt(t.Lat, t.Lon)) <= t.RangeNm + marginNm) return true;
            return false;
        }

        /// <summary>The straight leg a-b passes within RangeNm + marginNm of a zone.</summary>
        public static bool LegCrosses(GeoPt a, GeoPt b, IList<IcThreat> zones, double marginNm)
        {
            if (zones == null) return false;
            double bx = GeoLon.DLon(a.Lon, b.Lon) * 60.0 * AswMath.LonK(a.Lat, b.Lat), by = (b.Lat - a.Lat) * 60.0, l2 = bx * bx + by * by;
            foreach (IcThreat t in zones)
            {
                double px = GeoLon.DLon(a.Lon, t.Lon) * 60.0 * AswMath.LonK(a.Lat, t.Lat), py = (t.Lat - a.Lat) * 60.0;
                double s = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (px * bx + py * by) / l2));
                double dx = px - bx * s, dy = py - by * s;
                if (Math.Sqrt(dx * dx + dy * dy) <= t.RangeNm + marginNm) return true;
            }
            return false;
        }

        /// <summary>The zones a detour may route around: not the target's, not one the unit is in, not one that covers the intercept point.</summary>
        public static List<IcThreat> Avoidable(GeoPt own, GeoPt ip, IList<IcThreat> threats, int targetId, double marginNm, List<IcThreat> skipped)
        {
            var res = new List<IcThreat>();
            if (threats == null) return res;
            foreach (IcThreat t in threats)
            {
                if (t.TrackId == targetId) continue;
                var c = new GeoPt(t.Lat, t.Lon);
                if (FlatNm(own, c) <= t.RangeNm + marginNm || FlatNm(ip, c) <= t.RangeNm + marginNm) { if (skipped != null) skipped.Add(t); continue; }
                res.Add(t);
            }
            return res;
        }

        /// <summary>A hit that stops the unit under OnZoneEntry hold/abort: not the target, entry within leadS (or the unit just went in: in now, not before).</summary>
        public static IcZoneHit Trigger(IList<IcZoneHit> hits, double leadS, ICollection<int> insideBefore)
        {
            if (hits == null) return null;
            foreach (IcZoneHit h in hits)
            {
                if (h.IsTarget) continue;
                if (h.InsideNow) { if (insideBefore != null && !insideBefore.Contains(h.Threat.TrackId)) return h; continue; }
                if (h.EntryS <= leadS) return h;
            }
            return null;
        }

        /// <summary>Change signature of a hit list for the log: the set of zones and inside / ahead (the countdown alone logs nothing).</summary>
        public static string Signature(IList<IcZoneHit> hits)
        {
            if (hits == null || hits.Count == 0) return "";
            var ids = new List<string>();
            foreach (IcZoneHit h in hits) ids.Add(h.Threat.TrackId + (h.InsideNow ? "i" : "e"));
            ids.Sort(StringComparer.Ordinal);
            return string.Join(";", ids.ToArray());
        }

        // ---------------------------------------------------------------- self test
        private const double Lat0 = 30, Lon0 = 40;
        private static GeoPt At(double n, double e) { return new GeoPt(Lat0 + n / 60.0, Lon0 + e / (60.0 * AswMath.LonK(Lat0))); }
        private static IcThreat T(int id, double n, double e, double r, double vx = 0, double vy = 0)
        {
            return new IcThreat { TrackId = id, Lat = Lat0 + n / 60.0, Lon = Lon0 + e / (60.0 * AswMath.LonK(Lat0)), RangeNm = r, VxKn = vx, VyKn = vy, HasVel = vx != 0 || vy != 0, Name = "T" + id };
        }

        public static string SelfTest()
        {
            try { return SelfTestCore(); }
            catch (Exception e) { return "intercept_threat: " + e.GetType().Name + ": " + e.Message; }
        }

        private static string SelfTestCore()
        {
            bool inside;
            // a fixed zone of 5 nm at 20 nm north, the unit goes north at 30 kn: enters at 15 nm = 30 min
            double e = EntryS(At(0, 0), new[] { At(40, 0) }, 30, T(1, 20, 0, 5), 99999, out inside);
            if (inside || Math.Abs(e - 1800) > 1e-6) return "straight entry " + e;
            // passes 6 nm abeam: no entry
            e = EntryS(At(0, 0), new[] { At(40, 0) }, 30, T(1, 20, 6, 5), 99999, out inside);
            if (!double.IsNaN(e)) return "abeam " + e;
            // inside now
            e = EntryS(At(0, 0), new[] { At(40, 0) }, 30, T(1, 2, 0, 5), 99999, out inside);
            if (!inside || e != 0) return "inside";
            // the route ends before the zone (IP at 10 nm)
            e = EntryS(At(0, 0), new[] { At(10, 0) }, 30, T(1, 20, 0, 5), 99999, out inside);
            if (!double.IsNaN(e)) return "route ends before " + e;
            // horizon: entry after maxS is ignored
            e = EntryS(At(0, 0), new[] { At(40, 0) }, 30, T(1, 20, 0, 5), 1000, out inside);
            if (!double.IsNaN(e)) return "horizon " + e;
            // second leg: east 10 nm, then north; zone at (20, 10) r 5 -> entry after 10 + 15 = 25 nm = 50 min
            e = EntryS(At(0, 0), new[] { At(0, 10), At(40, 10) }, 30, T(1, 20, 10, 5), 99999, out inside);
            if (Math.Abs(e - 3000) > 1e-6) return "second leg " + e;
            // a moving threat closes in: own north 30 kn, threat 20 nm north coming south 30 kn, r 5 -> 15 nm at 60 kn closing = 15 min
            e = EntryS(At(0, 0), new[] { At(40, 0) }, 30, T(1, 20, 0, 5, 0, -30), 99999, out inside);
            if (Math.Abs(e - 900) > 1e-6) return "moving " + e;
            // the unit stands (kn 0), a threat comes: 15 nm at 30 kn = 30 min
            e = EntryS(At(0, 0), new GeoPt[0], 0, T(1, 20, 0, 5, 0, -30), 99999, out inside);
            if (Math.Abs(e - 1800) > 1e-6) return "standing " + e;
            if (!double.IsNaN(EntryS(At(0, 0), new[] { At(40, 0) }, 30, T(1, 20, 0, 0), 99999, out inside))) return "range 0";

            // Check: order inside first, then by entry; target flag
            var hits = Check(At(0, 0), new[] { At(40, 0) }, 30, new[] { T(7, 30, 0, 5), T(8, 20, 0, 5), T(9, 1, 0, 3), T(10, 0, 30, 5) }, 7, 99999);
            if (hits.Count != 3 || hits[0].Threat.TrackId != 9 || !hits[0].InsideNow || hits[1].Threat.TrackId != 8 || hits[2].Threat.TrackId != 7 || !hits[2].IsTarget) return "check order";
            if (Signature(hits) != "7e;8e;9i") return "signature " + Signature(hits);

            // Trigger: target never; inside only when new; lead
            var ins = new HashSet<int> { 9 };
            IcZoneHit tr = Trigger(hits, 120, ins);
            if (tr != null) return "trigger far";
            tr = Trigger(hits, 1900, ins);
            if (tr == null || tr.Threat.TrackId != 8) return "trigger lead";
            tr = Trigger(hits, 120, new HashSet<int>());
            if (tr == null || tr.Threat.TrackId != 9) return "trigger new inside";

            // Fold
            var ws = new List<IcWeapon>
            {
                new IcWeapon { Name = "SA-N-4", Type = "Missile", Primary = "AAW", MaxNm = 8 },
                new IcWeapon { Name = "AK-725", Type = "Projectile", Primary = "AAW", Secondary = "ASuW", HasSecondary = true, MaxNm = 8.5 },
                new IcWeapon { Name = "SS-N-2", Type = "Missile", Primary = "ASuW", MaxNm = 25 },
                new IcWeapon { Name = "RBU-6000", Type = "Rbu", Primary = "ASuW", MaxNm = 3 },
                new IcWeapon { Name = "SET-65", Type = "Torpedo", Primary = "ASW", MaxNm = 8 },
            };
            double rn; string nm, kd;
            if (!Fold(ws, false, out rn, out nm, out kd) || rn != 25 || nm != "SS-N-2" || kd != "missile") return "fold surface " + rn + " " + nm;
            if (!Fold(ws, true, out rn, out nm, out kd) || rn != 8 || nm != "SET-65" || kd != "torpedo") return "fold submerged " + rn + " " + nm;
            if (Fold(new[] { ws[0] }, false, out rn, out nm, out kd)) return "fold aaw only";
            ws[1] = new IcWeapon { Name = "AK-725", Type = "Projectile", Primary = "AAW", Secondary = "ASuW", HasSecondary = false, MaxNm = 30 };
            if (!Fold(ws, false, out rn, out nm, out kd) || rn != 25) return "fold secondary needs HasSecondary";

            // Blocked / LegCrosses / Avoidable
            var z = new List<IcThreat> { T(1, 20, 0, 5) };
            if (!Blocked(At(20, 6.5), z, 2) || Blocked(At(20, 7.5), z, 2)) return "blocked";
            if (!LegCrosses(At(0, 6), At(40, 6), z, 2) || LegCrosses(At(0, 8), At(40, 8), z, 2)) return "leg crosses";
            var skipped = new List<IcThreat>();
            var av = Avoidable(At(0, 0), At(40, 0), new[] { T(1, 20, 0, 5), T(2, 1, 0, 5), T(3, 39, 0, 5), T(4, 20, 3, 5) }, 4, 2, skipped);
            if (av.Count != 1 || av[0].TrackId != 1 || skipped.Count != 2) return "avoidable " + av.Count + "/" + skipped.Count;

            if (AvoidOf("Detour ") != "detour" || AvoidOf("x") != "warn" || AvoidOf(null) != "warn" || AvoidOf("off") != "off") return "AvoidOf";
            if (OnEntryOf("HOLD") != "hold" || OnEntryOf("abort") != "abort" || OnEntryOf("") != "warn") return "OnEntryOf";
            if (KindOf("Projectile") != "gun" || KindOf("Asroc") != "asw" || KindOf(null) != "other") return "KindOf";
            if (Math.Abs(RouteS(At(0, 0), new[] { At(0, 10), At(10, 10) }, 20) - 3600) > 1e-6 || !double.IsNaN(RouteS(At(0, 0), new[] { At(1, 0) }, 0))) return "RouteS";
            return LatTest();
        }

        // T-348: the route entry time and the leg test on the beta globe at 0 / 45 / 60 / 70 deg (flat: previous behaviour)
        private static string LatTest()
        {
            foreach (double lat in new[] { 0.0, 45, 60, 70 })
                using (AswMath.WorldScope(true))
                {
                    var P = new GeoPt(lat, 10);
                    GeoPt ip = AswMath.Offset(P, 90, 40), zc = AswMath.Offset(P, 90, 20);
                    var z = new IcThreat { TrackId = 1, Lat = zc.Lat, Lon = zc.Lon, RangeNm = 5, Name = "T1" };
                    bool inside;
                    double e = EntryS(P, new[] { ip }, 30, z, 99999, out inside);
                    if (inside || Math.Abs(e - 1800) > 1800 * 0.015) return "lat entry " + lat + " " + e;
                    GeoPt far = AswMath.Offset(P, 90, 40), off = AswMath.Offset(AswMath.Offset(P, 90, 20), 0, 6);
                    var z2 = new IcThreat { TrackId = 2, Lat = off.Lat, Lon = off.Lon, RangeNm = 5, Name = "T2" };
                    if (LegCrosses(P, far, new List<IcThreat> { z2 }, 0)) return "lat leg abeam " + lat;
                    var z3 = new IcThreat { TrackId = 3, Lat = off.Lat, Lon = off.Lon, RangeNm = 7, Name = "T3" };
                    if (!LegCrosses(P, far, new List<IcThreat> { z3 }, 0)) return "lat leg crosses " + lat;
                    if (Math.Abs(FlatNm(P, ip) / 40 - 1) > 0.015) return "lat FlatNm " + lat;
                }
            return null;
        }
    }
}
