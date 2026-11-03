// T-179 / ADR-0079: the CAP "Alert" group kind - pure math (target filter, raids, who scrambles, what to do after the raid, fill from deck, save migration).
// No Unity, no game reads, selftest.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    [Flags] internal enum AlertTargets { None = 0, Air = 1, Helo = 2, Unknown = 4 }

    /// <summary>Settings of an "Alert" group (ADR-0079 section 3); written to the save and to the "Undo" snapshot.</summary>
    internal sealed class CapAlertSpec
    {
        public int RefUid, Ready = 2, PerRaid = 2;           // PerRaid 0 = all ready
        public string Level = "deck", Speed = "max", Roe = "tight", After = "patrol";
        public double RadiusNm = 80, PursuitNm = 60;
        public AlertTargets Targets = AlertTargets.Air;
        public bool Fill;

        /// <summary>Defaults from the [Cap] settings.</summary>
        public static CapAlertSpec FromParams(CapParams p)
        {
            bool ok;
            var t = CapAlertMath.ParseTargets(p.AlertTargets, out ok);
            return new CapAlertSpec
            {
                Ready = Math.Max(1, Math.Min(4, p.AlertCount)), PerRaid = Math.Max(0, Math.Min(4, p.AlertPerRaid)),
                Level = p.AlertLevel == "hangar" ? "hangar" : "deck", Speed = p.AlertSpeed == "cruise" ? "cruise" : "max",
                Roe = p.Roe, After = p.AlertAfter == "alert" ? "alert" : "patrol",
                RadiusNm = p.AlertNm, PursuitNm = p.AlertPursuitNm, Targets = ok ? t : AlertTargets.Air, Fill = p.AlertFill
            };
        }

        public CapAlertSpec Clone() { return (CapAlertSpec)MemberwiseClone(); }

        public bool Same(CapAlertSpec o)
        {
            return o != null && RefUid == o.RefUid && Ready == o.Ready && PerRaid == o.PerRaid && Level == o.Level && Speed == o.Speed && Roe == o.Roe
                && After == o.After && Math.Abs(RadiusNm - o.RadiusNm) < 1e-6 && Math.Abs(PursuitNm - o.PursuitNm) < 1e-6 && Targets == o.Targets && Fill == o.Fill;
        }
    }

    internal sealed class AlertRaid
    {
        public int Key; public List<int> Ids = new List<int>(); public double Nm = double.MaxValue;
        // T-219: track points and the course/speed of the track nearest to the reference point (filled by Raids()).
        public List<GeoPt> Pts = new List<GeoPt>(); public double CourseDeg, Kn; public GeoPt Near; public bool HasNear;
    }

    /// <summary>T-219: memory of the place of a raid: the last seen point of its tracks, course, speed, game time (s).</summary>
    internal sealed class RaidMemo
    {
        public string Key; public List<int> Ids = new List<int>(); public double Lat, Lon, CourseDeg, Kn, At;
        public RaidMemo Clone() { var m = (RaidMemo)MemberwiseClone(); m.Ids = new List<int>(Ids); return m; }
    }
    internal sealed class AlertReady { public int Uid; public bool DeckReady; public double ReadyInS; }
    internal sealed class AlertBird { public int Uid; public double Lat, Lon, ReserveNm, PatrolKn; public int AamNow, AamStart; }

    /// <summary>One patrol group with an order; the controller computes the facts.</summary>
    internal sealed class PatrolOffer
    {
        public string Group; public bool Accept; public int FreeSlot = -1; public bool Gap;
        public GeoPt SlotPt; public double MissilePct, FuelThresholdNm;   // FuelThresholdNm = CapMath.FuelThresholdNm(...) of the patrol for this bird
    }

    internal sealed class AfterClearResult
    {
        public string Op;   // rtb_alert | lend
        public string Why;  // after_alert | no_patrol | patrol_full | unfit | null
        public string To; public int Slot = -1; public double ReserveNm, NeedNm;
    }

    internal sealed class FillCandidate { public int Uid, DeckUid; public string Where; public double ReadyInS, Score; public bool Free; }

    internal static class CapAlertMath
    {
        public const double RaidNm = 10;

        /// <summary>"air,helo,unknown" (any order, any case) -> flags; empty or garbage -> ok=false.</summary>
        public static AlertTargets ParseTargets(string csv, out bool ok)
        {
            var t = AlertTargets.None; ok = false;
            if (string.IsNullOrWhiteSpace(csv)) return t;
            foreach (string part in csv.Split(','))
            {
                string s = part.Trim().ToLowerInvariant();
                if (s.Length == 0) continue;
                if (s == "air") t |= AlertTargets.Air;
                else if (s == "helo") t |= AlertTargets.Helo;
                else if (s == "unknown") t |= AlertTargets.Unknown;
                else return AlertTargets.None;
            }
            ok = t != AlertTargets.None;
            return t;
        }

        public static string TargetsCsv(AlertTargets t)
        {
            var l = new List<string>();
            if ((t & AlertTargets.Air) != 0) l.Add("air");
            if ((t & AlertTargets.Helo) != 0) l.Add("helo");
            if ((t & AlertTargets.Unknown) != 0) l.Add("unknown");
            return string.Join(",", l);
        }

        /// <summary>ADR-0079 section 4: a hostile aircraft / a hostile helicopter / an unknown track at or above UnknownMinKn; a missile is never a raid.</summary>
        public static bool IsRaidTrack(CapContact c, AlertTargets t, double unknownMinKn)
        {
            if (c == null || c.Missile) return false;
            if (c.Hostile) return c.Helo ? (t & AlertTargets.Helo) != 0 : (t & AlertTargets.Air) != 0;
            if (c.Unknown) return (t & AlertTargets.Unknown) != 0 && c.SpeedKn >= unknownMinKn;
            return false;
        }

        /// <summary>Tracks within RaidNm of each other are one raid; Nm = the nearest track to refPt.</summary>
        public static List<AlertRaid> Raids(IList<CapContact> contacts, AlertTargets t, double unknownMinKn, GeoPt refPt)
        {
            var list = new List<CapContact>();
            if (contacts != null) foreach (var c in contacts) if (IsRaidTrack(c, t, unknownMinKn)) list.Add(c);
            var par = new int[list.Count];
            for (int i = 0; i < par.Length; i++) par[i] = i;
            Func<int, int> find = null;
            find = x => par[x] == x ? x : (par[x] = find(par[x]));
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                    if (AswMath.DistNm(new GeoPt(list[i].Lat, list[i].Lon), new GeoPt(list[j].Lat, list[j].Lon)) <= RaidNm) par[find(i)] = find(j);
            var map = new Dictionary<int, AlertRaid>(); var res = new List<AlertRaid>();
            for (int i = 0; i < list.Count; i++)
            {
                int r = find(i); AlertRaid cl;
                if (!map.TryGetValue(r, out cl)) { cl = new AlertRaid(); map[r] = cl; res.Add(cl); }
                cl.Ids.Add(list[i].Id);
                var pt = new GeoPt(list[i].Lat, list[i].Lon);
                cl.Pts.Add(pt);
                double dn = AswMath.DistNm(refPt, pt);
                if (dn < cl.Nm || !cl.HasNear) { cl.Near = pt; cl.CourseDeg = list[i].CourseDeg; cl.Kn = list[i].SpeedKn; cl.HasNear = true; }
                cl.Nm = Math.Min(cl.Nm, dn);
            }
            foreach (var cl in res) { int k = int.MaxValue; foreach (int id in cl.Ids) k = Math.Min(k, id); cl.Key = k; }
            return res;
        }

        // ---- T-197 ----
        /// <summary>The leash may start outside its circle (the deck is far from the protected unit): until the bird has been inside once, the bound is the start distance plus this slack.</summary>
        public const double LeashStartSlackNm = 10;

        /// <summary>Leash: until the bird was inside PursuitNm, the bound is max(PursuitNm, startNm + LeashStartSlackNm); after entering it is PursuitNm.</summary>
        public static bool LeashOut(double distNm, double pursuitNm, double startNm, bool wasInside)
        {
            double bound = wasInside || startNm < 0 ? pursuitNm : Math.Max(pursuitNm, startNm + LeashStartSlackNm);
            return distNm > bound;
        }

        /// <summary>A bird that never entered the leash circle is called back after this many seconds (no endless chase outside).</summary>
        public const double LeashNeverInS = 900;

        internal enum RaidGate { Go, Served, Wait }

        /// <summary>Seconds to wait for the AirGuard key of a new raid before the group works by its own track ids.</summary>
        public const double RaidKeyWaitS = 3;

        // ---- T-219 ----
        public const double MemoTtlS = 900;        // a raid memory lives this long after the last sight
        public const double MemoDriftCapS = 600;   // the place forecast is not extrapolated beyond this
        public const double MemoSlackFrac = 0.25;  // tolerance grows by 25 % of the forecast path...
        public const double MemoSlackMaxNm = 15;   // ...but not more than this over RaidNm

        /// <summary>The forecast point of a memory: course and speed over min(now - At, MemoDriftCapS).</summary>
        public static GeoPt Project(RaidMemo m, double now)
        {
            double dt = Math.Max(0, Math.Min(now - m.At, MemoDriftCapS));
            return AswMath.Offset(new GeoPt(m.Lat, m.Lon), m.CourseDeg, m.Kn * dt / 3600.0);
        }

        /// <summary>False when the memory is older than MemoTtlS; else any raid point within RaidNm + min(25 % of the path, 15 nm) of the forecast.</summary>
        public static bool MemoMatch(AlertRaid raid, RaidMemo m, double now)
        {
            return MemoDist(raid, m, now) >= 0;
        }

        /// <summary>Distance of the nearest raid point to the forecast if it matches, else -1.</summary>
        private static double MemoDist(AlertRaid raid, RaidMemo m, double now)
        {
            if (raid == null || m == null || now - m.At > MemoTtlS) return -1;
            var fc = Project(m, now);
            double path = m.Kn * Math.Max(0, Math.Min(now - m.At, MemoDriftCapS)) / 3600.0;
            double tol = RaidNm + Math.Min(MemoSlackFrac * path, MemoSlackMaxNm);
            double best = -1;
            foreach (var pt in raid.Pts)
            {
                double d = AswMath.DistNm(fc, pt);
                if (d <= tol && (best < 0 || d < best)) best = d;
            }
            return best;
        }

        public static RaidMemo MemoOf(AlertRaid raid, double now, string key)
        {
            var m = new RaidMemo { Key = key, At = now, CourseDeg = raid.CourseDeg, Kn = raid.Kn, Ids = new List<int>(raid.Ids) };
            GeoPt p = raid.HasNear ? raid.Near : (raid.Pts.Count > 0 ? raid.Pts[0] : new GeoPt(0, 0));
            m.Lat = p.Lat; m.Lon = p.Lon;
            return m;
        }

        /// <summary>The raid of the same wave for a bird without live tracks: 1) shared ids, 2) shared AirGuard key, 3) MemoMatch(tgt) (the nearest to the forecast). null = none.</summary>
        internal static AlertRaid RebindRaid(IList<AlertRaid> raids, ICollection<int> bound, string key, RaidMemo tgt, Func<int, string> keyOf, double now, out string why)
        {
            why = null;
            if (raids == null) return null;
            if (bound != null)
                foreach (var r in raids) foreach (int id in r.Ids) if (bound.Contains(id)) { why = "ids"; return r; }
            if (keyOf != null)
            {
                var keys = new HashSet<string>();
                if (key != null) keys.Add(key);
                if (bound != null) foreach (int id in bound) { string k = keyOf(id); if (k != null) keys.Add(k); }
                if (keys.Count > 0)
                    foreach (var r in raids) foreach (int id in r.Ids) { string k = keyOf(id); if (k != null && keys.Contains(k)) { why = "key"; return r; } }
            }
            if (tgt != null)
            {
                AlertRaid best = null; double bd = double.MaxValue;
                foreach (var r in raids) { double d = MemoDist(r, tgt, now); if (d >= 0 && d < bd) { bd = d; best = r; } }
                if (best != null) { why = "near"; return best; }
            }
            return null;
        }

        /// <summary>A raid is served if any of its ids is served OR the AirGuard key of any id is (also the key of a served id: alias) OR its place matches a memory.
        /// keyOf == null (no AirGuard): by id (and memory) only.
        /// No key yet and the first sight of the tracks is younger than waitS: Wait. key = the AirGuard key or null.</summary>
        internal static RaidGate GateRaid(AlertRaid raid, ICollection<int> servedIds, ICollection<string> servedKeys, Func<int, string> keyOf,
                                          IDictionary<int, double> firstSeen, double now, double waitS, out string key,
                                          IList<RaidMemo> memos = null)
        {
            key = null;
            foreach (int id in raid.Ids) if (servedIds != null && servedIds.Contains(id)) return RaidGate.Served;
            if (memos != null) foreach (var m in memos) if (MemoMatch(raid, m, now)) return RaidGate.Served;
            if (keyOf == null) return RaidGate.Go;
            HashSet<string> alias = null;
            if (servedIds != null && servedIds.Count > 0)
            {
                alias = new HashSet<string>();
                foreach (int sid in servedIds) { string sk = keyOf(sid); if (sk != null) alias.Add(sk); }
            }
            foreach (int id in raid.Ids)
            {
                string k = keyOf(id);
                if (k == null) continue;
                if (key == null) key = k;
                if (servedKeys != null && servedKeys.Contains(k)) return RaidGate.Served;
                if (alias != null && alias.Contains(k)) return RaidGate.Served;
            }
            if (key != null) return RaidGate.Go;
            double first = now;
            foreach (int id in raid.Ids) { double t; if (firstSeen != null && firstSeen.TryGetValue(id, out t) && t < first) first = t; }
            return now - first < waitS ? RaidGate.Wait : RaidGate.Go;
        }

        /// <summary>The group whose alert circles are drawn: the one selected in the visible window, else the last selected one (window closed).</summary>
        public static string CircleGroup(string selectedNow, ref string last, bool inMission = true)
        {
            if (!inMission) { last = null; return null; }   // T-201: a new mission forgets the last group (same name, other mission)
            if (selectedNow != null) last = selectedNow;
            return selectedNow ?? last;
        }

        public static bool InReach(AlertRaid r, double radiusNm) { return r != null && r.Nm <= radiusNm; }

        /// <summary>0 = all ready; else at most perRaid.</summary>
        public static int TakeCount(int perRaid, int readyNow)
        {
            if (readyNow <= 0) return 0;
            return perRaid <= 0 ? readyNow : Math.Min(perRaid, readyNow);
        }

        /// <summary>Uids to launch: deck_ready first, then the smaller remaining preparation.</summary>
        public static List<int> PickScramble(IList<AlertReady> ready, int perRaid)
        {
            var l = new List<AlertReady>(ready ?? new List<AlertReady>());
            l.Sort((a, b) => a.DeckReady != b.DeckReady ? (a.DeckReady ? -1 : 1) : a.ReadyInS.CompareTo(b.ReadyInS));
            int n = TakeCount(perRaid, l.Count);
            var res = new List<int>();
            for (int i = 0; i < n; i++) res.Add(l[i].Uid);
            return res;
        }

        /// <summary>ADR-0079 section 5. "alert" = back to stand alert; "patrol" = lend the bird to the nearest patrol group with a missing bird when it is fit.</summary>
        public static AfterClearResult AfterClear(AlertBird b, string after, IList<PatrolOffer> patrols, double handoverMinS)
        {
            if (after == "alert") return new AfterClearResult { Op = "rtb_alert", Why = "after_alert" };
            bool anyAccept = false; PatrolOffer best = null; double bestD = double.MaxValue; bool anyFree = false;
            var here = new GeoPt(b.Lat, b.Lon);
            AfterClearResult unfit = null;
            if (patrols != null)
                foreach (var p in patrols)
                {
                    if (p == null || !p.Accept) continue;
                    anyAccept = true;
                    if (p.FreeSlot < 0 && !p.Gap) continue;
                    anyFree = true;
                    double d = AswMath.DistNm(here, p.SlotPt);
                    double need = p.FuelThresholdNm + handoverMinS * (b.PatrolKn > 0 ? b.PatrolKn : 300) / 3600.0;
                    bool missiles = b.AamNow > (int)Math.Floor(p.MissilePct * b.AamStart);
                    bool fuel = b.ReserveNm - d >= need;
                    if (!missiles || !fuel)
                    {
                        if (unfit == null) unfit = new AfterClearResult { Op = "rtb_alert", Why = "unfit", ReserveNm = b.ReserveNm - d, NeedNm = need };
                        continue;
                    }
                    if (d < bestD) { bestD = d; best = p; }
                }
            if (best != null)
            {
                double need = best.FuelThresholdNm + handoverMinS * (b.PatrolKn > 0 ? b.PatrolKn : 300) / 3600.0;
                return new AfterClearResult { Op = "lend", To = best.Group, Slot = best.FreeSlot >= 0 ? best.FreeSlot : 0, ReserveNm = b.ReserveNm - bestD, NeedNm = need };
            }
            if (!anyAccept) return new AfterClearResult { Op = "rtb_alert", Why = "no_patrol" };
            if (!anyFree) return new AfterClearResult { Op = "rtb_alert", Why = "patrol_full" };
            return unfit ?? new AfterClearResult { Op = "rtb_alert", Why = "unfit" };
        }

        /// <summary>Free machines to take from the decks of the group: only Free; the deck of the protected ship first, ready before not ready, sooner first, then the better loadout.</summary>
        public static List<int> PickFill(IList<FillCandidate> c, int need, int protectDeckUid)
        {
            var l = new List<FillCandidate>();
            if (c != null) foreach (var x in c) if (x != null && x.Free) l.Add(x);
            l.Sort((a, b) =>
            {
                bool pa = a.DeckUid == protectDeckUid, pb = b.DeckUid == protectDeckUid;
                if (pa != pb) return pa ? -1 : 1;
                bool ra = a.Where == "deck_ready", rb = b.Where == "deck_ready";
                if (ra != rb) return ra ? -1 : 1;
                int t = a.ReadyInS.CompareTo(b.ReadyInS);
                return t != 0 ? t : b.Score.CompareTo(a.Score);
            });
            var res = new List<int>();
            for (int i = 0; i < l.Count && i < need; i++) res.Add(l[i].Uid);
            return res;
        }

        /// <summary>Save format 1 -> 2: "none" (no alert) | "alert_group" (the group had no zone) | "dropped" (a patrol group with a zone loses the alert).</summary>
        public static string MigrateV1(bool hasZone, int alert)
        {
            if (alert <= 0) return "none";
            return hasZone ? "dropped" : "alert_group";
        }

        // ================================================= selftest ====================================================================
        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }

        private static CapContact C(int id, double lat, double lon, bool hostile = true, bool unknown = false, bool helo = false, bool missile = false, double kn = 450)
        {
            return new CapContact { Id = id, Lat = lat, Lon = lon, Hostile = hostile && !unknown, Unknown = unknown, Helo = helo, Missile = missile, SpeedKn = kn };
        }

        public static string SelfTest()
        {
            _err = null;
            bool ok;
            True("targets empty", ParseTargets("", out ok) == AlertTargets.None && !ok);
            True("targets garbage", ParseTargets("air,tank", out ok) == AlertTargets.None && !ok);
            True("targets order", TargetsCsv(ParseTargets("unknown, helo,AIR", out ok)) == "air,helo,unknown" && ok);
            True("targets null", ParseTargets(null, out ok) == AlertTargets.None && !ok);

            var p = new CapParams { AlertCount = 3, AlertPerRaid = 0, AlertLevel = "hangar", AlertTargets = "air,unknown", AlertNm = 100, AlertAfter = "alert", AlertFill = true };
            var s = CapAlertSpec.FromParams(p);
            True("spec from params", s.Ready == 3 && s.PerRaid == 0 && s.Level == "hangar" && s.Targets == (AlertTargets.Air | AlertTargets.Unknown) && s.RadiusNm == 100 && s.After == "alert" && s.Fill && s.Roe == "tight");
            var s2 = s.Clone();
            True("clone same", s.Same(s2) && !ReferenceEquals(s, s2));
            s2.Ready = 1;
            True("not same", !s.Same(s2) && !s.Same(null));
            var bad = CapAlertSpec.FromParams(new CapParams { AlertTargets = "zzz", AlertLevel = "x", AlertSpeed = "y", AlertAfter = "z" });
            True("garbage -> defaults", bad.Targets == AlertTargets.Air && bad.Level == "deck" && bad.Speed == "max" && bad.After == "patrol");

            // target filter
            var air = AlertTargets.Air; var all = AlertTargets.Air | AlertTargets.Helo | AlertTargets.Unknown;
            True("aircraft", IsRaidTrack(C(1, 0, 0), air, 350));
            True("helicopter is not an aircraft", !IsRaidTrack(C(1, 0, 0, helo: true), air, 350));
            True("helicopter by helo", IsRaidTrack(C(1, 0, 0, helo: true), AlertTargets.Helo, 350));
            True("aircraft not by helo", !IsRaidTrack(C(1, 0, 0), AlertTargets.Helo, 350));
            True("unknown 300 kn below the limit", !IsRaidTrack(C(1, 0, 0, unknown: true, kn: 300), all, 350));
            True("unknown 400 kn", IsRaidTrack(C(1, 0, 0, unknown: true, kn: 400), all, 350));
            True("unknown not asked", !IsRaidTrack(C(1, 0, 0, unknown: true, kn: 400), air, 350));
            True("missile is no raid", !IsRaidTrack(C(1, 0, 0, missile: true), all, 350));
            True("non hostile is no raid", !IsRaidTrack(C(1, 0, 0, hostile: false), all, 350));
            True("null", !IsRaidTrack(null, all, 350));

            // raids: two tracks 8 nm apart are one raid, a third 50 nm away is another
            var refPt = new GeoPt(10.0, 11.0);
            var rl = Raids(new List<CapContact> { C(5, 10 + 78.0 / 60, 11), C(3, 10 + 70.0 / 60, 11), C(9, 10 + 150.0 / 60, 11) }, air, 350, refPt);
            True("two raids", rl.Count == 2);
            True("cluster key + nm", rl.Count == 2 && rl[0].Key == 3 && rl[0].Ids.Count == 2 && Math.Abs(rl[0].Nm - 70) < 0.5 && rl[1].Key == 9);
            True("reach 78 / 85 at 80", rl.Count == 2 && InReach(rl[0], 80) && !InReach(Raids(new List<CapContact> { C(7, 10 + 85.0 / 60, 11) }, air, 350, refPt)[0], 80));
            True("no raids", Raids(null, air, 350, refPt).Count == 0 && Raids(new List<CapContact> { C(1, 10, 11, missile: true) }, air, 350, refPt).Count == 0);

            // per raid
            True("take 1 of 3", TakeCount(1, 3) == 1);
            True("take 2 of 1", TakeCount(2, 1) == 1);
            True("take all of 4", TakeCount(0, 4) == 4);
            True("take of none", TakeCount(2, 0) == 0 && TakeCount(0, 0) == 0);
            var rdy = new List<AlertReady> { new AlertReady { Uid = 1, ReadyInS = 300 }, new AlertReady { Uid = 2, DeckReady = true }, new AlertReady { Uid = 3, ReadyInS = 60 }, new AlertReady { Uid = 4, DeckReady = true, ReadyInS = 0 } };
            var pk = PickScramble(rdy, 2);
            True("scramble order: ready first", pk.Count == 2 && pk[0] == 2 && pk[1] == 4);
            pk = PickScramble(rdy, 3);
            True("scramble: then the sooner prepared", pk.Count == 3 && pk[2] == 3);
            True("scramble all", PickScramble(rdy, 0).Count == 4 && PickScramble(rdy, 1).Count == 1);
            True("scramble none", PickScramble(new List<AlertReady>(), 2).Count == 0);

            // after the raid
            var bird = new AlertBird { Uid = 1, Lat = 10.5, Lon = 11, ReserveNm = 900, PatrolKn = 420, AamNow = 6, AamStart = 8 };
            Func<string, int, double, PatrolOffer> off = (g, slot, lat) => new PatrolOffer { Group = g, Accept = true, FreeSlot = slot, SlotPt = new GeoPt(lat, 11), MissilePct = 0.25, FuelThresholdNm = 300 };
            var one = new List<PatrolOffer> { off("ПВО-1", 1, 10.8) };
            var r = AfterClear(bird, "alert", one, 600);
            True("after=alert: rtb_alert", r.Op == "rtb_alert" && r.Why == "after_alert");
            r = AfterClear(bird, "patrol", one, 600);
            True("free slot: lend", r.Op == "lend" && r.To == "ПВО-1" && r.Slot == 1 && r.Why == null);
            True("lend facts", r.NeedNm > 300 + 600 * 420 / 3600.0 - 1 && r.ReserveNm < 900 && r.ReserveNm > 800);
            r = AfterClear(bird, "patrol", new List<PatrolOffer> { off("ПВО-1", -1, 10.8) }, 600);
            True("full patrol", r.Op == "rtb_alert" && r.Why == "patrol_full");
            var refuse = off("ПВО-1", 0, 10.8); refuse.Accept = false;
            r = AfterClear(bird, "patrol", new List<PatrolOffer> { refuse }, 600);
            True("accept off: no_patrol", r.Op == "rtb_alert" && r.Why == "no_patrol");
            True("no patrols at all", AfterClear(bird, "patrol", new List<PatrolOffer>(), 600).Why == "no_patrol" && AfterClear(bird, "patrol", null, 600).Why == "no_patrol");
            r = AfterClear(new AlertBird { Lat = 10.5, Lon = 11, ReserveNm = 900, PatrolKn = 420, AamNow = 2, AamStart = 8 }, "patrol", one, 600);
            True("2 of 8 missiles is not fit (floor 25 % = 2)", r.Why == "unfit");
            r = AfterClear(new AlertBird { Lat = 10.5, Lon = 11, ReserveNm = 900, PatrolKn = 420, AamNow = 3, AamStart = 8 }, "patrol", one, 600);
            True("3 of 8 is fit", r.Op == "lend");
            r = AfterClear(new AlertBird { Lat = 10.5, Lon = 11, ReserveNm = 350, PatrolKn = 420, AamNow = 8, AamStart = 8 }, "patrol", one, 600);
            True("little fuel: unfit", r.Why == "unfit" && r.NeedNm > r.ReserveNm);
            r = AfterClear(bird, "patrol", new List<PatrolOffer> { off("ПВО-1", 0, 11.5), off("ПВО-3", 1, 10.7) }, 600);
            True("two patrols: the nearer slot", r.Op == "lend" && r.To == "ПВО-3");
            var gap = off("ПВО-1", -1, 10.8); gap.Gap = true;
            r = AfterClear(bird, "patrol", new List<PatrolOffer> { gap }, 600);
            True("gap without a free slot: lend into slot 0", r.Op == "lend" && r.Slot == 0);
            var farUnfit = off("ПВО-1", 0, 20.0);
            r = AfterClear(bird, "patrol", new List<PatrolOffer> { farUnfit, off("ПВО-3", 2, 10.9) }, 600);
            True("the unfit far one is skipped", r.Op == "lend" && r.To == "ПВО-3");

            // fill
            var fc = new List<FillCandidate>
            {
                new FillCandidate { Uid = 1, DeckUid = 300, Where = "deck_ready", Free = true },
                new FillCandidate { Uid = 2, DeckUid = 100, Where = "hangar", ReadyInS = 900, Free = true },
                new FillCandidate { Uid = 3, DeckUid = 100, Where = "deck_ready", Free = true },
                new FillCandidate { Uid = 4, DeckUid = 100, Where = "deck_prep", ReadyInS = 100, Free = true, Score = 1 },
                new FillCandidate { Uid = 5, DeckUid = 100, Where = "deck_prep", ReadyInS = 100, Free = true, Score = 5 },
                new FillCandidate { Uid = 6, DeckUid = 100, Where = "deck_ready", Free = false },
            };
            var f = PickFill(fc, 4, 100);
            True("fill: protected deck first, ready, sooner, score", f.Count == 4 && f[0] == 3 && f[1] == 5 && f[2] == 4 && f[3] == 2);
            f = PickFill(fc, 2, 300);
            True("fill: other protected deck", f.Count == 2 && f[0] == 1 && f[1] == 3);
            True("fill: only free", !PickFill(fc, 9, 100).Contains(6) && PickFill(fc, 9, 100).Count == 5);
            True("fill: none", PickFill(null, 2, 100).Count == 0 && PickFill(fc, 0, 100).Count == 0);

            // T-197: leash, raid gate, circle group
            True("leash: far deck, not inside, 70", !LeashOut(70, 60, 70, false));
            True("leash: far deck, not inside, 79", !LeashOut(79, 60, 70, false));
            True("leash: far deck, not inside, 81", LeashOut(81, 60, 70, false));
            True("leash: was inside, 61", LeashOut(61, 60, 70, true));
            True("leash: near deck, 61", LeashOut(61, 60, 30, false));
            True("leash: inside", !LeashOut(50, 60, 30, true));
            var gr = new AlertRaid(); gr.Ids.Add(7040);
            var seen = new Dictionary<int, double> { { 7040, 100 } };
            string gk;
            Func<int, string> keys = id => id == 7039 || id == 7040 ? "r7039" : null;
            True("gate: shared key served", GateRaid(gr, new HashSet<int>(), new HashSet<string> { "r7039" }, keys, seen, 101, 3, out gk) == RaidGate.Served);
            True("gate: key not served -> go with key", GateRaid(gr, new HashSet<int>(), new HashSet<string>(), keys, seen, 101, 3, out gk) == RaidGate.Go && gk == "r7039");
            True("gate: served by id", GateRaid(gr, new HashSet<int> { 7040 }, null, keys, seen, 101, 3, out gk) == RaidGate.Served);
            Func<int, string> nokeys = id => null;
            True("gate: no key yet -> wait", GateRaid(gr, new HashSet<int>(), new HashSet<string>(), nokeys, seen, 102, 3, out gk) == RaidGate.Wait && gk == null);
            True("gate: no key after 3 s -> go", GateRaid(gr, new HashSet<int>(), new HashSet<string>(), nokeys, seen, 103, 3, out gk) == RaidGate.Go && gk == null);
            True("gate: no AirGuard -> by id only", GateRaid(gr, new HashSet<int>(), new HashSet<string>(), null, seen, 100, 3, out gk) == RaidGate.Go);
            string lastG = null;
            True("circle: selected", CircleGroup("CAP-2", ref lastG) == "CAP-2" && lastG == "CAP-2");
            True("circle: window closed", CircleGroup(null, ref lastG) == "CAP-2");
            True("circle: new selection wins", CircleGroup("CAP-1", ref lastG) == "CAP-1" && lastG == "CAP-1");
            True("circle: out of mission forgets", CircleGroup(null, ref lastG, false) == null && lastG == null);
            True("circle: new mission starts empty", CircleGroup(null, ref lastG) == null);

            // T-219: raid points, memory, gate, rebind
            True("raid pts + kn of the nearest", rl.Count == 2 && rl[0].Pts.Count == 2 && rl[1].Pts.Count == 1);
            var rk = Raids(new List<CapContact> { C(5, 10 + 78.0 / 60, 11, kn: 300), C(3, 10 + 70.0 / 60, 11, kn: 480) }, air, 350, refPt);
            True("raid kn of the nearest track", rk.Count == 1 && rk[0].Pts.Count == 2 && Math.Abs(rk[0].Kn - 480) < 1e-9);
            var north = new GeoPt(10.0 + 50.0 / 60, 11.0);
            var mem = new RaidMemo { Key = "r7028", Lat = north.Lat, Lon = north.Lon, CourseDeg = 180, Kn = 500, At = 1000, Ids = new List<int> { 7028 } };
            var fcp = Project(mem, 1060);
            True("project: 41.7 nm south of start", Math.Abs(AswMath.DistNm(north, fcp) - 500.0 * 60 / 3600) < 0.1 && fcp.Lat < north.Lat);
            Func<GeoPt, AlertRaid> raidAt = pt => { var q = new AlertRaid(); q.Ids.Add(7027); q.Pts.Add(pt); q.Near = pt; q.HasNear = true; return q; };
            True("memo match: 6 nm from the forecast", MemoMatch(raidAt(AswMath.Offset(fcp, 90, 6)), mem, 1060));
            True("memo match: 40 nm away", !MemoMatch(raidAt(AswMath.Offset(fcp, 90, 40)), mem, 1060));
            True("memo ttl: 901 s", !MemoMatch(raidAt(north), mem, 1901) && MemoMatch(raidAt(AswMath.Offset(Project(mem, 1900), 90, 3)), mem, 1900));
            var fcap = Project(mem, 3000);
            True("memo drift cap 600 s", Math.Abs(AswMath.DistNm(north, fcap) - 500.0 * 600 / 3600) < 0.2 && Math.Abs(AswMath.DistNm(Project(mem, 1600), fcap)) < 1e-6);
            var mo = MemoOf(rk[0], 77, "k1");
            True("memo of", mo.Key == "k1" && mo.At == 77 && mo.Ids.Count == 2 && Math.Abs(mo.Kn - 480) < 1e-9 && Math.Abs(mo.Lat - (10 + 70.0 / 60)) < 1e-9);
            var g27 = new AlertRaid(); g27.Ids.Add(7027); g27.Pts.Add(AswMath.Offset(fcp, 0, 5)); g27.Near = g27.Pts[0]; g27.HasNear = true;
            Func<int, string> alias = id => id == 7027 || id == 7028 ? "r7027" : null;
            True("gate: alias of a served id", GateRaid(g27, new HashSet<int> { 7028 }, new HashSet<string>(), alias, seen, 1060, 3, out gk) == RaidGate.Served);
            Func<int, string> two = id => id == 7027 ? "r7027" : id == 7028 ? "r7028" : null;
            True("gate: different keys, no memory -> go", GateRaid(g27, new HashSet<int> { 7028 }, new HashSet<string>(), two, seen, 1060, 3, out gk) == RaidGate.Go && gk == "r7027");
            True("gate: different keys, memory near -> served", GateRaid(g27, new HashSet<int> { 7028 }, new HashSet<string>(), two, seen, 1060, 3, out gk, new List<RaidMemo> { mem }) == RaidGate.Served);
            True("gate: memory expired -> go", GateRaid(g27, new HashSet<int>(), new HashSet<string>(), two, seen, 1901, 3, out gk, new List<RaidMemo> { mem }) == RaidGate.Go);
            var far27 = raidAt(AswMath.Offset(fcp, 90, 40));
            True("gate: far raid -> go", GateRaid(far27, new HashSet<int>(), new HashSet<string>(), two, seen, 1060, 3, out gk, new List<RaidMemo> { mem }) == RaidGate.Go);
            var rb7028 = new AlertRaid(); rb7028.Ids.Add(7028); rb7028.Pts.Add(north);
            var rb7031 = new AlertRaid(); rb7031.Ids.Add(7031); rb7031.Pts.Add(AswMath.Offset(north, 90, 200));
            string why;
            True("rebind: ids first", RebindRaid(new List<AlertRaid> { g27, rb7028 }, new HashSet<int> { 7028 }, "r7027", mem, two, 1060, out why) == rb7028 && why == "ids");
            Func<int, string> k31 = id => id == 7031 ? "r7028" : id == 7028 ? "r7028" : null;
            True("rebind: key", RebindRaid(new List<AlertRaid> { g27, rb7031 }, new HashSet<int> { 7028 }, "r7028", mem, k31, 1060, out why) == rb7031 && why == "key");
            True("rebind: near", RebindRaid(new List<AlertRaid> { rb7031, g27 }, new HashSet<int> { 7028 }, null, mem, two, 1060, out why) == g27 && why == "near");
            var g27b = raidAt(AswMath.Offset(fcp, 0, 9));
            True("rebind: two near -> nearest to the forecast", RebindRaid(new List<AlertRaid> { g27b, g27 }, null, null, mem, null, 1060, out why) == g27 && why == "near");
            True("rebind: nothing", RebindRaid(new List<AlertRaid> { rb7031 }, new HashSet<int> { 7028 }, null, null, two, 1060, out why) == null && why == null);

            // save migration
            True("migrate none", MigrateV1(false, 0) == "none" && MigrateV1(true, 0) == "none");
            True("migrate no zone", MigrateV1(false, 2) == "alert_group");
            True("migrate with zone", MigrateV1(true, 2) == "dropped");
            return _err;
        }
    }
}
