using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-173 / ADR-0074: pure maths of the strike group (target eligibility, priority, time to water, assignment, claims, own-sub latch). No game, no Unity.

    /// <summary>An own submarine (true place) or an allied one (only as the friendly track of the player's map).</summary>
    internal sealed class AswOwnSub { public int Uid, TrackId; public string Name; public double Lat, Lon; public bool Allied; }

    internal sealed class AswShipPos { public int Uid; public string Name; public double Lat, Lon; }

    /// <summary>A torpedo of the player in the water. TargetId = -1: unknown.</summary>
    internal sealed class AswTorpObs { public int Id, LauncherUid, TargetId = -1; public double Lat, Lon; }

    /// <summary>The ASW torpedo an aircraft would drop: launch range and altitude window, speed, run time.</summary>
    internal sealed class AswDropSpec { public double MinNm, MaxNm, MinAltFt, MaxAltFt, SpeedKn, RunS = 600, MaxDepthFt; public string Ammo; public bool Charge; /* a depth charge / RBU, not a torpedo */ }

    /// <summary>Global (Live) parameters of the strike group; the NextOrder keys are copied into the group at the order.</summary>
    internal sealed class AswStrikeParams
    {
        public string Targets = "hostile_unknown";
        public double OwnSubSafeNm = 4, OwnSubClearS = 20, RadiusNm = 0 /* 0 = no limit (customer, Q-U2) */, UnknownConfirmS = 60,
                      FreshFixS = 90, ReattackGapS = 20, MaxEtaS = 1800, IdleS = 600, ToastS = 20, RebalanceMinS = 120;
        public int MaxTorpedoes = 0, MissStreak = 2, Airborne = 0;
        public bool Toast = true;
    }

    /// <summary>What the strike code knows about a submarine track (the player's knowledge only).</summary>
    internal sealed class StrikeTrack
    {
        public int Id;
        public string Side, MapPosture, VmPosture, ClassName, Decoy, Biologic;
        public bool? Classified;
        public bool Live, Surfaced;
        public double Lat, Lon, LiveForS, FixAgeS;
    }

    internal sealed class StrikePolicy
    {
        public string Targets = "hostile_unknown";
        public double UnknownConfirmS = 60, RadiusNm;
        public bool HasGuard;
        public GeoPt Guard;
        public double OwnTrackNm = 0.3;
    }

    internal struct StrikeElig
    {
        public bool Ok, Unknown;
        public string Reason;   // decoy|biologic|unclassified|neutral|friendly|not_hostile|own_track|unknown_young|out_of_zone|not_live|surfaced|skipped
    }

    internal sealed class StrikeShooter { public int Uid, Target; public bool Air, Holder, Busy; public double TtwS = -1; }

    internal sealed class StrikeTarget { public int Id; public double ShipNm, FixAgeS; public bool Hostile; }

    internal sealed class StrikeAssign { public int Target, Uid; public string Role; public double TtwS; }   // Role: shooter|second|deck

    internal struct OwnSubLatch { public bool On; public double ClearSinceS; }

    internal static class AswStrikeMath
    {
        /// <summary>T-222: a submarine track the game marks live only between sonar fixes stays live for the strike this long after its last live fix (mission s); T-223: 90 = Strike FreshFixS default.</summary>
        public const double StrikeLiveHoldS = 90;

        /// <summary>T-223: the fuel kept for the attack itself (drop, MAD pass slack), mission s.</summary>
        public const double StrikeFuelMarginS = 60;

        /// <summary>T-223: an airborne machine may be a shooter when the fuel left after the way home from here covers the run to the drop point plus StrikeFuelMarginS.</summary>
        public static bool StrikeFuelOk(double fuelFrac, double enduranceS, double transitS, double runNm, double kn, out double remainS, out double needS)
        {
            return StrikeFuelOk(fuelFrac, enduranceS, transitS, runNm, 0, kn, out remainS, out needS);
        }

        /// <summary>T-223 D2: same, plus the extra way home that the run adds: max(0, drop point to deck - machine to deck) nm (0 when the deck is unknown).</summary>
        public static bool StrikeFuelOk(double fuelFrac, double enduranceS, double transitS, double runNm, double extraReturnNm, double kn, out double remainS, out double needS)
        {
            remainS = Math.Max(0, fuelFrac * enduranceS - transitS);
            needS = kn > 0 ? (Math.Max(0, runNm) + Math.Max(0, extraReturnNm)) / kn * 3600.0 + StrikeFuelMarginS : double.MaxValue;
            return kn > 0 && remainS >= needS;
        }

        /// <summary>T-222: live now, or the last live fix (mission time, -1 = unknown) is not older than holdS and not in the future (a save load).</summary>
        public static bool HeldLive(bool liveNow, double fixS, double nowS, double holdS)
        {
            if (liveNow) return true;
            return fixS >= 0 && fixS <= nowS && nowS - fixS <= holdS;
        }

        /// <summary>Names the game gives a track that read as a biological object (whale, school of fish); null = none.</summary>
        public static string BiologicName(params string[] names)
        {
            if (names == null) return null;
            foreach (var n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                string l = n.ToLowerInvariant();
                if (l.Contains("biolog") || l.Contains("whale") || l.Contains("school") || l.Contains("fish")) return n;
            }
            return null;
        }

        private static bool Generic(string cls)
        {
            if (string.IsNullOrWhiteSpace(cls)) return true;
            string l = cls.Trim().ToLowerInvariant();
            return l == "submarine" || l == "sub" || l.StartsWith("submarine contact", StringComparison.Ordinal) || l == "unknown" || l == "подводный контакт" || l == "подводная лодка" || l == "неопознанный";   // i18n-allow: class names of the game tracks to match, never shown
        }

        /// <summary>The game classified the track as a submarine: its flag, or a concrete class name (not the generic "Submarine").</summary>
        public static bool IsClassified(bool? classified, string className)
        {
            if (classified == true) return true;
            return !Generic(className);
        }

        /// <summary>ADR-0074 item 3. Only what the player knows: side, posture of the map, classification, the own tracks.</summary>
        public static StrikeElig Eligible(StrikeTrack t, StrikePolicy p, IList<AswOwnSub> own, ISet<int> ownTrackIds)
        {
            var r = new StrikeElig();
            if (t.Decoy != null) { r.Reason = "decoy"; return r; }
            if (t.Biologic != null) { r.Reason = "biologic"; return r; }
            if (!t.Live) { r.Reason = "not_live"; return r; }
            if (t.Surfaced) { r.Reason = "surfaced"; return r; }
            string rej = AttackHostility.StrikeOk(t.Side, t.MapPosture, t.VmPosture, p.Targets);
            if (rej != null) { r.Reason = rej; return r; }
            bool hostile = (t.Side != null && t.Side.Equals("hostile", StringComparison.OrdinalIgnoreCase)) || AttackHostility.MapHostile(t.MapPosture, t.VmPosture);
            r.Unknown = !hostile;
            if (r.Unknown)
            {
                if (!IsClassified(t.Classified, t.ClassName)) { r.Reason = "unclassified"; return r; }
                if (t.LiveForS < p.UnknownConfirmS) { r.Reason = "unknown_young"; return r; }
            }
            if (ownTrackIds != null && ownTrackIds.Contains(t.Id)) { r.Reason = "own_track"; return r; }
            if (own != null)
                foreach (var o in own)
                    if (o.TrackId == t.Id || AswMath.DistNm(new GeoPt(o.Lat, o.Lon), new GeoPt(t.Lat, t.Lon)) <= p.OwnTrackNm) { r.Reason = "own_track"; return r; }
            if (p.RadiusNm > 0 && p.HasGuard && AswMath.DistNm(p.Guard, new GeoPt(t.Lat, t.Lon)) > p.RadiusNm) { r.Reason = "out_of_zone"; return r; }
            r.Ok = true;
            return r;
        }

        // ---- priority, time to water -------------------------------------------------------------------------------

        /// <summary>&lt; 0: a goes first. Closer to an own ship, fresher fix, hostile before unknown.</summary>
        public static int Priority(StrikeTarget a, StrikeTarget b)
        {
            if (Math.Abs(a.ShipNm - b.ShipNm) > 0.5) return a.ShipNm < b.ShipNm ? -1 : 1;
            if (Math.Abs(a.FixAgeS - b.FixAgeS) > 10) return a.FixAgeS < b.FixAgeS ? -1 : 1;
            if (a.Hostile != b.Hostile) return a.Hostile ? -1 : 1;
            return a.Id.CompareTo(b.Id);
        }

        public static double TtwAir(double distToDropNm, double speedKn, bool madPass, double madVerifyS)
        {
            double v = speedKn > 1 ? speedKn : 100;
            return Math.Max(0, distToDropNm) / v * 3600.0 + (madPass ? madVerifyS : 0);
        }

        /// <summary>A deck machine: ready + launch + flight to the drop point; readyInS &lt; 0 = unknown (-1).</summary>
        public static double TtwDeck(double readyInS, double launchS, double distNm, double speedKn)
        {
            if (readyInS < 0) return -1;
            return readyInS + Math.Max(0, launchS) + TtwAir(distNm, speedKn, false, 0);
        }

        /// <summary>The hysteresis of ADR-0074 item 4: the gain must be over 30 % and over 60 s (a negative current = unknown: any known value is better).</summary>
        public static bool Better(double curTtw, double newTtw)
        {
            if (newTtw < 0) return false;
            if (curTtw < 0) return true;
            double gain = curTtw - newTtw;
            return gain > 0.3 * curTtw && gain > 60;
        }

        // ---- assignment --------------------------------------------------------------------------------------------

        /// <summary>
        /// Greedy: targets by priority, each takes the free shooter with the smallest time to water (the holder counts x2; a deck machine
        /// only when no airborne shooter exists or it is faster); one shooter per target; the rest of the airborne shooters become second numbers
        /// of the most important targets (one per target). A busy shooter (engaged / in water / assessing) is never moved; one on approach moves only if Better().
        /// current = target -> uid now on approach.
        /// </summary>
        public static List<StrikeAssign> Assign(IList<StrikeTarget> targets, IList<StrikeShooter> shooters, IDictionary<int, int> current, double maxEtaS)
        {
            var res = new List<StrikeAssign>();
            var order = new List<StrikeTarget>(targets);
            order.Sort(Priority);
            var used = new HashSet<int>();
            var done = new HashSet<int>();
            foreach (var s in shooters)
            {
                if (!s.Busy || s.Target == 0) continue;
                bool known = false;
                foreach (var t in order) if (t.Id == s.Target) known = true;
                if (!known || done.Contains(s.Target)) continue;
                done.Add(s.Target); used.Add(s.Uid);
                res.Add(new StrikeAssign { Target = s.Target, Uid = s.Uid, Role = s.Air ? "shooter" : "deck", TtwS = 0 });
            }
            foreach (var t in order)
            {
                if (done.Contains(t.Id)) continue;
                StrikeShooter bestAir = null, bestDeck = null; double airEff = 0, deckEff = 0;
                int cur; current.TryGetValue(t.Id, out cur);
                StrikeShooter curS = null; double curEff = -1;
                foreach (var s in shooters)
                {
                    if (s.Busy || used.Contains(s.Uid)) continue;
                    if (s.Air)
                    {
                        if (s.TtwS < 0) continue;
                        double e = s.TtwS * (s.Holder ? 2 : 1);
                        if (e > maxEtaS) continue;
                        if (bestAir == null || e < airEff) { bestAir = s; airEff = e; }
                        if (s.Uid == cur && cur != 0) { curS = s; curEff = e; }
                    }
                    else
                    {
                        if (s.TtwS > maxEtaS) continue;
                        if (bestDeck == null || (s.TtwS >= 0 && (bestDeck.TtwS < 0 || s.TtwS < bestDeck.TtwS))) { bestDeck = s; deckEff = s.TtwS; }
                    }
                }
                StrikeShooter pick = null; string role = "shooter"; double ttw = -1;
                if (bestAir != null && (bestDeck == null || bestDeck.TtwS < 0 || bestDeck.TtwS >= airEff)) { pick = bestAir; ttw = airEff; }
                else if (bestDeck != null) { pick = bestDeck; role = "deck"; ttw = deckEff; }
                if (pick == null) continue;
                if (curS != null && pick != curS && role == "shooter" && !Better(curEff, ttw)) { pick = curS; ttw = curEff; }
                used.Add(pick.Uid); done.Add(t.Id);
                res.Add(new StrikeAssign { Target = t.Id, Uid = pick.Uid, Role = role, TtwS = ttw });
            }
            // second numbers: airborne free shooters, one per target, in the priority order
            int si = 0;
            var rest = new List<StrikeShooter>();
            foreach (var s in shooters) if (s.Air && !s.Busy && !used.Contains(s.Uid) && s.TtwS >= 0 && s.TtwS <= maxEtaS) rest.Add(s);
            foreach (var t in order)
            {
                if (si >= rest.Count) break;
                bool hasShooter = false;
                foreach (var a in res) if (a.Target == t.Id && a.Role != "second") hasShooter = true;
                if (!hasShooter) continue;
                var s = rest[si++];
                res.Add(new StrikeAssign { Target = t.Id, Uid = s.Uid, Role = "second", TtwS = s.TtwS });
            }
            return res;
        }

        /// <summary>StrikeClaims between strike groups: the group with the lowest known time to water wins; the holder stays unless another is Better(); ties keep the holder. null = nobody.</summary>
        public static string ClaimWinner(IDictionary<string, double> ttwByGroup, string holder)
        {
            string best = null; double bt = -1;
            foreach (var kv in ttwByGroup)
            {
                if (kv.Value < 0) continue;
                if (best == null || kv.Value < bt || (kv.Value == bt && kv.Key == holder)) { best = kv.Key; bt = kv.Value; }
            }
            if (best == null) return holder != null && ttwByGroup.ContainsKey(holder) ? holder : null;
            double ht;
            if (holder != null && best != holder && ttwByGroup.TryGetValue(holder, out ht) && ht >= 0 && !Better(ht, bt)) return holder;
            return best;
        }

        /// <summary>ADR-0074 item 6: on when an own / allied sub is nearer than safeNm; off after it stayed farther than safeNm + 0.5 for clearS. NaN (unreadable) = on.</summary>
        public static bool OwnSubHold(ref OwnSubLatch l, double nearestNm, double safeNm, double clearS, double now)
        {
            if (double.IsNaN(nearestNm)) { l.On = true; l.ClearSinceS = -1; return true; }
            if (nearestNm < safeNm) { l.On = true; l.ClearSinceS = -1; return true; }
            if (!l.On) return false;
            if (nearestNm > safeNm + 0.5)
            {
                if (l.ClearSinceS < 0) l.ClearSinceS = now;
                if (now - l.ClearSinceS >= clearS) { l.On = false; l.ClearSinceS = -1; }
            }
            else l.ClearSinceS = -1;
            return l.On;
        }

        /// <summary>After maxStreak misses in a row the next drop needs a fix not older than freshS (an unknown age counts as stale).</summary>
        public static bool NeedFreshFix(int missStreak, int maxStreak, double fixAgeS, double freshS)
        {
            if (maxStreak <= 0 || missStreak < maxStreak) return false;
            return fixAgeS < 0 || fixAgeS > freshS;
        }

        /// <summary>2026-10-22: the machine has a place to land that the game knows (a home ship / deck / air base). A land-based MPA of a mission
        /// without one (P-3C: getHomeBase() == null) is never sent to land: the game refuses with no_home_base.</summary>
        public static bool HasHome(AswMember m)
        {
            return m != null && (m.ShipUid != 0 || m.DeckUid != 0 || !string.IsNullOrEmpty(m.Base));
        }

        /// <summary>null = the machine may join a strike group (an ASW exclusion first; no torpedo and no torpedo loadout on the deck = no).</summary>
        public static string StrikeExclusion(AswMember m, bool torpedoLoadoutOnDeck)
        {
            string r = AswMath.ExclusionReason(m);
            if (r != null) return r;
            if (m.Where == "air") return m.Torpedoes > 0 ? null : "no_torpedo";
            if (m.Where != null && m.Where.StartsWith("deck", StringComparison.Ordinal))
            {
                if (m.Torpedoes > 0) return null;
                if (!m.LoadKnown && torpedoLoadoutOnDeck) return null;
                return torpedoLoadoutOnDeck ? null : "no_torpedo";
            }
            return "not_aircraft";
        }

        public static string SelfTest()
        {
            string e = EligibleTest() ?? OrderTest() ?? AssignTest() ?? LatchTest();
            return e;
        }

        private static StrikeTrack Tr(string side, double liveFor = 120, string cls = "Kilo")
        {
            return new StrikeTrack { Id = 7, Side = side, MapPosture = side == "hostile" ? "Enemy" : "Unknown", ClassName = cls, Classified = cls != null ? (bool?)true : null, Live = true, LiveForS = liveFor, Lat = 35, Lon = 20 };
        }

        private static string EligibleTest()
        {
            var p = new StrikePolicy();
            if (!Eligible(Tr("hostile"), p, null, null).Ok) return "elig: red";
            var g = Eligible(Tr("unknown", 60), p, null, null);
            if (!g.Ok || !g.Unknown) return "elig: grey 60 s ok";
            if (Eligible(Tr("unknown", 30), p, null, null).Reason != "unknown_young") return "elig: grey 30 s";
            if (Eligible(Tr("unknown", 90, null), p, null, null).Reason != "unclassified") return "elig: grey no class";
            var gen = Tr("unknown", 90, "Submarine"); gen.Classified = null;
            if (Eligible(gen, p, null, null).Reason != "unclassified") return "elig: grey generic class";
            var d = Tr("hostile"); d.Decoy = "Noisemaker";
            if (Eligible(d, p, null, null).Reason != "decoy") return "elig: decoy";
            d = Tr("hostile"); d.Biologic = "whale";
            if (Eligible(d, p, null, null).Reason != "biologic") return "elig: whale";
            d = Tr("unknown"); d.MapPosture = "Neutral";
            if (Eligible(d, p, null, null).Reason != "neutral") return "elig: neutral posture";
            d = Tr("unknown"); d.VmPosture = "Friendly";
            if (Eligible(d, p, null, null).Reason != "friendly") return "elig: friendly posture";
            if (Eligible(Tr("neutral"), p, null, null).Reason != "neutral" || Eligible(Tr("friendly"), p, null, null).Reason != "friendly") return "elig: neutral side";
            if (Eligible(Tr("none"), p, null, null).Reason != "not_hostile") return "elig: none side";
            var own = new HashSet<int> { 7 };
            if (Eligible(Tr("hostile"), p, null, own).Reason != "own_track") return "elig: own track id";
            var subs = new List<AswOwnSub> { new AswOwnSub { Uid = 1, Lat = 35.001, Lon = 20, Name = "Own" } };
            if (Eligible(Tr("hostile"), p, subs, null).Reason != "own_track") return "elig: own sub on top";
            subs[0].Lat = 35.2;
            if (!Eligible(Tr("hostile"), p, subs, null).Ok) return "elig: own sub 12 nm away";
            p.Targets = "hostile";
            if (Eligible(Tr("unknown", 300), p, null, null).Reason != "not_hostile") return "elig: hostile only";
            if (!Eligible(Tr("hostile"), p, null, null).Ok) return "elig: hostile only red";
            p = new StrikePolicy { RadiusNm = 10, HasGuard = true, Guard = new GeoPt(35, 20.5) };
            if (Eligible(Tr("hostile"), p, null, null).Reason != "out_of_zone") return "elig: out of zone";
            p.RadiusNm = 0;
            if (!Eligible(Tr("hostile"), p, null, null).Ok) return "elig: no radius = no zone";
            d = Tr("hostile"); d.Live = false;
            if (Eligible(d, new StrikePolicy(), null, null).Reason != "not_live") return "elig: not live";
            d = Tr("hostile"); d.Surfaced = true;
            if (Eligible(d, new StrikePolicy(), null, null).Reason != "surfaced") return "elig: surfaced";
            if (BiologicName("Kilo", "Submarine") != null || BiologicName(null, "Biologic") == null || BiologicName("Sperm Whale") == null || BiologicName("school of fish") == null) return "biologic name";
            if (!IsClassified(true, null) || !IsClassified(null, "Kilo") || IsClassified(null, "Submarine") || IsClassified(false, null)) return "classified rule";
            return null;
        }

        private static string OrderTest()
        {
            var a = new StrikeTarget { Id = 1, ShipNm = 5, FixAgeS = 10, Hostile = true };
            var b = new StrikeTarget { Id = 2, ShipNm = 9, FixAgeS = 5, Hostile = true };
            if (Priority(a, b) >= 0 || Priority(b, a) <= 0) return "prio: closer first";
            b.ShipNm = 5.2; a.FixAgeS = 40;
            if (Priority(b, a) >= 0) return "prio: fresher fix second";
            b.FixAgeS = 40; b.Hostile = false;
            if (Priority(a, b) >= 0) return "prio: hostile before unknown";
            if (Math.Abs(TtwAir(6, 120, false, 0) - 180) > 0.01 || Math.Abs(TtwAir(6, 120, true, 240) - 420) > 0.01) return "ttw air";
            if (TtwDeck(-1, 60, 5, 100) != -1) return "ttw deck unknown";
            if (Math.Abs(TtwDeck(300, 60, 10, 100) - (300 + 60 + 360)) > 0.01) return "ttw deck";
            if (Better(100, 80) || !Better(300, 100) || !Better(-1, 50) || Better(100, -1) || Better(150, 100)) return "better";
            return null;
        }

        private static StrikeShooter Sh(int uid, double ttw, bool air = true, bool busy = false, int target = 0, bool holder = false)
        {
            return new StrikeShooter { Uid = uid, TtwS = ttw, Air = air, Busy = busy, Target = target, Holder = holder };
        }

        private static StrikeAssign Find(List<StrikeAssign> l, int target, string role)
        {
            foreach (var a in l) if (a.Target == target && a.Role == role) return a;
            return null;
        }

        private static string AssignTest()
        {
            var ts = new List<StrikeTarget> { new StrikeTarget { Id = 1, ShipNm = 5, Hostile = true }, new StrikeTarget { Id = 2, ShipNm = 8, Hostile = true }, new StrikeTarget { Id = 3, ShipNm = 12, Hostile = true } };
            var sh = new List<StrikeShooter> { Sh(10, 200), Sh(11, 400), Sh(12, -1, false), };
            sh[2].TtwS = 900;
            var r = Assign(ts, sh, new Dictionary<int, int>(), 1800);
            var a1 = Find(r, 1, "shooter"); var a2 = Find(r, 2, "shooter"); var a3 = Find(r, 3, "deck");
            if (a1 == null || a1.Uid != 10 || a2 == null || a2.Uid != 11 || a3 == null || a3.Uid != 12) return "assign: 3 targets 2 air 1 deck";
            // extra airborne machine becomes the second number of the first target
            sh = new List<StrikeShooter> { Sh(10, 200), Sh(11, 400), Sh(13, 500) };
            r = Assign(new List<StrikeTarget> { ts[0], ts[1] }, sh, new Dictionary<int, int>(), 1800);
            var sec = Find(r, 1, "second");
            if (sec == null || sec.Uid != 13 || Find(r, 2, "second") != null) return "assign: second number of the top target";
            // busy shooters stay
            sh = new List<StrikeShooter> { Sh(10, 100), Sh(11, 400, true, true, 2) };
            r = Assign(new List<StrikeTarget> { ts[0], ts[1] }, sh, new Dictionary<int, int>(), 1800);
            if (Find(r, 2, "shooter") == null || Find(r, 2, "shooter").Uid != 11 || Find(r, 1, "shooter").Uid != 10) return "assign: busy not moved";
            // hysteresis: 400 -> 330 is no gain; the current one stays
            sh = new List<StrikeShooter> { Sh(10, 400), Sh(11, 330) };
            r = Assign(new List<StrikeTarget> { ts[0] }, sh, new Dictionary<int, int> { { 1, 10 } }, 1800);
            if (Find(r, 1, "shooter").Uid != 10) return "assign: hysteresis keeps current";
            sh = new List<StrikeShooter> { Sh(10, 900), Sh(11, 300) };
            r = Assign(new List<StrikeTarget> { ts[0] }, sh, new Dictionary<int, int> { { 1, 10 } }, 1800);
            if (Find(r, 1, "shooter").Uid != 11) return "assign: big gain moves";
            // too far
            r = Assign(new List<StrikeTarget> { ts[0] }, new List<StrikeShooter> { Sh(10, 2500) }, new Dictionary<int, int>(), 1800);
            if (r.Count != 0) return "assign: over MaxEtaS not assigned";
            // holder x2
            sh = new List<StrikeShooter> { Sh(10, 300, true, false, 0, true), Sh(11, 500) };
            r = Assign(new List<StrikeTarget> { ts[0] }, sh, new Dictionary<int, int>(), 1800);
            if (Find(r, 1, "shooter").Uid != 11) return "assign: holder counts x2";
            // deck with unknown time only when nobody is airborne
            r = Assign(new List<StrikeTarget> { ts[0] }, new List<StrikeShooter> { Sh(20, -1, false) }, new Dictionary<int, int>(), 1800);
            if (Find(r, 1, "deck") == null) return "assign: deck without time when nobody flies";
            r = Assign(new List<StrikeTarget> { ts[0] }, new List<StrikeShooter> { Sh(20, -1, false), Sh(10, 600) }, new Dictionary<int, int>(), 1800);
            if (Find(r, 1, "shooter") == null || Find(r, 1, "deck") != null) return "assign: air before a deck of unknown time";
            // deck faster than air
            r = Assign(new List<StrikeTarget> { ts[0] }, new List<StrikeShooter> { Sh(20, 200, false), Sh(10, 600) }, new Dictionary<int, int>(), 1800);
            if (Find(r, 1, "deck") == null) return "assign: faster deck wins";
            return null;
        }

        private static string LatchTest()
        {
            var l = new OwnSubLatch();
            if (!OwnSubHold(ref l, 3, 4, 20, 100)) return "latch: 3 nm on";
            if (!OwnSubHold(ref l, 4.2, 4, 20, 101)) return "latch: 4.2 nm still on";
            if (!OwnSubHold(ref l, 4.6, 4, 20, 110)) return "latch: 4.6 nm just started";
            if (!OwnSubHold(ref l, 4.6, 4, 20, 125)) return "latch: 15 s not enough";
            if (OwnSubHold(ref l, 4.6, 4, 20, 131)) return "latch: 21 s clears";
            if (!OwnSubHold(ref l, double.NaN, 4, 20, 140)) return "latch: unreadable = on";
            if (!OwnSubHold(ref l, 5, 4, 20, 141) || !OwnSubHold(ref l, 4.2, 4, 20, 170)) return "latch: reset by 4.2";
            var m = new OwnSubLatch();
            if (OwnSubHold(ref m, 9, 4, 20, 1)) return "latch: far from the start = off";
            if (NeedFreshFix(1, 2, 500, 90) || !NeedFreshFix(2, 2, 500, 90) || NeedFreshFix(2, 2, 30, 90) || !NeedFreshFix(3, 2, -1, 90) || NeedFreshFix(5, 0, 500, 90)) return "fresh fix";
            var w = new Dictionary<string, double> { { "A", 300 }, { "B", 100 } };
            if (ClaimWinner(w, "A") != "B") return "claim: lower wins";
            w["B"] = 280;
            if (ClaimWinner(w, "A") != "A") return "claim: holder keeps";
            w["B"] = 100; w["A"] = 100;
            if (ClaimWinner(w, "A") != "A") return "claim: tie keeps holder";
            if (ClaimWinner(new Dictionary<string, double> { { "A", -1 } }, null) != null) return "claim: unknown";
            var air = new AswMember { Where = "air", Torpedoes = 2, Passive = 5 };
            if (StrikeExclusion(air, false) != null) return "excl: armed in the air";
            air.Torpedoes = 0; air.Passive = 5;
            if (StrikeExclusion(air, true) != "no_torpedo") return "excl: no torpedo in the air";
            var deck = new AswMember { Where = "deck_ready", Torpedoes = 0, Passive = 5 };
            if (StrikeExclusion(deck, false) != "no_torpedo" || StrikeExclusion(deck, true) != null) return "excl: deck loadout";
            if (StrikeExclusion(new AswMember { Where = "player", Torpedoes = 2 }, true) != "player") return "excl: asw reason first";
            if (HasHome(null) || HasHome(new AswMember { Uid = 159, Type = "P-3C", Where = "air" }) || !HasHome(new AswMember { Where = "air", ShipUid = 8 })
                || !HasHome(new AswMember { Where = "air", Base = "Keflavik" }) || !HasHome(new AswMember { Where = "air", DeckUid = 8 })) return "HasHome";
            return null;
        }
    }
}
