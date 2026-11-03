// T-137 range rings: pure model. Facts of own units and the class reference of an identified contact -> RingScene. No Unity, no game.
// Side rule (ADR-0055): Own() is called only for Player (and Ally when Allies is on); Class() only after IsIdentified() == true.
// ADR-0068 supplement 2026-10-22 (customer): no category rings (SAM / AShM / ASW groups) and no fire-sector layer any more: a weapon ring is drawn
// only for the weapons the player checked on a unit / track; sonar rings stay.
using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    internal enum RingKind { Aaw, Asuw, Asw, SonarActive, SonarPassive }
    internal enum RingSource { Own, Ally, Class }
    internal enum RingTarget { Unknown, Aaw, Asuw, Asw }
    /// <summary>Ammunition.Type of the game. Other = unreadable; Unknown / None = the game's own "no type" (pods, containers, broken ini) - not a weapon (2026-10-22).</summary>
    internal enum RingAmmoType { Other, Projectile, Missile, Torpedo, Noisemaker, Chaff, Fueltank, Sonobuoy, Rbu, Asroc, AerialRocket, AirDepthCharge, Bomb, Mlrs, Cluster, Moss, Paratrooper, Unknown, None }
    internal enum UnitSide { Player, Ally, Other }
    /// <summary>T-162: whether the contact menu may offer the class weapons of a clicked track.</summary>
    internal enum RingPinTarget { None, Track }

    internal struct RingPt { public double Lat, Lon; public RingPt(double lat, double lon) { Lat = lat; Lon = lon; } }
    /// <summary>IsMin: inner minimum-range circle (drawn faint); MaxNm is then the minimum range itself. Weapon = the display name of the checked weapon
    /// (null for sonar rings); Kind = the weapon's category (colour only); Tint = the n-th checked weapon of the unit with the same kind and delivery.</summary>
    internal struct RingSpec { public RingKind Kind; public RingSource Src; public RingVia Via; public double MaxNm, MinNm; public bool UpTo, IsMin; public string Weapon; public int Tint; }
    /// <summary>A wedge: arc of RangeNm and two radii (ADR-0068 p. 4a: a weapon that fires only in sectors). IsMin = the minimum-range arc only (no radii);
    /// LabelHere = the widest wedge carries the label.</summary>
    internal struct SectorSpec { public RingKind Kind; public RingSource Src; public RingVia Via; public double CenterDeg, WidthDeg, RangeNm, MinNm; public bool UpTo, IsMin, LabelHere; public string Weapon; public int Tint; }

    internal sealed class UnitRings
    {
        public int Key; public bool Air, Selected, Pinned; public RingPt Center; public double HeadingDeg;
        /// <summary>T-321: nm per degree of the game world at the centre (rings are drawn in the metric of the game); !Valid = geodesic.</summary>
        public RingGeoMetric Metric;
        /// <summary>T-321: the checked own weapons of this unit (their verdict against the selected contact).</summary>
        public List<AmmoFact> Checked = new List<AmmoFact>();
        public List<RingSpec> Rings = new List<RingSpec>(); public List<SectorSpec> Sectors = new List<SectorSpec>();
    }

    internal struct RingLabel { public double Lat, Lon, Nm; public RingKind Kind; public RingVia Via; public RingSource Src; public bool Air, UpTo; public string Weapon; public int Tint; }

    /// <summary>T-321: what the launcher of the game says about the selected contact for one checked weapon of an own unit (numbers in game miles).</summary>
    internal struct RingVerdict { public int TrackId, Uid; public string Weapon; public KinVerdict Verdict; public double DistNm, MaxNm, MinNm; public RingPt At; }

    internal sealed class RingScene { public List<UnitRings> Units = new List<UnitRings>(); public List<RingVerdict> Verdicts = new List<RingVerdict>(); public int RingsCut; public bool AnyAir; public int Version; }

    internal sealed class KinCheck { public KinVerdict Verdict; public double DistNm, MaxNm, MinNm; }

    internal struct AmmoFact { public string Name; public RingTarget Primary, Secondary; public RingAmmoType Type; public double MinNm, MaxNm; public int Left; public bool AntiTorpedo; public string Display; /* game display name, null -> Name */ }
    internal struct ArcDeg { public double A, B; }
    internal sealed class MountFact { public List<ArcDeg> Arcs = new List<ArcDeg>(); public List<string> AmmoNames = new List<string>(); }
    internal struct SonarFact { public double ActiveNm, PassiveNm; }
    internal sealed class OwnArmament
    {
        public List<AmmoFact> Ammo = new List<AmmoFact>(); public List<MountFact> Mounts = new List<MountFact>(); public List<SonarFact> Sonars = new List<SonarFact>();
        public bool AmmoCountKnown;
    }

    internal struct ClassWeapon { public string Key, Name; public bool Air; public RingTarget Primary, Secondary; public bool HasSecondary; public RingAmmoType Type; public double MinNm, MaxNm, GunAaNm; }
    /// <summary>A mount of the class ini: Type (Gun / CIWS / Missile ...), SystemName, the ammunition it fires (file names) and its arcs.</summary>
    internal sealed class ClassMount { public string Type, System; public List<string> AmmoRefs = new List<string>(); public List<ArcDeg> Arcs = new List<ArcDeg>(); }
    internal sealed class ClassWeaponInfo { public ClassWeapon W; public List<List<ArcDeg>> MountArcs = new List<List<ArcDeg>>(); }   // empty = no mount found -> all-round
    internal sealed class ClassRanges
    {
        public string IniRef; public double PassiveSonarNm;
        public List<ClassWeaponInfo> Weapons = new List<ClassWeaponInfo>();
        public bool ArcsKnown;                                           // the class ini was read (vessels / land_units)
    }

    internal sealed class RingOptions
    {
        public string Scope = "selected";
        /// <summary>Show.Sonar: sonar rings of the selected own units (and their group with Scope=group) and the passive sonar of the selected / pinned contact.</summary>
        public bool Sonar = true, Allies;
        public int MaxRings = 24;
        public double MaxRingNm = 1000;
        /// <summary>Below this speed (kn) a track has no known course.</summary>
        public double CourseMinKn = 1.0;
        /// <summary>The checked weapons and sonar overrides; null = none.</summary>
        public RangeRingPins Pins;
        /// <summary>T-164: false = no own / allied rings at all (Better TacMap draws them); Own() is never called.</summary>
        public bool OwnActive = true;
    }

    internal interface IRingFacts
    {
        IList<int> SelectedOwn();
        IList<int> GroupOf(int uid);
        UnitSide SideOf(int uid);
        OwnArmament Own(int uid);
        bool OwnPose(int uid, out RingPt p, out double headingDeg, out bool air);
        int SelectedContact();
        UnitSide TrackSide(int trackId);
        bool IsIdentified(int trackId);
        bool TrackPose(int trackId, out RingPt p, out bool air);
        ClassRanges Class(int trackId);
        /// <summary>ADR-0068: the course of the player's TRACK (never the object); only after IsIdentified; false = unknown.</summary>
        bool TrackCourse(int trackId, out double courseDeg);
        /// <summary>2026-10-22: null = the track is there; "destroyed" = the game reports the unit behind it destroyed / removed; "lost" = the track left the
        /// player's plotting table. Asked first, before TrackSide, for the selected contact and every pinned track.</summary>
        string TrackGone(int trackId);
        /// <summary>T-321: the game's metric at a point (cached by cell); !Valid = geodesic. Never touches a unit.</summary>
        RingGeoMetric Metric(RingPt at);
        /// <summary>T-321: the effective radius (game miles) of a checked weapon of an own unit; NaN = not known (yet) -> the nominal range of the fact.</summary>
        double ReachNm(int uid, AmmoFact a);
        /// <summary>T-321: the launcher verdict of a checked weapon of an own unit against the player's track; null = not computed (yet) / unavailable.
        /// Needs only the side and the position of the track: never IsIdentified / Class.</summary>
        KinCheck VsTrack(int uid, AmmoFact a, int trackId);
    }

    internal static class RangeRingModel
    {
        /// <summary>Anti-submarine delivery weapons (RBU, ASROC, depth charges) always belong to ASW, whatever _targetType says.</summary>
        public static bool IsAswType(RingAmmoType t) { return t == RingAmmoType.Rbu || t == RingAmmoType.Asroc || t == RingAmmoType.AirDepthCharge; }

        /// <summary>T-160: the unit has a sonar (the "Sonar rings" menu item).</summary>
        public static bool HasSonar(OwnArmament a)
        {
            if (a == null) return false;
            foreach (SonarFact s in a.Sonars) if (s.ActiveNm > 0 || s.PassiveNm > 0) return true;
            return false;
        }

        private static bool Ok(double nm, RingOptions o) { return !double.IsNaN(nm) && !double.IsInfinity(nm) && nm > 0 && nm <= o.MaxRingNm; }

        /// <summary>T-171: the weapons of the class with the arcs of their mounts and the largest passive sonar range.</summary>
        public static ClassRanges BuildClass(string iniRef, IList<ClassWeapon> ws, IList<double> passiveNm, IList<ClassMount> mounts, bool arcsKnown)
        {
            var r = new ClassRanges { IniRef = iniRef, ArcsKnown = arcsKnown, Weapons = RangeRingReach.JoinClassArcs(ws, mounts) };
            if (passiveNm != null)
                foreach (double p in passiveNm)
                    if (!double.IsNaN(p) && p > r.PassiveSonarNm) r.PassiveSonarNm = p;
            return r;
        }

        /// <summary>T-162: whether the contact menu may offer the weapons of this track. Reads: TrackSide; for Other then IsIdentified; only when identified Class.</summary>
        public static RingPinTarget PinTarget(IRingFacts f, int trackId)
        {
            if (f == null || trackId < 0) return RingPinTarget.None;
            if (f.TrackSide(trackId) != UnitSide.Other) return RingPinTarget.None;
            if (!f.IsIdentified(trackId)) return RingPinTarget.None;
            return f.Class(trackId) == null ? RingPinTarget.None : RingPinTarget.Track;
        }

        private sealed class Budget { public int Left, Cut; public bool Take() { if (Left > 0) { Left--; return true; } Cut++; return false; } }

        /// <summary>The scene: the selected own units (and their group with Scope=group) for their sonar, the selected contact, the pinned units and tracks
        /// with their checked weapons. Order: selected own, selected contact, pinned own, pinned tracks, the rest of the group.</summary>
        public static RingScene Build(IRingFacts f, RingOptions o)
        {
            var scene = new RingScene();
            if (f == null) return scene;
            if (o == null) o = new RingOptions();
            var b = new Budget { Left = Math.Max(0, o.MaxRings) };

            IList<int> sel = o.OwnActive ? (f.SelectedOwn() ?? new int[0]) : new int[0];
            var selected = new HashSet<int>();
            var order = new List<int>();
            foreach (int uid in sel) if (selected.Add(uid)) order.Add(uid);
            if (o.OwnActive && o.Scope == "group")
            {
                foreach (int uid in sel)
                {
                    IList<int> g = f.GroupOf(uid);
                    if (g == null) continue;
                    foreach (int m in g) if (!selected.Contains(m) && !order.Contains(m)) order.Add(m);
                }
            }
            var inSel = new HashSet<int>(order);

            var done = new HashSet<int>();
            var rest = new List<int>();
            foreach (int uid in order) { if (selected.Contains(uid)) { done.Add(uid); AddOwn(f, o, b, scene, uid, true, true); } else rest.Add(uid); }
            var doneTracks = new HashSet<int>();
            int c = f.SelectedContact();
            if (c >= 0) { doneTracks.Add(c); AddClassUnit(f, o, b, scene, c, true, o.Pins != null && o.Pins.IsTrackPinned(c)); }
            if (o.Pins != null)
            {
                if (o.OwnActive) foreach (int uid in o.Pins.Pinned()) { if (done.Add(uid)) AddOwn(f, o, b, scene, uid, false, inSel.Contains(uid)); }
                foreach (int t in o.Pins.PinnedTracks()) { if (doneTracks.Add(t)) AddClassUnit(f, o, b, scene, t, false, true); }
            }
            if (o.OwnActive) foreach (int uid in rest) { if (done.Add(uid)) AddOwn(f, o, b, scene, uid, false, true); }

            scene.RingsCut = b.Cut;
            foreach (UnitRings u in scene.Units) u.Metric = f.Metric(u.Center);
            if (c >= 0) AddVerdicts(f, scene, c);
            return scene;
        }

        public const int MaxVerdicts = 4;

        /// <summary>T-321: the selected contact of the OTHER side: for every checked weapon of the own units of the scene what the launcher of the game would say
        /// (at most MaxVerdicts lines). Reads of the track: TrackGone, TrackSide, TrackPose and VsTrack; the class of the track is not needed.</summary>
        private static void AddVerdicts(IRingFacts f, RingScene scene, int c)
        {
            bool any = false;
            foreach (UnitRings x in scene.Units) if (x.Key >= 0 && x.Checked.Count > 0) { any = true; break; }
            if (!any) return;   // no checked own weapon: nothing to say, nothing more is read about the track
            if (f.TrackGone(c) != null || f.TrackSide(c) != UnitSide.Other) return;
            RingPt at; bool air;
            if (!f.TrackPose(c, out at, out air)) return;
            foreach (UnitRings u in scene.Units)
            {
                if (u.Key < 0) continue;
                foreach (AmmoFact a in u.Checked)
                {
                    if (scene.Verdicts.Count >= MaxVerdicts) return;
                    KinCheck k = f.VsTrack(u.Key, a, c);
                    if (k == null || k.Verdict == KinVerdict.Unknown) continue;
                    scene.Verdicts.Add(new RingVerdict { TrackId = c, Uid = u.Key, Weapon = string.IsNullOrEmpty(a.Display) ? a.Name : a.Display, Verdict = k.Verdict, DistNm = k.DistNm, MaxNm = k.MaxNm, MinNm = k.MinNm, At = at });
                }
            }
        }

        private sealed class Mins { public List<RingSpec> Rings = new List<RingSpec>(); public List<SectorSpec> Wedges = new List<SectorSpec>(); }

        /// <summary>One checked weapon: a ring when all-round, else the wedges (ADR-0068 p. 4a); its minimum range goes to mins.</summary>
        private static void EmitReach(UnitRings u, Mins mins, Budget b, RingKind kind, RingSource src, RingVia via, double max, double min, bool upTo, bool restricted, List<double[]> spans, string weapon, int tint)
        {
            bool withMin = min > 0 && min < max;
            if (!restricted)
            {
                if (b.Take()) u.Rings.Add(new RingSpec { Kind = kind, Src = src, Via = via, MaxNm = max, MinNm = min, UpTo = upTo, Weapon = weapon, Tint = tint });
                if (withMin) mins.Rings.Add(new RingSpec { Kind = kind, Src = src, Via = via, MaxNm = min, MinNm = min, UpTo = upTo, IsMin = true, Weapon = weapon, Tint = tint });
                return;
            }
            int wide = 0;
            for (int i = 1; i < spans.Count; i++) if (spans[i][1] > spans[wide][1]) wide = i;
            for (int i = 0; i < spans.Count; i++)
            {
                if (b.Take()) u.Sectors.Add(new SectorSpec { Kind = kind, Src = src, Via = via, CenterDeg = spans[i][0], WidthDeg = spans[i][1], RangeNm = max, MinNm = min, UpTo = upTo, LabelHere = i == wide, Weapon = weapon, Tint = tint });
                if (withMin) mins.Wedges.Add(new SectorSpec { Kind = kind, Src = src, Via = via, CenterDeg = spans[i][0], WidthDeg = spans[i][1], RangeNm = min, UpTo = upTo, IsMin = true, Weapon = weapon, Tint = tint });
            }
        }

        /// <summary>The shade of the next checked weapon of a unit: how many weapons before it had the same kind and delivery (= the same base colour).</summary>
        private static int NextTint(List<KeyValuePair<RingKind, RingVia>> seen, RingKind kind, RingVia via)
        {
            int n = 0;
            foreach (var e in seen) if (e.Key == kind && e.Value == via) n++;
            seen.Add(new KeyValuePair<RingKind, RingVia>(kind, via));
            return n;
        }

        private static bool OwnRestricted(OwnArmament arm, string key, out List<double[]> spans)
        {
            var arcs = new List<List<ArcDeg>>();
            foreach (MountFact m in arm.Mounts) if (m.AmmoNames.Contains(key)) arcs.Add(m.Arcs);
            return !RangeRingReach.Coverage(arcs, out spans);
        }

        /// <summary>An own / allied unit: the rings of its checked weapons and its sonar (the unit's override, else Show.Sonar inside the selection).</summary>
        private static void AddOwn(IRingFacts f, RingOptions o, Budget b, RingScene scene, int uid, bool selected, bool inSel)
        {
            bool pinned = o.Pins != null && o.Pins.IsPinned(uid);
            UnitSide side = f.SideOf(uid);
            if (side != UnitSide.Player && !(side == UnitSide.Ally && o.Allies)) return;   // Own() is never called for anyone else
            RingPt pos; double heading; bool air;
            if (!f.OwnPose(uid, out pos, out heading, out air)) return;
            OwnArmament arm = f.Own(uid);
            if (arm == null) return;
            RingSource src = side == UnitSide.Ally ? RingSource.Ally : RingSource.Own;
            var u = new UnitRings { Key = uid, Air = air, Selected = selected, Pinned = pinned, Center = pos, HeadingDeg = heading };
            var mins = new Mins();
            var shades = new List<KeyValuePair<RingKind, RingVia>>();

            if (o.Pins != null)
            {
                foreach (string wkey in o.Pins.Weapons(uid))
                {
                    foreach (AmmoFact a in arm.Ammo)
                    {
                        if (a.Name != wkey || !RangeRingReach.Candidate(a.Type, a.AntiTorpedo) || a.Left == 0) continue;
                        AmmoFact ar = a;
                        double rn = f.ReachNm(uid, a);   // T-321: the game's own radius; NaN = not known yet -> nominal
                        if (!double.IsNaN(rn) && !double.IsInfinity(rn) && rn > 0) ar.MaxNm = rn;
                        ReachIn w = RangeRingReach.InOf(ar);
                        RingKind kind = RangeRingReach.CategoryOf(w);
                        double max = RangeRingReach.RadiusOf(w, kind);
                        if (!Ok(max, o)) break;
                        List<double[]> spans;
                        bool restricted = OwnRestricted(arm, a.Name, out spans);
                        RingVia via = RangeRingReach.ViaFor(kind, a.Type);
                        EmitReach(u, mins, b, kind, src, via, max, Pos(a.MinNm), false, restricted, spans, string.IsNullOrEmpty(a.Display) ? a.Name : a.Display, NextTint(shades, kind, via));
                        u.Checked.Add(a);
                        break;
                    }
                }
            }

            bool sonarGlobal = inSel && o.Sonar;
            if (o.Pins != null ? o.Pins.SonarOn(uid, sonarGlobal) : sonarGlobal)
            {
                double act = 0, pas = 0;
                foreach (SonarFact s in arm.Sonars)
                {
                    if (Ok(s.ActiveNm, o) && s.ActiveNm > act) act = s.ActiveNm;
                    if (Ok(s.PassiveNm, o) && s.PassiveNm > pas) pas = s.PassiveNm;
                }
                if (act > 0 && b.Take()) u.Rings.Add(new RingSpec { Kind = RingKind.SonarActive, Src = src, MaxNm = act });
                if (pas > 0 && b.Take()) u.Rings.Add(new RingSpec { Kind = RingKind.SonarPassive, Src = src, MaxNm = pas });
            }

            foreach (RingSpec m in mins.Rings) if (b.Take()) u.Rings.Add(m);
            foreach (SectorSpec m in mins.Wedges) if (b.Take()) u.Sectors.Add(m);
            if (u.Rings.Count == 0 && u.Sectors.Count == 0) return;
            scene.Units.Add(u);
            if (air) scene.AnyAir = true;
        }

        private static double Pos(double v) { return double.IsNaN(v) || !(v > 0) ? 0 : v; }

        private static ClassWeaponInfo InfoOf(ClassRanges cr, string key)
        {
            if (key == null) return null;
            foreach (ClassWeaponInfo i in cr.Weapons) if (i.W.Key == key) return i;
            return null;
        }

        /// <summary>Wedges only for a class whose ini was read and whose weapon sits on mounts with limited arcs.</summary>
        private static bool ClassRestricted(ClassRanges cr, string key, out List<double[]> spans)
        {
            spans = new List<double[]>();
            if (!cr.ArcsKnown) return false;
            ClassWeaponInfo i = InfoOf(cr, key);
            if (i == null || i.MountArcs.Count == 0) return false;
            return !RangeRingReach.Coverage(i.MountArcs, out spans);
        }

        /// <summary>An identified foreign track (selected contact and/or pinned): the rings of its checked class weapons and, with Show.Sonar, its passive
        /// sonar. A destroyed / lost track draws nothing (TrackGone first, nothing else is read).</summary>
        private static void AddClassUnit(IRingFacts f, RingOptions o, Budget b, RingScene scene, int c, bool selected, bool pinned)
        {
            if (f.TrackGone(c) != null) return;                // destroyed or lost: no rings, no further calls
            if (f.TrackSide(c) != UnitSide.Other) return;      // a track of our own/allied unit never takes the class branch
            if (!f.IsIdentified(c)) return;                    // unidentified: nothing, no further calls
            ClassRanges cr = f.Class(c);
            if (cr == null) return;
            RingPt pos; bool air;
            if (!f.TrackPose(c, out pos, out air)) return;
            var u = new UnitRings { Key = -1 - c, Air = air, Selected = selected, Pinned = pinned, Center = pos };
            var mins = new Mins();
            int courseState = 0;   // 0 = not asked, 1 = known, 2 = unknown
            var shades = new List<KeyValuePair<RingKind, RingVia>>();

            if (o.Pins != null)
            {
                foreach (string wkey in o.Pins.TrackWeapons(c))
                {
                    ClassWeaponInfo info = InfoOf(cr, wkey);
                    if (info == null || !RangeRingReach.Candidate(info.W.Type, false)) continue;
                    ReachIn w = RangeRingReach.InOf(info.W);
                    RingKind kind = RangeRingReach.CategoryOf(w);
                    double max = RangeRingReach.RadiusOf(w, kind);
                    if (!Ok(max, o)) continue;
                    List<double[]> spans;
                    bool restricted = ClassRestricted(cr, wkey, out spans) && Course(f, c, u, ref courseState);
                    RingVia via = RangeRingReach.ViaFor(kind, info.W.Type);
                    EmitReach(u, mins, b, kind, RingSource.Class, via, max, Pos(info.W.MinNm), true, restricted, spans, string.IsNullOrEmpty(info.W.Name) ? info.W.Key : info.W.Name, NextTint(shades, kind, via));
                }
            }
            if (o.Sonar && Ok(cr.PassiveSonarNm, o) && b.Take())
                u.Rings.Add(new RingSpec { Kind = RingKind.SonarPassive, Src = RingSource.Class, MaxNm = cr.PassiveSonarNm, UpTo = true });
            foreach (RingSpec m in mins.Rings) if (b.Take()) u.Rings.Add(m);
            foreach (SectorSpec m in mins.Wedges) if (b.Take()) u.Sectors.Add(m);
            if (u.Rings.Count == 0 && u.Sectors.Count == 0) return;
            scene.Units.Add(u);
            if (air) scene.AnyAir = true;
        }

        /// <summary>The course of the track (asked at most once per unit, only when wedges would be needed); false = unknown -> the caller draws a circle.</summary>
        private static bool Course(IRingFacts f, int c, UnitRings u, ref int state)
        {
            if (state == 0)
            {
                double deg;
                if (f.TrackCourse(c, out deg) && !double.IsNaN(deg) && !double.IsInfinity(deg)) { state = 1; u.HeadingDeg = deg; } else state = 2;
            }
            return state == 1;
        }

        /// <summary>ADR-0068 p. 3: drops checked weapons that left the list, one by one (own: SideOf first, then Own(); track: TrackSide / IsIdentified, then
        /// Class()). The other checked weapons of the unit stay; the last one going unpins. Returns "uid=5 key=x" / "track=9 key=y".</summary>
        public static List<string> PruneWeapons(IRingFacts f, RangeRingPins pins, RingOptions o)
        {
            var gone = new List<string>();
            if (f == null || pins == null) return gone;
            if (o == null) o = new RingOptions();
            if (o.OwnActive)
            {
                foreach (int uid in pins.WeaponUnits())
                {
                    List<WeaponOpt> list = f.SideOf(uid) == UnitSide.Player ? RangeRingReach.OwnWeapons(f.Own(uid), int.MaxValue) : null;
                    foreach (string key in pins.Weapons(uid))
                    {
                        if (list != null && HasKey(list, key)) continue;
                        pins.RemoveWeapon(uid, key);
                        gone.Add("uid=" + uid + " key=" + key);
                    }
                }
            }
            foreach (int t in pins.WeaponTracks())
            {
                bool ok = f.TrackSide(t) == UnitSide.Other && f.IsIdentified(t);
                List<WeaponOpt> list = ok ? RangeRingReach.ClassWeapons(f.Class(t), int.MaxValue) : null;
                foreach (string key in pins.TrackWeapons(t))
                {
                    if (list != null && HasKey(list, key)) continue;
                    pins.RemoveTrackWeapon(t, key);
                    gone.Add("track=" + t + " key=" + key);
                }
            }
            return gone;
        }

        /// <summary>2026-10-22: drops the pin and the checked weapons of every pinned track that is destroyed or lost (TrackGone, nothing else is read).
        /// Returns "track=9 reason=destroyed".</summary>
        public static List<string> PruneGoneTracks(IRingFacts f, RangeRingPins pins)
        {
            var gone = new List<string>();
            if (f == null || pins == null) return gone;
            foreach (int t in pins.PinnedTracks())
            {
                string why = f.TrackGone(t);
                if (why == null) continue;
                pins.DropTrack(t);
                gone.Add("track=" + t + " reason=" + why);
            }
            return gone;
        }

        private static bool HasKey(List<WeaponOpt> l, string key)
        {
            foreach (WeaponOpt w in l) if (w.Key == key) return true;
            return false;
        }

        // ---- labels ---------------------------------------------------------------------------------------------------------------------
        /// <summary>ADR-0068 p. 5: where a label goes. A ring: its bearing by kind (+12 deg per shade, so two weapons of one kind do not overlap); a wedge-only
        /// weapon: the middle of its widest wedge. Radius below minPx (pxPerNm &lt;= 0 = unknown -> label) -> none.</summary>
        public static List<RingLabel> Labels(RingScene s, double pxPerNm, double minPx, int max)
        {
            var res = new List<RingLabel>();
            if (s == null) return res;
            foreach (UnitRings u in s.Units)
            {
                foreach (RingSpec r in u.Rings)
                {
                    if (res.Count >= max) return res;
                    if (r.IsMin || !(r.MaxNm > 0) || (pxPerNm > 0 && r.MaxNm * pxPerNm < minPx)) continue;
                    double lat, lon;
                    RangeRingKin.OffsetMetric(u.Center.Lat, u.Center.Lon, RangeRingReach.LabelBearing(r.Kind, r.Src == RingSource.Class) + 12.0 * r.Tint, r.MaxNm, u.Metric, out lat, out lon);
                    res.Add(new RingLabel { Lat = lat, Lon = lon, Nm = r.MaxNm, Kind = r.Kind, Via = r.Via, Src = r.Src, Air = u.Air, UpTo = r.UpTo, Weapon = r.Weapon, Tint = r.Tint });
                }
                foreach (SectorSpec c in u.Sectors)
                {
                    if (res.Count >= max) return res;
                    if (c.IsMin || !c.LabelHere || !(c.RangeNm > 0) || (pxPerNm > 0 && c.RangeNm * pxPerNm < minPx)) continue;
                    double lat, lon;
                    RangeRingKin.OffsetMetric(u.Center.Lat, u.Center.Lon, u.HeadingDeg + c.CenterDeg, c.RangeNm, u.Metric, out lat, out lon);
                    res.Add(new RingLabel { Lat = lat, Lon = lon, Nm = c.RangeNm, Kind = c.Kind, Via = c.Via, Src = c.Src, Air = u.Air, UpTo = c.UpTo, Weapon = c.Weapon, Tint = c.Tint });
                }
            }
            return res;
        }

        // ---- self test ------------------------------------------------------------------------------------------------------------------
        internal sealed class FakeFacts : IRingFacts
        {
            public List<int> Sel = new List<int>();
            public Dictionary<int, List<int>> Groups = new Dictionary<int, List<int>>();
            public Dictionary<int, UnitSide> Sides = new Dictionary<int, UnitSide>();
            public Dictionary<int, OwnArmament> Arm = new Dictionary<int, OwnArmament>();
            public Dictionary<int, bool> Air = new Dictionary<int, bool>();
            public int Contact = -1; public UnitSide ContactSide = UnitSide.Other; public bool Ident; public ClassRanges Cls; public RingPt TrackAt = new RingPt(5, 6);
            public Dictionary<int, ClassRanges> ClsOf = new Dictionary<int, ClassRanges>();
            public Dictionary<int, bool> IdentOf = new Dictionary<int, bool>();
            public Dictionary<int, UnitSide> TrackSides = new Dictionary<int, UnitSide>();
            public Dictionary<int, string> Gone = new Dictionary<int, string>();
            public IList<int> SelectedOwn() { return Sel; }
            public IList<int> GroupOf(int uid) { List<int> g; return Groups.TryGetValue(uid, out g) ? g : null; }
            public UnitSide SideOf(int uid) { UnitSide s; return Sides.TryGetValue(uid, out s) ? s : UnitSide.Player; }
            public OwnArmament Own(int uid) { return Arm[uid]; }
            public bool OwnPose(int uid, out RingPt p, out double h, out bool air) { p = new RingPt(uid, uid * 2); h = 90; bool a; air = Air.TryGetValue(uid, out a) && a; return true; }
            public int SelectedContact() { return Contact; }
            public UnitSide TrackSide(int id) { UnitSide v; return TrackSides.TryGetValue(id, out v) ? v : ContactSide; }
            public bool IsIdentified(int id) { bool v; return IdentOf.TryGetValue(id, out v) ? v : Ident; }
            public bool TrackPose(int id, out RingPt p, out bool air) { p = TrackAt; air = false; return true; }
            public ClassRanges Class(int id) { ClassRanges v; return ClsOf.TryGetValue(id, out v) ? v : Cls; }
            public double? Course;   // the course of every track; null = unknown
            public bool TrackCourse(int id, out double deg) { deg = Course ?? 0; return Course.HasValue; }
            public string TrackGone(int id) { string v; return Gone.TryGetValue(id, out v) ? v : null; }
            public RingGeoMetric MetricAt;                                  // the metric of every point (default = geodesic)
            public RingGeoMetric Metric(RingPt at) { return MetricAt; }
            public Dictionary<string, double> Reach = new Dictionary<string, double>();   // ammo name -> game radius; absent = NaN
            public double ReachNm(int uid, AmmoFact a) { double v; return Reach.TryGetValue(a.Name, out v) ? v : double.NaN; }
            public Dictionary<string, KinCheck> Checks = new Dictionary<string, KinCheck>();   // ammo name -> verdict; absent = null
            public int VsTrackCalls;
            public KinCheck VsTrack(int uid, AmmoFact a, int trackId) { VsTrackCalls++; KinCheck v; return Checks.TryGetValue(a.Name, out v) ? v : null; }
        }

        internal static AmmoFact Am(string n, RingTarget p, double max, int left, RingTarget s = RingTarget.Unknown, double min = 0, RingAmmoType t = RingAmmoType.Missile, bool at = false)
        {
            return new AmmoFact { Name = n, Primary = p, Secondary = s, Type = t, MinNm = min, MaxNm = max, Left = left, AntiTorpedo = at };
        }

        private static OwnArmament Arm(params AmmoFact[] ammo) { var a = new OwnArmament(); a.Ammo.AddRange(ammo); return a; }

        private static FakeFacts One(params AmmoFact[] ammo)
        {
            var f = new FakeFacts();
            f.Sel.Add(1);
            f.Arm[1] = Arm(ammo);
            return f;
        }

        private static RingSpec? Find(UnitRings u, RingKind k, bool min = false)
        {
            foreach (RingSpec r in u.Rings) if (r.Kind == k && r.IsMin == min) return r;
            return null;
        }

        private static string Keys(RingScene s)
        {
            var l = new List<string>();
            foreach (UnitRings u in s.Units) l.Add(u.Key.ToString());
            return string.Join(",", l.ToArray());
        }

        private static MountFact Mount(string ammo, double a, double b)
        {
            var m = new MountFact(); m.Arcs.Add(new ArcDeg { A = a, B = b }); m.AmmoNames.Add(ammo);
            return m;
        }

        /// <summary>A class with one SAM ("sam", all-round), one AShM ("ashm") and a passive sonar.</summary>
        internal static ClassRanges TestClass(string ini, double sam, double ashm, double passive)
        {
            var ws = new List<ClassWeapon>();
            if (sam > 0) ws.Add(new ClassWeapon { Key = "sam", Name = "SAM-" + ini, Type = RingAmmoType.Missile, Primary = RingTarget.Aaw, MaxNm = sam });
            if (ashm > 0) ws.Add(new ClassWeapon { Key = "ashm", Name = "AShM-" + ini, Type = RingAmmoType.Missile, Primary = RingTarget.Asuw, MaxNm = ashm });
            return BuildClass(ini, ws, passive > 0 ? new List<double> { passive } : new List<double>(), null, false);
        }

        private static ClassRanges HarpoonClass(bool arcsKnown)
        {
            var ws = new List<ClassWeapon>
            {
                new ClassWeapon { Key = "harpoon.ini", Name = "Harpoon", Type = RingAmmoType.Missile, Primary = RingTarget.Asuw, MaxNm = 67, MinNm = 3 },
                new ClassWeapon { Key = "sm2.ini", Name = "SM-2", Type = RingAmmoType.Missile, Primary = RingTarget.Aaw, MaxNm = 90 },
                new ClassWeapon { Key = "mk45", Name = "5in", Type = RingAmmoType.Projectile, Primary = RingTarget.Asuw, MaxNm = 14, Secondary = RingTarget.Aaw, HasSecondary = true, GunAaNm = 7 }
            };
            var mounts = new List<ClassMount>
            {
                new ClassMount { Type = "Missile", System = "HarpoonLauncher", AmmoRefs = { "harpoon.ini" }, Arcs = { new ArcDeg { A = -135, B = 135 } } },
                new ClassMount { Type = "Gun", System = "MK45", Arcs = { new ArcDeg { A = -150, B = 150 } } }
            };
            return BuildClass("cls", ws, new List<double> { 12 }, mounts, arcsKnown);
        }

        private static string AmmoTypeNamesOk()
        {
            // the adapter parses Ammunition.Type names with Enum.TryParse(ignoreCase): every game name must have a member here
            foreach (string n in new[] { "Projectile", "Missile", "RBU", "ASROC", "Torpedo", "Noisemaker", "Chaff", "Bomb", "AerialRocket", "Sonobuoy", "Fueltank", "AirDepthCharge" })
            {
                RingAmmoType t;
                if (!Enum.TryParse(n, true, out t) || t == RingAmmoType.Other) return "ammo type name " + n;
            }
            return null;
        }

        public static string SelfTest()
        {
            return AmmoTypeNamesOk() ?? SelfTestOwn() ?? SelfTestSonarScope() ?? SelfTestClass() ?? SelfTestPrune() ?? SelfTestLabels();
        }

        /// <summary>Own units: nothing without a checked weapon; each checked weapon its ring (+ minimum), shades, wedges, empty magazine, side filter.</summary>
        private static string SelfTestOwn()
        {
            var sonarOff = new RingOptions { Sonar = false, Pins = new RangeRingPins() };
            var f = One(Am("sam", RingTarget.Aaw, 60, 2), new AmmoFact { Name = "harpoon", Display = "Harpoon", Primary = RingTarget.Asuw, Type = RingAmmoType.Missile, MaxNm = 67, MinNm = 3, Left = 4 },
                Am("rbu", RingTarget.Unknown, 3, 5, RingTarget.Unknown, 0, RingAmmoType.Rbu), Am("decoy", RingTarget.Unknown, 9, 5, RingTarget.Unknown, 0, RingAmmoType.Noisemaker));
            f.Arm[1].Sonars.Add(new SonarFact { ActiveNm = 8, PassiveNm = 30 });
            if (Build(f, sonarOff).Units.Count != 0) return "no checked weapon: no rings (no category rings any more)";
            var o = new RingOptions { Sonar = false, Pins = new RangeRingPins() };
            o.Pins.ToggleWeapon(1, "harpoon");
            RingScene s = Build(f, o);
            if (s.Units.Count != 1 || !s.Units[0].Selected || !s.Units[0].Pinned) return "one weapon: unit " + Keys(s);
            UnitRings u = s.Units[0];
            RingSpec? h = Find(u, RingKind.Asuw), hm = Find(u, RingKind.Asuw, true);
            if (h == null || h.Value.Weapon != "Harpoon" || h.Value.MaxNm != 67 || h.Value.Tint != 0 || h.Value.Src != RingSource.Own || h.Value.UpTo || hm == null || hm.Value.MaxNm != 3) return "own weapon ring + minimum";
            if (u.Rings.Count != 2 || Find(u, RingKind.SonarActive) != null) return "only the checked weapon, sonar off";
            // SM-2 and Harpoon at once; RBU is an ASW weapon
            o.Pins.ToggleWeapon(1, "sam"); o.Pins.ToggleWeapon(1, "rbu");
            u = Build(f, o).Units[0];
            RingSpec? a = Find(u, RingKind.Aaw), r = Find(u, RingKind.Asw);
            if (Find(u, RingKind.Asuw) == null || a == null || a.Value.Weapon != "sam" || a.Value.MaxNm != 60 || r == null || r.Value.Via != RingVia.Asw) return "own three weapons";
            o.Pins.ToggleWeapon(1, "harpoon");
            u = Build(f, o).Units[0];
            if (Find(u, RingKind.Asuw) != null || Find(u, RingKind.Aaw) == null) return "Harpoon unchecked, SAM stays";
            // a decoy is not a weapon; an empty magazine draws nothing
            o.Pins.ToggleWeapon(1, "decoy");
            if (Build(f, o).Units[0].Rings.Exists(x => x.Weapon == "decoy")) return "decoy drew a ring";
            var fe = One(Am("sam", RingTarget.Aaw, 60, 0));
            var oe = new RingOptions { Sonar = false, Pins = new RangeRingPins() };
            oe.Pins.ToggleWeapon(1, "sam");
            if (Build(fe, oe).Units.Count != 0) return "empty magazine drew a ring";
            // two weapons of the same kind and delivery get different shades
            var f2 = One(Am("sm2", RingTarget.Aaw, 90, 2), Am("essm", RingTarget.Aaw, 27, 8));
            var o2 = new RingOptions { Pins = new RangeRingPins() };
            o2.Pins.ToggleWeapon(1, "sm2"); o2.Pins.ToggleWeapon(1, "essm");
            var aaw = Build(f2, o2).Units[0].Rings.FindAll(x => x.Kind == RingKind.Aaw && !x.IsMin);
            if (aaw.Count != 2 || aaw[0].Tint != 0 || aaw[1].Tint != 1 || aaw[1].MaxNm != 27) return "same colour weapons: shades " + aaw.Count;
            // circle or wedges: a mount of +-140 deg -> wedges only (no ring), two mounts covering all round -> a circle
            var f3 = One(Am("harpoon", RingTarget.Asuw, 67, 4, RingTarget.Unknown, 3));
            f3.Arm[1].Mounts.Add(Mount("harpoon", -140, 140));
            var o3 = new RingOptions { Sonar = false, Pins = new RangeRingPins() };
            o3.Pins.ToggleWeapon(1, "harpoon");
            u = Build(f3, o3).Units[0];
            if (Find(u, RingKind.Asuw) != null || u.Sectors.FindAll(x => !x.IsMin).Count != 1 || !u.Sectors.Exists(x => x.IsMin && x.RangeNm == 3) || u.Sectors[0].WidthDeg != 280 || !u.Sectors[0].LabelHere) return "wedges replace the ring";
            f3.Arm[1].Mounts.Add(Mount("harpoon", 100, -100));
            if (Find(Build(f3, o3).Units[0], RingKind.Asuw) == null) return "mounts all round -> circle";
            // the side filter: an ally only with Allies; anyone else never reaches Own()
            var f4 = One(Am("sam", RingTarget.Aaw, 60, 2));
            f4.Sel.Add(2); f4.Arm[2] = Arm(Am("sam", RingTarget.Aaw, 60, 2)); f4.Sides[2] = UnitSide.Ally;
            f4.Sel.Add(3); f4.Sides[3] = UnitSide.Other;
            var o4 = new RingOptions { Pins = new RangeRingPins() };
            o4.Pins.ToggleWeapon(1, "sam"); o4.Pins.ToggleWeapon(2, "sam"); o4.Pins.ToggleWeapon(3, "sam");
            if (Keys(Build(f4, o4)) != "1") return "ally off: " + Keys(Build(f4, o4));
            o4.Allies = true;
            s = Build(f4, o4);
            if (Keys(s) != "1,2" || s.Units[1].Rings[0].Src != RingSource.Ally) return "ally on: " + Keys(s);
            // OwnActive=false: no own rings at all
            o4.OwnActive = false;
            if (Build(f4, o4).Units.Count != 0) return "own off";
            // budget and the air flag
            var f5 = One(Am("sm2", RingTarget.Aaw, 90, 2, RingTarget.Unknown, 5), Am("essm", RingTarget.Aaw, 27, 8, RingTarget.Unknown, 1));
            f5.Air[1] = true;
            var o5 = new RingOptions { MaxRings = 3, Pins = new RangeRingPins() };
            o5.Pins.ToggleWeapon(1, "sm2"); o5.Pins.ToggleWeapon(1, "essm");
            s = Build(f5, o5);
            if (s.Units[0].Rings.Count != 3 || s.RingsCut != 1 || !s.AnyAir) return "budget " + s.Units[0].Rings.Count + "/" + s.RingsCut;
            return null;
        }

        /// <summary>Sonar rings: Show.Sonar for the selection (and the group with Scope=group), the per-unit override, the pin order.</summary>
        private static string SelfTestSonarScope()
        {
            Func<FakeFacts> mk = () =>
            {
                var f = new FakeFacts();
                foreach (int id in new[] { 1, 2, 3, 5 })
                {
                    var a = Arm(Am("sam", RingTarget.Aaw, 80, 1));
                    a.Sonars.Add(new SonarFact { ActiveNm = 8, PassiveNm = 30 });
                    f.Arm[id] = a;
                }
                f.Sel.Add(1); f.Groups[1] = new List<int> { 1, 2, 3 };
                return f;
            };
            var o = new RingOptions { Pins = new RangeRingPins() };
            RingScene s = Build(mk(), o);
            if (Keys(s) != "1" || Find(s.Units[0], RingKind.SonarActive)?.MaxNm != 8 || Find(s.Units[0], RingKind.SonarPassive)?.MaxNm != 30) return "sonar of the selected unit";
            o.Scope = "group";
            if (Keys(Build(mk(), o)) != "1,2,3") return "sonar of the group " + Keys(Build(mk(), o));
            o.Sonar = false;
            if (Build(mk(), o).Units.Count != 0) return "Show.Sonar off";
            // the override of one unit: on pins it (visible outside the selection), off hides it in the selection
            o.Pins.ToggleSonar(5, false);
            o.Pins.ToggleWeapon(2, "sam");
            s = Build(mk(), o);
            if (Keys(s) != "5,2" || Find(s.Units[0], RingKind.SonarPassive) == null || s.Units[0].Selected || !s.Units[0].Pinned) return "sonar pin + weapon pin order " + Keys(s);
            o.Sonar = true;
            o.Pins.ToggleSonar(1, true);
            s = Build(mk(), o);
            if (s.Units.Exists(x => x.Key == 1)) return "sonar override off of the selected unit";
            if (Find(s.Units.Find(x => x.Key == 2), RingKind.SonarPassive) == null || Find(s.Units.Find(x => x.Key == 2), RingKind.Aaw) == null) return "group member: sonar + weapon";
            if (!HasSonar(mk().Arm[1]) || HasSonar(new OwnArmament()) || HasSonar(null)) return "HasSonar";
            return null;
        }

        /// <summary>Foreign tracks: checked class weapons (UpTo, named, wedges along the course), passive sonar, order, destroyed / lost tracks.</summary>
        private static string SelfTestClass()
        {
            var o = new RingOptions { Sonar = false, Pins = new RangeRingPins() };
            var f = new FakeFacts { Contact = 9, Ident = true, Cls = HarpoonClass(true), Course = 90 };
            if (Build(f, o).Units.Count != 0) return "selected contact without a checked weapon: nothing";
            o.Sonar = true;
            RingScene s = Build(f, o);
            if (s.Units.Count != 1 || s.Units[0].Rings.Count != 1 || s.Units[0].Rings[0].Kind != RingKind.SonarPassive || !s.Units[0].Rings[0].UpTo || s.Units[0].Key != -10) return "contact passive sonar";
            o.Sonar = false;
            o.Pins.ToggleTrackWeapon(9, "harpoon.ini");
            UnitRings u = Build(f, o).Units[0];
            if (!u.Pinned || !u.Selected || u.HeadingDeg != 90 || Find(u, RingKind.Aaw) != null || !u.Sectors.Exists(x => !x.IsMin && x.Weapon == "Harpoon" && x.UpTo && x.Src == RingSource.Class && x.WidthDeg == 270) || !u.Sectors.Exists(x => x.IsMin && x.RangeNm == 3)) return "class weapon wedges along the course";
            o.Pins.ToggleTrackWeapon(9, "mk45");
            u = Build(f, o).Units[0];
            if (!u.Sectors.Exists(x => x.Weapon == "Harpoon") || !u.Sectors.Exists(x => x.Via == RingVia.Gun && x.Kind == RingKind.Asuw && x.RangeNm == 14)) return "class: Harpoon and the gun at once";
            f.Course = null;
            u = Build(f, o).Units[0];
            if (Find(u, RingKind.Asuw) == null || u.Sectors.Count != 0) return "class without a course: circles";
            f.Cls = HarpoonClass(false); f.Course = 90;
            if (Build(f, o).Units[0].Sectors.Count != 0) return "!ArcsKnown -> circle";
            o.Pins.ToggleTrackWeapon(9, "sm2.ini");
            if (Find(Build(f, o).Units[0], RingKind.Aaw) == null) return "class SAM";
            // pinned tracks after the selected contact; unidentified / own-side tracks draw nothing
            var f2 = new FakeFacts { Contact = 9, Ident = true, Cls = HarpoonClass(false) };
            f2.Sel.Add(1); f2.Arm[1] = Arm(Am("sam", RingTarget.Aaw, 60, 2));
            f2.IdentOf[30] = false; f2.TrackSides[31] = UnitSide.Ally;
            var o2 = new RingOptions { Pins = new RangeRingPins() };
            o2.Pins.ToggleWeapon(1, "sam");
            foreach (int t in new[] { 21, 9, 30, 31 }) o2.Pins.ToggleTrackWeapon(t, "harpoon.ini");
            s = Build(f2, o2);
            if (Keys(s) != "1,-10,-22") return "track order " + Keys(s);
            if (s.Units.FindAll(x => x.Key == -10).Count != 1 || !s.Units[1].Selected || !s.Units[1].Pinned || s.Units[2].Selected) return "contact and pinned: once";
            // destroyed / lost
            f2.Gone[9] = "destroyed"; f2.Gone[21] = "lost";
            if (Keys(Build(f2, o2)) != "1") return "gone tracks drew " + Keys(Build(f2, o2));
            // PinTarget
            var f3 = new FakeFacts { Ident = true, Cls = HarpoonClass(false) };
            f3.IdentOf[30] = false; f3.TrackSides[31] = UnitSide.Ally; f3.ClsOf[32] = null; f3.IdentOf[32] = true;
            if (PinTarget(f3, 21) != RingPinTarget.Track || PinTarget(f3, 30) != RingPinTarget.None || PinTarget(f3, 31) != RingPinTarget.None || PinTarget(f3, -1) != RingPinTarget.None) return "PinTarget";
            var c = HarpoonClass(true);
            if (c.PassiveSonarNm != 12 || c.Weapons.Count != 3 || c.Weapons[0].MountArcs.Count != 1 || c.Weapons[2].MountArcs.Count != 1 || c.Weapons[1].MountArcs.Count != 0 || !c.ArcsKnown) return "BuildClass";
            return null;
        }

        /// <summary>PruneWeapons per weapon, PruneGoneTracks.</summary>
        private static string SelfTestPrune()
        {
            var f = One(Am("sm2", RingTarget.Aaw, 90, 0), Am("harpoon", RingTarget.Asuw, 67, 4));
            f.Sides[2] = UnitSide.Other;
            f.Contact = 9; f.Ident = true; f.Cls = HarpoonClass(true);
            var o = new RingOptions { Pins = new RangeRingPins() };
            o.Pins.ToggleWeapon(1, "sm2"); o.Pins.ToggleWeapon(1, "harpoon"); o.Pins.ToggleWeapon(2, "x"); o.Pins.ToggleTrackWeapon(9, "gone.ini"); o.Pins.ToggleTrackWeapon(9, "harpoon.ini");
            List<string> gone = PruneWeapons(f, o.Pins, o);
            if (gone.Count != 3 || !gone.Contains("uid=1 key=sm2") || !gone.Contains("uid=2 key=x") || !gone.Contains("track=9 key=gone.ini")) return "PruneWeapons " + string.Join("|", gone);
            if (!o.Pins.HasWeapon(1, "harpoon") || !o.Pins.IsPinned(1) || o.Pins.IsPinned(2) || !o.Pins.HasTrackWeapon(9, "harpoon.ini")) return "PruneWeapons per weapon";
            if (PruneWeapons(f, o.Pins, o).Count != 0) return "PruneWeapons keeps valid choices";
            f.IdentOf[9] = false;
            gone = PruneWeapons(f, o.Pins, o);
            if (gone.Count != 1 || gone[0] != "track=9 key=harpoon.ini" || o.Pins.IsTrackPinned(9)) return "PruneWeapons: identification lost";
            o.OwnActive = false;
            f.Arm.Remove(1);
            if (PruneWeapons(f, o.Pins, o).Count != 0) return "PruneWeapons with OwnActive=false does not read own units";
            // PruneGoneTracks
            var fg = new FakeFacts();
            var og = new RingOptions { Pins = new RangeRingPins() };
            og.Pins.ToggleTrackWeapon(21, "a"); og.Pins.ToggleTrackWeapon(22, "a"); og.Pins.ToggleTrackWeapon(23, "a");
            fg.Gone[21] = "destroyed"; fg.Gone[22] = "lost";
            List<string> gt = PruneGoneTracks(fg, og.Pins);
            if (gt.Count != 2 || !gt.Contains("track=21 reason=destroyed") || !gt.Contains("track=22 reason=lost") || og.Pins.TrackCount != 1 || !og.Pins.IsTrackPinned(23)) return "PruneGoneTracks " + string.Join("|", gt);
            if (PruneGoneTracks(fg, og.Pins).Count != 0) return "PruneGoneTracks twice";
            return null;
        }

        private static string SelfTestLabels()
        {
            var sc = new RingScene();
            var u = new UnitRings { Key = 1, Center = new RingPt(10, 20), HeadingDeg = 90 };
            u.Rings.Add(new RingSpec { Kind = RingKind.Asuw, Src = RingSource.Own, Via = RingVia.Missile, MaxNm = 100, Weapon = "Harpoon" });
            u.Rings.Add(new RingSpec { Kind = RingKind.Asuw, Src = RingSource.Own, Via = RingVia.Missile, MaxNm = 5, IsMin = true, Weapon = "Harpoon" });
            u.Rings.Add(new RingSpec { Kind = RingKind.Aaw, Src = RingSource.Class, Via = RingVia.Missile, MaxNm = 10, UpTo = true, Weapon = "SM-2" });
            u.Sectors.Add(new SectorSpec { Kind = RingKind.Asw, Src = RingSource.Own, Via = RingVia.Asw, CenterDeg = 0, WidthDeg = 60, RangeNm = 8 });
            u.Sectors.Add(new SectorSpec { Kind = RingKind.Asw, Src = RingSource.Own, Via = RingVia.Asw, CenterDeg = 180, WidthDeg = 120, RangeNm = 8, LabelHere = true });
            u.Rings.Add(new RingSpec { Kind = RingKind.Aaw, Src = RingSource.Own, Via = RingVia.Missile, MaxNm = 27, Weapon = "ESSM", Tint = 1 });
            sc.Units.Add(u);
            List<RingLabel> ls = Labels(sc, 0, 40, 24);
            if (ls.Count != 4) return "labels without a scale: " + ls.Count;
            double lat, lon;
            RangeRingMath.Offset(10, 20, 20, 100, out lat, out lon);
            if (Math.Abs(ls[0].Lat - lat) > 1e-9 || Math.Abs(ls[0].Lon - lon) > 1e-9 || ls[0].Nm != 100 || ls[0].Weapon != "Harpoon") return "own AShM label at 20 deg";
            RangeRingMath.Offset(10, 20, 240, 10, out lat, out lon);
            if (Math.Abs(ls[1].Lat - lat) > 1e-9 || !ls[1].UpTo || ls[1].Src != RingSource.Class) return "class SAM label at 60 + 180 deg";
            RangeRingMath.Offset(10, 20, 72, 27, out lat, out lon);
            if (Math.Abs(ls[2].Lat - lat) > 1e-9 || ls[2].Tint != 1) return "second SAM shade: label 12 deg further";
            RangeRingMath.Offset(10, 20, 90 + 180, 8, out lat, out lon);
            if (Math.Abs(ls[3].Lon - lon) > 1e-9 || Math.Abs(ls[3].Lat - lat) > 1e-9) return "wedge label in the middle of the wide wedge";
            ls = Labels(sc, 0.5, 40, 24);   // 100 nm * 0.5 = 50 px; the rest is below 40 px
            if (ls.Count != 1 || ls[0].Nm != 100) return "labels below minPx: " + ls.Count;
            if (Labels(sc, 0, 40, 2).Count != 2 || Labels(null, 0, 40, 2).Count != 0) return "labels max";
            return null;
        }
    }
}
