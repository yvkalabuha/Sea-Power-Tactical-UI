// T-137 A4: leak test. LeakFacts records every call and throws when the side/identification contract is broken.
using System;
using System.Collections.Generic;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    internal sealed class LeakException : Exception { public LeakException(string m) : base(m) { } }

    internal sealed class LeakFacts : IRingFacts
    {
        public readonly List<string> Log = new List<string>();
        public List<int> Sel = new List<int>();
        public Dictionary<int, UnitSide> Sides = new Dictionary<int, UnitSide>();
        public bool AlliesOn;
        public int Contact = -1; public UnitSide ContactSide = UnitSide.Other; public bool Ident; public ClassRanges Cls;
        public RingPt TrackAt = new RingPt(12.5, 33.25);
        // T-162: per-track values (a track without an entry uses the single contact fields); Class() requires an IsIdentified() == true answer for that very track
        public Dictionary<int, UnitSide> TrackSides = new Dictionary<int, UnitSide>();
        public Dictionary<int, bool> IdentOf = new Dictionary<int, bool>();
        public Dictionary<int, ClassRanges> ClsOf = new Dictionary<int, ClassRanges>();
        public HashSet<int> IdentifiedSeen = new HashSet<int>();
        public bool Identified;   // flips to true on the first IsIdentified() answer "true"

        public IList<int> SelectedOwn() { Log.Add("SelectedOwn"); return Sel; }
        public IList<int> GroupOf(int uid) { Log.Add("GroupOf"); return null; }
        public UnitSide SideOf(int uid) { Log.Add("SideOf"); UnitSide s; return Sides.TryGetValue(uid, out s) ? s : UnitSide.Other; }
        public OwnArmament Own(int uid)
        {
            Log.Add("Own");
            UnitSide s = SideOf(uid); Log.RemoveAt(Log.Count - 1);
            if (s != UnitSide.Player && !(s == UnitSide.Ally && AlliesOn)) throw new LeakException("Own() for side " + s);
            var a = new OwnArmament();
            a.Ammo.Add(new AmmoFact { Name = "m", Primary = RingTarget.Aaw, Type = RingAmmoType.Missile, MaxNm = 30, Left = 1 });
            return a;
        }
        public bool OwnPose(int uid, out RingPt p, out double h, out bool air) { Log.Add("OwnPose"); p = new RingPt(1, 2); h = 0; air = false; return true; }
        public int SelectedContact() { Log.Add("SelectedContact"); return Contact; }
        public UnitSide TrackSide(int id) { Log.Add("TrackSide"); UnitSide v; return TrackSides.TryGetValue(id, out v) ? v : ContactSide; }
        public bool IsIdentified(int id)
        {
            Log.Add("IsIdentified");
            bool r; if (!IdentOf.TryGetValue(id, out r)) r = Ident;
            Identified = r;
            if (r) IdentifiedSeen.Add(id); else IdentifiedSeen.Remove(id);
            return r;
        }
        public bool TrackPose(int id, out RingPt p, out bool air) { Log.Add("TrackPose"); p = TrackAt; air = false; return true; }
        public ClassRanges Class(int id)
        {
            Log.Add("Class");
            if (!Identified && !IdentifiedSeen.Contains(id)) throw new LeakException("Class() before IsIdentified() == true");
            ClassRanges v; return ClsOf.TryGetValue(id, out v) ? v : Cls;
        }
        public double? Course = 77; public List<int> CourseAsked = new List<int>();
        public bool TrackCourse(int id, out double deg)
        {
            Log.Add("TrackCourse");
            if (!Identified && !IdentifiedSeen.Contains(id)) throw new LeakException("TrackCourse() before IsIdentified() == true");
            CourseAsked.Add(id);
            deg = Course ?? 0; return Course.HasValue;
        }
        public Dictionary<int, string> Gone = new Dictionary<int, string>();
        public string TrackGone(int id) { Log.Add("TrackGone"); string v; return Gone.TryGetValue(id, out v) ? v : null; }
        // T-321
        public RingGeoMetric MetricAt;
        public RingGeoMetric Metric(RingPt at) { return MetricAt; }
        public Dictionary<string, double> Reach = new Dictionary<string, double>();
        public double ReachNm(int uid, AmmoFact a) { double v; return Reach.TryGetValue(a.Name, out v) ? v : double.NaN; }
        public Dictionary<string, KinCheck> Checks = new Dictionary<string, KinCheck>();
        public KinCheck VsTrack(int uid, AmmoFact a, int trackId)
        {
            Log.Add("VsTrack");
            UnitSide s = SideOf(uid); Log.RemoveAt(Log.Count - 1);
            if (s != UnitSide.Player && !(s == UnitSide.Ally && AlliesOn)) throw new LeakException("VsTrack() for side " + s);
            KinCheck v; return Checks.TryGetValue(a.Name, out v) ? v : null;
        }
        public int Count(string name) { int n = 0; foreach (string s in Log) if (s == name) n++; return n; }
    }

    internal static class RingTestLeakCases
    {
        /// <summary>A class with a SAM, an AShM (all-round) and a passive sonar of 14 nm.</summary>
        private static ClassRanges Cls() { return RangeRingModel.TestClass("k", 25, 60, 14); }

        private static RangeRingPins Track7() { var p = new RangeRingPins(); p.ToggleTrackWeapon(7, "sam"); p.ToggleTrackWeapon(7, "ashm"); return p; }

        private static string Calls(LeakFacts f) { return string.Join(",", f.Log); }

        public static string SelfTest()
        {
            // a pinned unit of another side is never asked for its armament
            {
                var pf = new LeakFacts { Contact = -1 };
                pf.Sides[2] = UnitSide.Other;
                var po = new RingOptions { Pins = new RangeRingPins() };
                po.Pins.ToggleWeapon(2, "m");
                RingScene ps = RangeRingModel.Build(pf, po);
                if (ps.Units.Count != 0 || pf.Count("Own") != 0 || pf.Count("OwnPose") != 0) return "pinned foreign unit leaked: " + Calls(pf);
            }

            string e = SelfTestKin() ?? SelfTest171() ?? SelfTest164() ?? SelfTest162() ?? SelfTestGone();
            if (e != null) return e;

            var o = new RingOptions { Pins = Track7() };
            // unidentified contact: empty scene, exactly one IsIdentified, nothing else about it
            var f = new LeakFacts { Contact = 7, Ident = false, Cls = Cls() };
            RingScene s = RangeRingModel.Build(f, o);
            if (s.Units.Count != 0) return "unidentified: scene not empty";
            if (f.Count("IsIdentified") != 1) return "unidentified: IsIdentified x" + f.Count("IsIdentified");
            if (f.Count("Class") != 0 || f.Count("TrackPose") != 0 || f.Count("Own") != 0 || f.Count("OwnPose") != 0) return "unidentified: extra calls " + Calls(f);

            // identified hostile with checked weapons: class only, UpTo everywhere, no active sonar, centre = TrackPose
            f = new LeakFacts { Contact = 7, Ident = true, Cls = Cls() };
            s = RangeRingModel.Build(f, o);
            string err = CheckClass(s, f);
            if (err != null) return "identified: " + err;
            if (f.Count("Own") != 0 || f.Count("OwnPose") != 0 || f.Count("Class") != 1) return "identified: calls " + Calls(f);

            // no class record: empty
            f = new LeakFacts { Contact = 7, Ident = true, Cls = null };
            s = RangeRingModel.Build(f, o);
            if (s.Units.Count != 0 || f.Count("Class") != 1) return "no record: not empty";

            // selected track of an own/allied unit never goes to the class branch
            foreach (UnitSide side in new[] { UnitSide.Player, UnitSide.Ally })
            {
                f = new LeakFacts { Contact = 7, Ident = true, ContactSide = side, Cls = Cls() };
                s = RangeRingModel.Build(f, o);
                if (s.Units.Count != 0 || f.Count("Class") != 0 || f.Count("IsIdentified") != 0) return "own track took the class branch (" + side + ")";
            }

            // Own() never reached for non-own sides: unknown/other selected unit, ally with Allies off
            var oo = new RingOptions { Pins = new RangeRingPins() };
            foreach (int uid in new[] { 1, 2, 3 }) oo.Pins.ToggleWeapon(uid, "m");
            f = new LeakFacts { Sel = new List<int> { 1, 2, 3 } };
            f.Sides[1] = UnitSide.Other; f.Sides[2] = UnitSide.Ally; f.Sides[3] = UnitSide.Player;
            try { s = RangeRingModel.Build(f, oo); }
            catch (LeakException ex) { return "Own() leak: " + ex.Message; }
            if (s.Units.Count != 1 || s.Units[0].Key != 3 || f.Count("Own") != 1) return "own filter: units " + s.Units.Count + " Own x" + f.Count("Own");
            oo.Allies = true; f.AlliesOn = true; f.Log.Clear();
            try { s = RangeRingModel.Build(f, oo); }
            catch (LeakException ex) { return "Own() leak (allies on): " + ex.Message; }
            if (s.Units.Count != 2 || f.Count("Own") != 2) return "allies on: Own x" + f.Count("Own");

            // the stub itself must throw (the guard is real)
            try { new LeakFacts().Own(1); return "stub does not throw for a non-own side"; } catch (LeakException) { }
            try { new LeakFacts { Cls = Cls() }.Class(1); return "stub does not throw for Class() before IsIdentified"; } catch (LeakException) { }
            return null;
        }

        /// <summary>T-321: the game's radius replaces the nominal one (NaN keeps it); the verdict against the selected contact needs the track side and position only.</summary>
        private static string SelfTestKin()
        {
            Func<int, LeakFacts> mk = contactSide =>
            {
                var f = new LeakFacts { Sel = new List<int> { 3 }, Contact = 7, ContactSide = (UnitSide)contactSide, Ident = false };
                f.Sides[3] = UnitSide.Player;
                return f;
            };
            var o = new RingOptions { Sonar = false, Pins = new RangeRingPins() };
            o.Pins.ToggleWeapon(3, "m");
            // the nominal 30 nm, the game says 21.2: the ring is 21.2; NaN keeps 30
            LeakFacts lf = mk((int)UnitSide.Other);
            lf.Reach["m"] = 21.2;
            RingScene s = RangeRingModel.Build(lf, o);
            if (s.Units.Count != 1 || s.Units[0].Rings.Count != 1 || s.Units[0].Rings[0].MaxNm != 21.2) return "kin: ring radius from ReachNm";
            lf = mk((int)UnitSide.Other);
            s = RangeRingModel.Build(lf, o);
            if (s.Units[0].Rings[0].MaxNm != 30) return "kin: NaN ReachNm keeps the nominal range";
            // the target outside the kinematic range but inside the nominal one: too far
            lf = mk((int)UnitSide.Other);
            lf.Reach["m"] = 21.2;
            lf.Checks["m"] = new KinCheck { Verdict = RangeRingKin.Verdict(25, 21.2, 0), DistNm = 25, MaxNm = 21.2, MinNm = 0 };
            s = RangeRingModel.Build(lf, o);
            if (s.Verdicts.Count != 1 || s.Verdicts[0].Verdict != KinVerdict.TooFar || s.Verdicts[0].Uid != 3 || s.Verdicts[0].TrackId != 7 || s.Verdicts[0].At.Lat != lf.TrackAt.Lat) return "kin: too far verdict";
            if (lf.Count("IsIdentified") != 1 || lf.Count("Class") != 0) return "kin: the verdict must not need identification: " + Calls(lf);
            // our own / allied track: VsTrack is never asked
            foreach (UnitSide side in new[] { UnitSide.Player, UnitSide.Ally })
            {
                lf = mk((int)side);
                lf.Checks["m"] = new KinCheck { Verdict = KinVerdict.InRange };
                s = RangeRingModel.Build(lf, o);
                if (s.Verdicts.Count != 0 || lf.Count("VsTrack") != 0) return "kin: verdict for a track of side " + side;
            }
            // no selected contact / a lost one: no verdict
            lf = mk((int)UnitSide.Other); lf.Contact = -1; lf.Checks["m"] = new KinCheck { Verdict = KinVerdict.InRange };
            if (RangeRingModel.Build(lf, o).Verdicts.Count != 0) return "kin: verdict without a contact";
            lf = mk((int)UnitSide.Other); lf.Gone[7] = "lost"; lf.Checks["m"] = new KinCheck { Verdict = KinVerdict.InRange };
            if (RangeRingModel.Build(lf, o).Verdicts.Count != 0 || lf.Count("VsTrack") != 0) return "kin: verdict for a lost track";
            // an unchecked weapon has no verdict; at most four lines
            lf = new LeakFacts { Sel = new List<int> { 1, 2, 3 }, Contact = 7, ContactSide = UnitSide.Other };
            var o2 = new RingOptions { Sonar = false, Pins = new RangeRingPins() };
            foreach (int uid in new[] { 1, 2, 3 }) { lf.Sides[uid] = UnitSide.Player; o2.Pins.ToggleWeapon(uid, "m"); }
            lf.Checks["m"] = new KinCheck { Verdict = KinVerdict.InRange, DistNm = 5, MaxNm = 30 };
            if (RangeRingModel.Build(lf, o2).Verdicts.Count != 3) return "kin: three checked weapons, three lines";
            var fm = new LeakFacts { Contact = 7, ContactSide = UnitSide.Other };
            for (int i = 0; i < 6; i++) fm.Sides[i] = UnitSide.Player;
            fm.Checks["m"] = new KinCheck { Verdict = KinVerdict.InRange };
            var o3 = new RingOptions { Sonar = false, Pins = new RangeRingPins() };
            for (int i = 0; i < 6; i++) { fm.Sel.Add(i); o3.Pins.ToggleWeapon(i, "m"); }
            if (RangeRingModel.Build(fm, o3).Verdicts.Count != RangeRingModel.MaxVerdicts) return "kin: at most four lines";
            lf = mk((int)UnitSide.Other);
            lf.Checks["m"] = new KinCheck { Verdict = KinVerdict.InRange };
            if (RangeRingModel.Build(lf, new RingOptions { Sonar = false, Pins = new RangeRingPins() }).Verdicts.Count != 0 || lf.Count("VsTrack") != 0) return "kin: no checked weapon, no verdict";
            // the metric of every unit
            lf = mk((int)UnitSide.Other);
            lf.MetricAt = new RingGeoMetric { LatNmPerDeg = 60, LonNmPerDeg = 60, Valid = true };
            s = RangeRingModel.Build(lf, o);
            if (!s.Units[0].Metric.Valid || s.Units[0].Metric.LonNmPerDeg != 60) return "kin: Metric filled for every unit";
            return null;
        }

        /// <summary>T-171: the course of a track and the weapon prune never read more than the contract allows.</summary>
        private static string SelfTest171()
        {
            // a class whose ini was read: wedges need the course, asked after IsIdentified and Class, once
            var ws = new List<ClassWeapon> { new ClassWeapon { Key = "h", Name = "H", Type = RingAmmoType.Missile, Primary = RingTarget.Asuw, MaxNm = 60 } };
            var mounts = new List<ClassMount> { new ClassMount { Type = "Missile", AmmoRefs = { "h" }, Arcs = { new ArcDeg { A = -45, B = 45 } } } };
            ClassRanges cls = RangeRingModel.BuildClass("k", ws, null, mounts, true);
            var p = new RangeRingPins(); p.ToggleTrackWeapon(7, "h");
            var f = new LeakFacts { Contact = 7, Ident = true, Cls = cls };
            RingScene s = RangeRingModel.Build(f, new RingOptions { Pins = p });
            if (s.Units.Count != 1 || s.Units[0].Sectors.Count != 1 || s.Units[0].HeadingDeg != 77) return "class wedges along the track course";
            if (f.Count("TrackCourse") != 1 || f.Log.IndexOf("TrackCourse") < f.Log.IndexOf("Class")) return "course asked once after Class: " + Calls(f);
            // all-round class: no course asked
            f = new LeakFacts { Contact = 7, Ident = true, Cls = RangeRingModel.BuildClass("k", ws, null, null, true) };
            RangeRingModel.Build(f, new RingOptions { Pins = p });
            if (f.Count("TrackCourse") != 0) return "circle class asked for the course";
            // PruneWeapons: a foreign unit never reaches Own(); an unidentified track never reaches Class()
            f = new LeakFacts { Contact = 7, Ident = false, Cls = cls, AlliesOn = true };
            f.Sides[2] = UnitSide.Other; f.Sides[3] = UnitSide.Ally;
            var o = new RingOptions { Pins = new RangeRingPins() };
            o.Pins.ToggleWeapon(2, "x"); o.Pins.ToggleWeapon(3, "y"); o.Pins.ToggleTrackWeapon(7, "h.ini");
            try { RangeRingModel.PruneWeapons(f, o.Pins, o); }
            catch (LeakException e) { return "PruneWeapons leak: " + e.Message; }
            if (f.Count("Own") != 0 || f.Count("Class") != 0 || f.Count("TrackCourse") != 0 || o.Pins.WeaponCount != 0) return "PruneWeapons reads " + Calls(f);
            if (f.Count("SideOf") != 2 || f.Count("IsIdentified") != 1) return "PruneWeapons calls " + Calls(f);
            return null;
        }

        /// <summary>T-164: OwnActive=false never asks for anything about own / allied units.</summary>
        private static string SelfTest164()
        {
            var f = new LeakFacts { Sel = new List<int> { 1, 2 }, AlliesOn = true, Contact = 7, Ident = true, Cls = Cls() };
            f.Sides[1] = UnitSide.Player; f.Sides[2] = UnitSide.Ally;
            var o = new RingOptions { Allies = true, OwnActive = false, Scope = "group", Pins = Track7() };
            o.Pins.ToggleWeapon(1, "m");
            RingScene s;
            try { s = RangeRingModel.Build(f, o); }
            catch (LeakException e) { return "OwnActive=false leak: " + e.Message; }
            foreach (string n in new[] { "Own", "OwnPose", "SideOf", "SelectedOwn", "GroupOf" })
                if (f.Count(n) != 0) return "OwnActive=false called " + n + ": " + Calls(f);
            string err = CheckClass(s, f);
            if (err != null) return "OwnActive=false: " + err;
            return null;
        }

        /// <summary>T-162: PinTarget and pinned tracks never read more than the contract allows.</summary>
        private static string SelfTest162()
        {
            var f = new LeakFacts { Ident = false, Cls = Cls() };
            if (RangeRingModel.PinTarget(f, 7) != RingPinTarget.None || Calls(f) != "TrackSide,IsIdentified") return "PinTarget unidentified: " + Calls(f);
            f = new LeakFacts { Ident = true, Cls = null };
            if (RangeRingModel.PinTarget(f, 7) != RingPinTarget.None || f.Count("Class") != 1) return "PinTarget no record: " + Calls(f);
            f = new LeakFacts { Ident = true, Cls = Cls() };
            if (RangeRingModel.PinTarget(f, 7) != RingPinTarget.Track || f.Count("Own") + f.Count("OwnPose") + f.Count("TrackPose") != 0) return "PinTarget identified: " + Calls(f);
            foreach (UnitSide side in new[] { UnitSide.Player, UnitSide.Ally })
            {
                f = new LeakFacts { ContactSide = side, Ident = true, Cls = Cls() };
                if (RangeRingModel.PinTarget(f, 7) != RingPinTarget.None || f.Count("IsIdentified") != 0) return "PinTarget " + side + ": " + Calls(f);
            }
            // a pinned unidentified track draws nothing and is not read past IsIdentified
            f = new LeakFacts { Ident = false, Cls = Cls() };
            RingScene ps = RangeRingModel.Build(f, new RingOptions { Pins = Track7() });
            if (ps.Units.Count != 0 || f.Count("Class") != 0 || f.Count("TrackPose") != 0) return "pinned unidentified track: " + Calls(f);
            // pinned tracks of our own / allied units never reach IsIdentified, Own or the class branch
            foreach (UnitSide side in new[] { UnitSide.Player, UnitSide.Ally })
            {
                f = new LeakFacts { ContactSide = side, Ident = true, Cls = Cls(), AlliesOn = true };
                ps = RangeRingModel.Build(f, new RingOptions { Allies = true, Pins = Track7() });
                if (ps.Units.Count != 0 || f.Count("IsIdentified") != 0 || f.Count("Own") + f.Count("OwnPose") != 0 || f.Count("Class") != 0) return "pinned own-side track took the class branch (" + side + "): " + Calls(f);
            }
            // pinned identified track (not selected): class rings only, centre = TrackPose, no own reads
            f = new LeakFacts { Ident = true, Cls = Cls() };
            ps = RangeRingModel.Build(f, new RingOptions { Pins = Track7() });
            string err = CheckClass(ps, f);
            if (err != null) return "pinned track: " + err;
            if (!ps.Units[0].Pinned || ps.Units[0].Selected || f.Count("Own") + f.Count("OwnPose") != 0) return "pinned track flags/reads: " + Calls(f);
            return null;
        }

        /// <summary>2026-10-22: a destroyed / lost track (selected or pinned) is asked TrackGone only: no side, identification, class or position read;
        /// PruneGoneTracks reads TrackGone only and drops the pin with the checked weapons.</summary>
        private static string SelfTestGone()
        {
            var f = new LeakFacts { Contact = 7, Ident = true, Cls = Cls() };
            f.Gone[7] = "destroyed"; f.Gone[8] = "lost";
            var o = new RingOptions { Pins = Track7() };
            o.Pins.ToggleTrackWeapon(8, "h.ini");
            RingScene s;
            try { s = RangeRingModel.Build(f, o); }
            catch (LeakException e) { return "gone leak: " + e.Message; }
            if (s.Units.Count != 0) return "gone tracks drew " + s.Units.Count;
            if (f.Count("TrackGone") != 2 || f.Count("TrackSide") + f.Count("IsIdentified") + f.Count("Class") + f.Count("TrackPose") + f.Count("TrackCourse") != 0) return "gone tracks read " + Calls(f);
            f.Log.Clear();
            var gone = RangeRingModel.PruneGoneTracks(f, o.Pins);
            if (gone.Count != 2 || !gone.Contains("track=8 reason=lost") || !gone.Contains("track=7 reason=destroyed") || o.Pins.TrackCount != 0 || o.Pins.WeaponCount != 0) return "PruneGoneTracks " + string.Join("|", gone);
            if (Calls(f) != "TrackGone,TrackGone") return "PruneGoneTracks reads " + Calls(f);
            // a live track: TrackGone first, then the usual order
            f = new LeakFacts { Contact = 7, Ident = true, Cls = Cls() };
            RangeRingModel.Build(f, new RingOptions { Pins = Track7() });
            if (f.Log.IndexOf("TrackGone") < 0 || f.Log.IndexOf("TrackGone") > f.Log.IndexOf("TrackSide")) return "TrackGone is not asked first: " + Calls(f);
            return null;
        }

        /// <summary>Class rings: SAM + AShM (checked) + passive sonar, all UpTo / Class, no sectors, centre = TrackPose.</summary>
        private static string CheckClass(RingScene s, LeakFacts f)
        {
            if (s.Units.Count != 1) return "units " + s.Units.Count;
            UnitRings u = s.Units[0];
            if (u.Sectors.Count != 0) return "sectors present";
            if (u.Center.Lat != f.TrackAt.Lat || u.Center.Lon != f.TrackAt.Lon) return "centre is not TrackPose";
            if (u.Rings.Count != 3) return "rings " + u.Rings.Count;
            foreach (RingSpec r in u.Rings)
            {
                if (!r.UpTo || r.Src != RingSource.Class) return "ring not UpTo/Class: " + r.Kind;
                if (r.Kind == RingKind.SonarActive) return "active sonar";
            }
            return null;
        }
    }
}
