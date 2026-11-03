using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-109 / ADR-0039: pure SAR math (candidates, registry, loadout, relief plan). No Unity, no reflection.
    internal sealed class SarParams
    {
        public double ThreatStandoffNm = 10, UnreachableS = 600, NoRaftsGraceS = 60, TransitAltFt = 500,
                      ShipFinalNm = 1.0, SubSurfaceNm = 2, NextRaftNearNm = 3, ProgressMinS = 120,
                      MinDepthM = 8, SubTransitDepthM = 30, ShoreMarginNm = 0.4,
                      PriorityBonusS = 900, SharedPenaltyS = 600,   // 0.28.1: a priority class is worth this much travel time; a raft another rescuer is already going to costs this much more
                      RearmMaxMin = 20;   // live finding 38: a ready helicopter with fewer seats is re-armed on deck to the SAR loadout only when that takes at most this long (mission minutes); 0 = never
        public int Standby = 1, MaxReacquire = 3, ProbeBudget = 1500, MaxCells = 40000;
        public string PriorityRafts = "all_life_raft_single,all_pod_president",
                      LoadoutTieBreak = "Empty,Ferry,Transport,Default",
                      ShipTransitTelegraph = "current", ShipOnComplete = "restore", LaunchMissionType = "sar";
        public bool ForceApproachSlow, Debug;
    }

    internal sealed class SarRaft { public int Id; public string Type; public double Lat, Lon; public int People; public bool Destroyed, OnLand; }
    internal sealed class SarThreat { public int Id; public double Lat, Lon; public bool AirDefense; }
    internal sealed class SarRescuer { public int Uid; public string Cls; public double Lat, Lon, SpeedKn, RangeNm; public int Seats; }
    internal sealed class SarClaim { public int RaftId, Rescuer, Seats, Attempts; public string Cls; public double Since, Eta, LastProgressS, LastDistNm; }

    internal sealed class SarArea
    {
        public string Kind = "all";          // all|rect|circle
        public double[] Rect;                // lat0, lon0, lat1, lon1
        public GeoPt Center; public double RadiusNm;
        public bool Contains(GeoPt g)
        {
            if (Kind == "rect" && Rect != null && Rect.Length >= 4)
                return g.Lat >= Math.Min(Rect[0], Rect[2]) && g.Lat <= Math.Max(Rect[0], Rect[2])
                    && g.Lon >= Math.Min(Rect[1], Rect[3]) && g.Lon <= Math.Max(Rect[1], Rect[3]);
            if (Kind == "circle")
            {
                var d = AswMath.FlatNm(Center, g);
                return Math.Sqrt(d.X * d.X + d.Y * d.Y) <= RadiusNm;
            }
            return true;
        }
    }

    internal sealed class SarHelo
    {
        public int Uid, DeckUid, Onboard, Capacity, BestCapacity;
        public string Type, Where, Loadout, PendingKey;
        public double FuelFrac, EnduranceS, TransitS, ReadyInS;
        public bool HasTarget, Ours;
        public double PrepS = -1;      // T-226: hangar row / cool: row - preparation of the SAR order (s, with the timing mode), -1 = neither
        public double CoolLeftS = -1;  // T-226: cool: row - cool-down left (s), -1 = none
        public bool CanRearm;    // live finding 38: a ready deck task of the group's deck and type that may be aborted and ordered again with the SAR loadout
        public string TaskKey;   // the deck task key of a ready helicopter ("task:<guid>"), for the re-arm
    }

    internal sealed class SarDemand { public int UncoveredRafts, People, SeatsAir, SeatsReserve; public bool Loiter; public double NoRaftsSince = -1; }
    internal sealed class SarAction
    {
        public string Kind;          // launch | rearm | hold | order | abort | warn
        public int Uid, DeckUid;
        public string Type, Loadout, Key, Code;
        public double EtaS;          // order / rearm: seconds until ready; launch: 0
        // live finding 38: the loadout decision of a ready helicopter (launch / rearm / hold), for the [Sar] loadout journal line
        public string Why, FromLoadout, BestLoadout;   // rearm: Loadout = the SAR loadout ordered, FromLoadout = the loadout aborted; launch / hold: BestLoadout = the SAR loadout of the deck
        public int Seats, BestSeats, People;
        public double PrepS;         // the preparation of the SAR loadout on this deck (s, after the deck timings mode)
    }

    internal sealed class SarRegistry                 // shared by helicopters, ships, subs; not saved
    {
        private readonly List<SarClaim> _claims = new List<SarClaim>();
        private readonly Dictionary<string, double> _unreach = new Dictionary<string, double>();

        public IReadOnlyList<SarClaim> Claims { get { return _claims; } }

        public int SeatsOn(int raftId)
        {
            long n = 0;
            foreach (var c in _claims) if (c.RaftId == raftId) n += c.Seats;
            return n > int.MaxValue ? int.MaxValue : (int)n;
        }

        public bool Covered(SarRaft r) { return SeatsOn(r.Id) >= r.People; }

        public string TryClaim(SarRaft r, SarRescuer s, double now, double eta)
        {
            if (ClaimOf(s.Uid) != null) return "busy";
            if (Unreachable(r.Id, s.Cls, now)) return "unreachable";
            if (Covered(r)) return "covered";
            _claims.Add(new SarClaim { RaftId = r.Id, Rescuer = s.Uid, Seats = s.Seats, Cls = s.Cls, Since = now, Eta = eta, LastProgressS = now, LastDistNm = -1 });
            return null;
        }

        public SarClaim ClaimOf(int rescuerUid)
        {
            foreach (var c in _claims) if (c.Rescuer == rescuerUid) return c;
            return null;
        }

        public int Release(int rescuerUid, string reason)
        {
            return _claims.RemoveAll(c => c.Rescuer == rescuerUid);
        }

        public List<KeyValuePair<SarClaim, string>> Sweep(IList<SarRaft> rafts, IList<SarThreat> threats, Func<int, GeoPt?> rescuerPos, double now, SarParams p)
        {
            var res = new List<KeyValuePair<SarClaim, string>>();
            foreach (var c in _claims.ToArray())
            {
                SarRaft r = null;
                if (rafts != null) foreach (var x in rafts) if (x.Id == c.RaftId) { r = x; break; }
                string why = null;
                if (r == null || r.Destroyed || r.People <= 0) why = "picked";
                else if (SarMath.UnderThreat(r, threats, p.ThreatStandoffNm)) why = "threat";
                else
                {
                    GeoPt? pos = rescuerPos == null ? null : rescuerPos(c.Rescuer);
                    if (pos.HasValue)
                    {
                        var d = AswMath.FlatNm(pos.Value, new GeoPt(r.Lat, r.Lon));
                        double dist = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                        if (c.LastDistNm < 0 || dist < c.LastDistNm - 0.05) { c.LastDistNm = dist; c.LastProgressS = now; }
                    }
                    if (now - c.LastProgressS > Math.Max(2 * c.Eta, c.Eta + p.ProgressMinS)) why = "stalled";
                }
                if (why != null) { _claims.Remove(c); res.Add(new KeyValuePair<SarClaim, string>(c, why)); }
            }
            return res;
        }

        public bool NoteMiss(int rescuerUid, double now, SarParams p)
        {
            var c = ClaimOf(rescuerUid);
            if (c == null) return false;
            c.Attempts++;
            if (c.Attempts < p.MaxReacquire) return false;
            MarkUnreachable(c.RaftId, c.Cls, now, p.UnreachableS);
            _claims.Remove(c);
            return true;
        }

        private static string UKey(int raftId, string cls) { return raftId + "|" + cls; }

        public void MarkUnreachable(int raftId, string cls, double now, double forS) { _unreach[UKey(raftId, cls)] = now + forS; }

        public bool Unreachable(int raftId, string cls, double now)
        {
            double until;
            if (!_unreach.TryGetValue(UKey(raftId, cls), out until)) return false;
            if (now < until) return true;
            _unreach.Remove(UKey(raftId, cls));
            return false;
        }

        public void Clear() { _claims.Clear(); _unreach.Clear(); }
    }

    internal static class SarMath
    {
        private static double DistNm(double lat0, double lon0, double lat1, double lon1)
        {
            double x = (lat1 - lat0) * 60.0, y = (lon1 - lon0) * 60.0 * AswMath.LonK(lat0, lat1);
            return Math.Sqrt(x * x + y * y);
        }

        public static bool Candidate(SarRaft r, out string why)
        {
            why = null;
            if (r.Destroyed) { why = "destroyed"; return false; }
            if (r.People <= 0) { why = "empty"; return false; }
            if (r.OnLand) { why = "land"; return false; }
            return true;
        }

        /// <summary>Known hostile air defence (or unclassified) closer than standoffNm to the raft (flat game rule).</summary>
        public static bool UnderThreat(SarRaft r, IList<SarThreat> t, double standoffNm)
        {
            if (t == null) return false;
            foreach (var x in t)
                if (x.AirDefense && DistNm(r.Lat, r.Lon, x.Lat, x.Lon) < standoffNm) return true;
            return false;
        }

        public static int PriorityClass(string raftType, string priorityCsv)
        {
            var l = (priorityCsv ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < l.Length; i++) if (string.Equals(l[i].Trim(), raftType, StringComparison.OrdinalIgnoreCase)) return i;
            return l.Length;
        }

        public static SarRaft PickRaft(SarRescuer s, IList<SarRaft> rafts, SarRegistry reg, SarArea area, IList<SarThreat> threats,
                                       Func<SarRaft, double> travelS, double now, SarParams p, out string why)
        {
            SarRaft best = null; double bestT = 0;
            bool threat = false, unreach = false, range = false;
            if (rafts != null)
                foreach (var r in rafts)
                {
                    string w;
                    if (!Candidate(r, out w)) continue;
                    if (area != null && !area.Contains(new GeoPt(r.Lat, r.Lon))) continue;
                    if (reg != null && reg.Covered(r)) continue;
                    if (reg != null && reg.Unreachable(r.Id, s.Cls, now)) { unreach = true; continue; }
                    if (UnderThreat(r, threats, p.ThreatStandoffNm)) { threat = true; continue; }
                    if (s.RangeNm > 0 && DistNm(s.Lat, s.Lon, r.Lat, r.Lon) > s.RangeNm) { range = true; continue; }
                    // 0.28.1: one score in seconds. The priority class used to win at any distance (live: a ship next to rafts of 8 sailors
                    // was sent 70 nm to a single-pilot raft); now a class is worth PriorityBonusS of travel, and a raft that another rescuer
                    // already goes to (seats claimed, people left) costs SharedPenaltyS more, so rescuers spread over the rafts
                    int cls = PriorityClass(r.Type, p.PriorityRafts);
                    double t = travelS(r) + cls * p.PriorityBonusS + (reg != null && reg.SeatsOn(r.Id) > 0 ? p.SharedPenaltyS : 0);
                    if (best == null || t < bestT || (t == bestT && r.Id < best.Id))
                    { best = r; bestT = t; }
                }
            why = best != null ? null : threat ? "threat" : unreach ? "unreachable" : range ? "range" : "no_rafts";
            return best;
        }

        public static int PickSarLoadout(IList<LoadoutOption> los, SarParams p, ISet<string> noAmmo)
        {
            return AirOpsMath.PickBest(los, lo => lo.Available ? lo.Capacity : 0, p.LoadoutTieBreak, noAmmo);
        }

        public static string LosKey(int deckUid, string type) { return deckUid + "|" + (type ?? ""); }

        // T-344: a no_ammo refusal belongs to (deck, type, loadout): the same loadout name of another type (or deck) is not touched
        public static string NoAmmoKey(int deckUid, string type, string loadout) { return LosKey(deckUid, type) + "|" + (loadout ?? ""); }

        // T-344: loadout names refused for ONE (deck, type); keys of other pairs are ignored; null keys -> empty set
        public static ISet<string> NoAmmoOf(IEnumerable<string> keys, int deckUid, string type)
        {
            var res = new HashSet<string>();
            if (keys == null) return res;
            string pre = LosKey(deckUid, type) + "|";
            foreach (var k in keys) if (k != null && k.StartsWith(pre, StringComparison.Ordinal)) res.Add(k.Substring(pre.Length));
            return res;
        }

        /// <summary>T-343: a loadout without weapons: the game's "Empty", or one whose known ammunition is all zero (Ka-25Ts AEW, CH-46 Transport).
        /// Weapons == null (not read) -> by name only.</summary>
        public static bool IsEmptyLoadout(LoadoutOption lo)
        {
            if (lo == null) return false;
            if (string.Equals(lo.Name, "Empty", StringComparison.OrdinalIgnoreCase)) return true;
            if (lo.Weapons == null) return false;
            foreach (var kv in lo.Weapons) if (kv.Value > 0) return false;
            return true;
        }

        /// <summary>
        /// T-343: the SAR loadout of ONE type from ITS OWN list; never looks at another type. 1) PickSarLoadout (available, seats, tie-break);
        /// 2) none -> its own empty loadout with seats (Available ignored: the deck answers the order, refusals have their back-off);
        /// 3) none -> ANY own loadout with seats (customer decision 2026-11-02, even an armed one); noAmmo loadouts are skipped in 2 and 3.
        /// Among several: most seats, then LoadoutTieBreak. why: best | own_empty | own_any | none (-1).
        /// </summary>
        public static int PickOwnSarLoadout(IList<LoadoutOption> own, SarParams p, ISet<string> noAmmo, out string why)
        {
            int i = PickSarLoadout(own, p, noAmmo);
            if (i >= 0) { why = "best"; return i; }
            i = AirOpsMath.PickBest(own, lo => IsEmptyLoadout(lo) ? lo.Capacity : 0, p.LoadoutTieBreak, noAmmo);
            if (i >= 0) { why = "own_empty"; return i; }
            i = AirOpsMath.PickBest(own, lo => lo.Capacity, p.LoadoutTieBreak, noAmmo);
            if (i >= 0) { why = "own_any"; return i; }
            why = "none";
            return -1;
        }

        /// <summary>
        /// Live finding 38: a ready helicopter whose loadout has fewer seats than the best SAR loadout of its deck (an SH-2F with ASW: 2 seats,
        /// Empty: 8). True = abort the ready task and order it again with <paramref name="best"/> (the game has no loadout change of a ready
        /// machine: the airframe goes back to the hangar, its ammunition is returned, the new task prepares ReadyUp again). False = it goes as is.
        /// why: best_loadout | no_sar_loadout | enough_seats (its seats cover the people waiting) | cannot_rearm (not a task of the group's deck and
        /// type, or the deck refused the abort) | rearm_off | same_trips (the better loadout saves no trip) | prep_long (prepS over RearmMaxMin)
        /// | more_seats / no_seats (re-arm).
        /// </summary>
        public static bool DecideRearm(SarHelo h, int people, LoadoutOption best, string deckMode, SarParams p, out string why, out double prepS)
        {
            prepS = best == null ? 0 : AirOpsMath.ReadyS(best, deckMode, 0);
            if (best == null || best.Capacity <= 0) { why = "no_sar_loadout"; return false; }
            if (h.Capacity >= best.Capacity) { why = "best_loadout"; return false; }
            if (h.Capacity > 0 && people <= h.Capacity) { why = "enough_seats"; return false; }
            if (!h.CanRearm) { why = "cannot_rearm"; return false; }
            if (p.RearmMaxMin <= 0) { why = "rearm_off"; return false; }
            if (h.Capacity <= 0) { why = "no_seats"; return true; }   // useless as it is: whatever the preparation time
            if (Trips(people, h.Capacity) <= Trips(people, best.Capacity)) { why = "same_trips"; return false; }
            if (prepS > p.RearmMaxMin * 60) { why = "prep_long"; return false; }
            why = "more_seats";
            return true;
        }

        /// <summary>Flights needed to carry people with seats per flight (ceil).</summary>
        public static int Trips(int people, int seats)
        {
            if (seats <= 0) return int.MaxValue;
            return people <= 0 ? 0 : (people + seats - 1) / seats;
        }

        /// <summary>Uncovered rafts in the area that a rescuer of class cls may take (not unreachable, not under threat).</summary>
        public static int Demand(IList<SarRaft> rafts, SarRegistry reg, SarArea area, IList<SarThreat> threats, string cls, double now, SarParams p)
        {
            int n = 0;
            if (rafts == null) return 0;
            foreach (var r in rafts)
            {
                string w;
                if (!Candidate(r, out w)) continue;
                if (area != null && !area.Contains(new GeoPt(r.Lat, r.Lon))) continue;
                if (reg != null && (reg.Covered(r) || reg.Unreachable(r.Id, cls, now))) continue;
                if (UnderThreat(r, threats, p.ThreatStandoffNm)) continue;
                n++;
            }
            return n;
        }

        public static string LeaveReason(SarHelo h, SarDemand d, double now, SarParams p)
        {
            if (h.Capacity > 0 && h.Onboard >= h.Capacity) return "full";
            double fuel = Math.Max(0, (h.FuelFrac * h.EnduranceS - h.TransitS) * 0.9);
            if (fuel <= 0) return "fuel";
            if (d.UncoveredRafts <= 0 && !h.HasTarget && d.NoRaftsSince >= 0 && now - d.NoRaftsSince >= p.NoRaftsGraceS)
                return d.Loiter ? null : "no_rafts";
            return null;
        }

        private static LoadoutOption FindLo(IList<LoadoutOption> los, string name)
        {
            if (los != null) foreach (var l in los) if (l.Name == name) return l;
            return null;
        }

        private static bool InPipeline(SarHelo h)
        {
            return !string.IsNullOrEmpty(h.PendingKey) || h.Where == "deck_prep" || h.Where == "deck_service";
        }

        /// <summary>Seconds to the next SAR launch from the deck: 0 = a ready helo; CoolDown+ReadyUp after landing; -1 = none.</summary>
        public static double NextLaunchS(IList<SarHelo> m, IList<LoadoutOption> deckLos, string deckMode)
        {
            return NextLaunchS(m, (dk, ty) => deckLos, deckMode);
        }

        /// <summary>T-343: the loadouts of each helicopter come from its own type's list.</summary>
        public static double NextLaunchS(IList<SarHelo> m, Func<int, string, IList<LoadoutOption>> losOf, string deckMode)
        {
            double best = -1;
            foreach (var h in m)
            {
                double t;
                if (h.Where == "deck_ready") t = 0;
                else if (InPipeline(h)) t = h.ReadyInS;
                else if (h.Where == "landed" && h.PrepS >= 0) t = Math.Max(0, h.CoolLeftS) + h.PrepS;   // T-226: hangar row = preparation only; cool: row = cool-down left + preparation
                else if (h.Where == "deck_recover" || h.Where == "landed")
                {
                    var lo = FindLo(losOf(h.DeckUid, h.Type), h.Loadout);
                    t = AirOpsMath.CoolDownS(lo, deckMode) + AirOpsMath.ReadyS(lo, deckMode, 0);
                }
                else continue;
                if (best < 0 || t < best) best = t;
            }
            return best;
        }

        /// <summary>SAR branch of the relief plan (ADR-0036 s6 + ADR-0039 s4, D1).</summary>
        public static List<SarAction> PlanSarRelief(IList<SarHelo> m, SarDemand d, IList<LoadoutOption> deckLos, string deckMode, double now, SarParams p)
        {
            return PlanSarRelief(m, d, (dk, ty) => deckLos, null, deckMode, now, p);
        }

        /// <summary>T-343: the plan with a loadout list per (deck, type): every helicopter gets the SAR loadout of its OWN type (PickOwnSarLoadout).</summary>
        public static List<SarAction> PlanSarRelief(IList<SarHelo> m, SarDemand d, Func<int, string, IList<LoadoutOption>> losOf, Func<int, string, ISet<string>> noAmmoOf,
                                                    string deckMode, double now, SarParams p)
        {
            var res = new List<SarAction>();
            var pickBy = new Dictionary<string, KeyValuePair<LoadoutOption, string>>();
            Func<SarHelo, LoadoutOption> bestOf = h =>
            {
                if (h.DeckUid == 0) return null;
                string k = LosKey(h.DeckUid, h.Type);
                KeyValuePair<LoadoutOption, string> kv;
                if (!pickBy.TryGetValue(k, out kv))
                {
                    var own = losOf(h.DeckUid, h.Type);
                    string w = "none";
                    int i = own == null ? -1 : PickOwnSarLoadout(own, p, noAmmoOf == null ? null : noAmmoOf(h.DeckUid, h.Type), out w);
                    kv = new KeyValuePair<LoadoutOption, string>(i >= 0 ? own[i] : null, w);
                    pickBy[k] = kv;
                }
                return kv.Key;
            };
            Func<SarHelo, int> capOf = h => { var b = bestOf(h); return b == null ? 0 : b.Capacity; };
            int air = 0, airIdle = 0;
            var ready = new List<SarHelo>();
            foreach (var h in m)
            {
                if (h.Where == "air") { air++; if (!h.HasTarget) airIdle++; }
                else if (h.Where == "deck_ready" && string.IsNullOrEmpty(h.PendingKey)) ready.Add(h);
            }
            ready.Sort((a, b) => b.Capacity != a.Capacity ? b.Capacity.CompareTo(a.Capacity) : a.Uid.CompareTo(b.Uid));

            int wanted = Math.Max(0, d.UncoveredRafts - airIdle);
            int launched = 0, remaining = Math.Max(0, d.People);
            var launchedSet = new HashSet<SarHelo>();
            var rearmSet = new HashSet<SarHelo>();
            foreach (var h in ready)
            {
                if (launched + rearmSet.Count >= wanted) break;
                var bestLo = bestOf(h); int bestCap = capOf(h);
                string why = "best_loadout"; double prepS = 0;
                if (bestCap > 0 && h.Capacity < bestCap)
                {
                    // live finding 38: fewer seats than the SAR loadout - re-arm on deck, go as is, or wait for a better one
                    if (DecideRearm(h, remaining, bestLo, deckMode, p, out why, out prepS))
                    {
                        res.Add(new SarAction { Kind = "rearm", Uid = h.Uid, DeckUid = h.DeckUid, Type = h.Type, Loadout = bestLo.Name, FromLoadout = h.Loadout, Key = h.TaskKey,
                                                EtaS = prepS, PrepS = prepS, Why = why, Seats = h.Capacity, BestSeats = bestCap, People = remaining });
                        rearmSet.Add(h); remaining = Math.Max(0, remaining - bestCap);
                        continue;
                    }
                    if (h.Capacity <= 0) continue;
                    if (why != "enough_seats" && air + launched > 0)   // wait for a better one
                    {
                        res.Add(new SarAction { Kind = "hold", Uid = h.Uid, DeckUid = h.DeckUid, Type = h.Type, Loadout = h.Loadout, Why = why, Seats = h.Capacity, BestSeats = bestCap, People = remaining, PrepS = prepS, BestLoadout = bestLo.Name });
                        continue;
                    }
                }
                else if (h.Capacity <= 0) continue;
                res.Add(new SarAction { Kind = "launch", Uid = h.Uid, DeckUid = h.DeckUid, Type = h.Type, Loadout = h.Loadout, EtaS = 0,
                                        Why = why, Seats = h.Capacity, BestSeats = bestCap, People = remaining, PrepS = prepS, BestLoadout = bestLo == null ? null : bestLo.Name });
                launchedSet.Add(h); launched++; remaining = Math.Max(0, remaining - h.Capacity);
            }

            // pipeline: ready helos of full capacity (not launched) + pending/preparing ones
            int need = Math.Max(0, wanted - launched) + Math.Max(0, p.Standby);
            var pipe = new List<SarHelo>();
            var decks = new Dictionary<int, int>();
            foreach (var h in m)
            {
                if (h.DeckUid != 0 && !decks.ContainsKey(h.DeckUid)) decks[h.DeckUid] = 0;
                if (launchedSet.Contains(h)) continue;
                if (rearmSet.Contains(h)) { pipe.Add(h); if (h.DeckUid != 0) decks[h.DeckUid]++; continue; }   // its new task of the SAR loadout is coming
                bool isReady = h.Where == "deck_ready" && string.IsNullOrEmpty(h.PendingKey) && h.Capacity >= capOf(h) && h.Capacity > 0;
                if (isReady || InPipeline(h)) { pipe.Add(h); if (h.DeckUid != 0) decks[h.DeckUid]++; }
            }
            int have = pipe.Count;
            if (have < need)
            {
                // T-343: a deck can order when one of its helicopters has a loadout of its own type (first such one: type + loadout of the order)
                var orderType = new Dictionary<int, SarHelo>();
                foreach (var h in m)
                    if (h.DeckUid != 0 && !orderType.ContainsKey(h.DeckUid) && bestOf(h) != null) orderType[h.DeckUid] = h;
                if (orderType.Count == 0)
                {
                    if (d.UncoveredRafts > 0 || d.People > 0)
                    {
                        // T-147: seats exist (airborne or on deck) but no deck can prepare a reserve -> not "no capacity"
                        bool anySeats = false;
                        foreach (var h in m) if (h.Capacity > 0) { anySeats = true; break; }
                        res.Add(new SarAction { Kind = "warn", Code = anySeats ? "no_reserve_deck" : "no_capacity" });
                    }
                }
                else
                {
                    // live 0.28.7: one order per deck and tick (the executor sends one per deck anyway); the next tick sees the new task and asks again
                    var ordered = new HashSet<int>();
                    for (int k = have; k < need; k++)
                    {
                        int deck = 0, cnt = int.MaxValue;
                        foreach (var kv in decks) if (orderType.ContainsKey(kv.Key) && (kv.Value < cnt || (kv.Value == cnt && kv.Key < deck))) { deck = kv.Key; cnt = kv.Value; }
                        decks[deck]++;
                        if (!ordered.Add(deck)) continue;
                        var oh = orderType[deck]; var olo = bestOf(oh);
                        string ow = pickBy[LosKey(oh.DeckUid, oh.Type)].Value;
                        res.Add(new SarAction { Kind = "order", DeckUid = deck, Type = oh.Type, Loadout = olo.Name, EtaS = AirOpsMath.ReadyS(olo, deckMode, 0),
                                                Why = ow == "own_empty" || ow == "own_any" ? ow : null });
                    }
                }
            }
            else if (have > need)
            {
                int surplus = have - need;
                for (int i = pipe.Count - 1; i >= 0 && surplus > 0; i--)
                {
                    var h = pipe[i];
                    if (!h.Ours || string.IsNullOrEmpty(h.PendingKey)) continue;
                    res.Add(new SarAction { Kind = "abort", Uid = h.Uid, DeckUid = h.DeckUid, Type = h.Type, Loadout = h.Loadout, Key = h.PendingKey });
                    surplus--;
                }
            }

            if (d.People > 0 && d.People > d.SeatsAir + d.SeatsReserve)
                res.Add(new SarAction { Kind = "warn", Code = "low_capacity" });
            return res;
        }

        // live finding 38: a ready SH-2F with ASW (2 seats) and an Empty loadout (8 seats) on its deck
        private static void RearmTests(SarParams p0)
        {
            var p = new SarParams { RearmMaxMin = p0.RearmMaxMin };
            var los = new List<LoadoutOption> { Lo("ASW", 2), Lo("Empty", 8), Lo("Ferry", 8) };
            los[1].ReadyUpMin = 15; los[2].ReadyUpMin = 15; los[0].ReadyUpMin = 30;
            var empty = los[1];
            string why; double prep;
            Func<int, SarHelo> asw = cap => { var h = Helo(1, "deck_ready", cap); h.CanRearm = true; h.TaskKey = "task:a"; return h; };
            True("rearm 5 people", DecideRearm(asw(2), 5, empty, "Arcade", p, out why, out prep) && why == "more_seats" && Math.Abs(prep - 90) < 1e-6);
            True("as is 2 people", !DecideRearm(asw(2), 2, empty, "Arcade", p, out why, out prep) && why == "enough_seats");
            True("as is 1 person", !DecideRearm(asw(2), 1, empty, "Realistic", p, out why, out prep) && why == "enough_seats");
            True("realistic 15 min ok", DecideRearm(asw(2), 5, empty, "Realistic", p, out why, out prep) && Math.Abs(prep - 900) < 1e-6);
            var slow = Lo("Empty", 8); slow.ReadyUpMin = 30;
            True("prep long", !DecideRearm(asw(2), 5, slow, "Realistic", p, out why, out prep) && why == "prep_long" && Math.Abs(prep - 1800) < 1e-6);
            True("prep long arcade ok", DecideRearm(asw(2), 5, slow, "Arcade", p, out why, out prep));
            var six = asw(6);
            True("same trips", !DecideRearm(six, 10, empty, "Arcade", p, out why, out prep) && why == "same_trips");
            True("fewer trips", DecideRearm(six, 13, empty, "Arcade", p, out why, out prep) && why == "more_seats");
            var foreign = asw(2); foreign.CanRearm = false;
            True("cannot rearm", !DecideRearm(foreign, 5, empty, "Arcade", p, out why, out prep) && why == "cannot_rearm");
            True("best already", !DecideRearm(asw(8), 20, empty, "Arcade", p, out why, out prep) && why == "best_loadout");
            True("no sar loadout", !DecideRearm(asw(2), 5, null, "Arcade", p, out why, out prep) && why == "no_sar_loadout");
            True("no seats rearm even slow", DecideRearm(asw(0), 1, slow, "Realistic", p, out why, out prep) && why == "no_seats");
            var off = new SarParams { RearmMaxMin = 0 };
            True("rearm off", !DecideRearm(asw(2), 5, empty, "Arcade", off, out why, out prep) && why == "rearm_off");
            Eq("trips", Trips(5, 2), 3); Eq("trips 0", Trips(0, 2), 0); Eq("trips exact", Trips(8, 8), 1);

            // plan: nothing airborne, 5 people on one raft -> re-arm (abort + Empty), no ASW launch; the re-armed one counts in the pipeline
            var m1 = new List<SarHelo> { asw(2) };
            var a1 = PlanSarRelief(m1, new SarDemand { UncoveredRafts = 1, People = 5, SeatsReserve = 2 }, los, "Arcade", 0, p);
            var ra = a1.Find(x => x.Kind == "rearm");
            True("plan rearm", ra != null && ra.Loadout == "Empty" && ra.FromLoadout == "ASW" && ra.Key == "task:a" && ra.Seats == 2 && ra.BestSeats == 8 && ra.People == 5 && Count(a1, "launch") == 0);
            True("plan rearm + standby only", Count(a1, "order") == 1);   // need = 1 wanted + 1 standby, the re-armed one is one of them
            // 2 people: goes as is
            var a2 = PlanSarRelief(new List<SarHelo> { asw(2) }, new SarDemand { UncoveredRafts = 1, People = 2 }, los, "Arcade", 0, p);
            True("plan as is", Count(a2, "launch") == 1 && Count(a2, "rearm") == 0 && a2.Find(x => x.Kind == "launch").Why == "enough_seats");
            // a foreign task (abort refused before): as is, nothing airborne (the old rule)
            var f = asw(2); f.CanRearm = false;
            var a3 = PlanSarRelief(new List<SarHelo> { f }, new SarDemand { UncoveredRafts = 1, People = 5 }, los, "Arcade", 0, p);
            True("plan foreign as is", Count(a3, "launch") == 1 && a3.Find(x => x.Kind == "launch").Why == "cannot_rearm");
            // two rafts, two ready ASW: both re-armed (each counts against one wanted launch), no third one
            var b1 = asw(2); var b2 = asw(2); b2.Uid = 2; b2.TaskKey = "task:b";
            var a4 = PlanSarRelief(new List<SarHelo> { b1, b2 }, new SarDemand { UncoveredRafts = 2, People = 12 }, los, "Arcade", 0, p);
            True("plan two rearms", Count(a4, "rearm") == 2 && Count(a4, "launch") == 0);
            // one raft of 3 + a ready Empty one and a ready ASW one: the Empty one goes, 0 people left for the ASW one -> not touched
            var e1 = Helo(3, "deck_ready", 8, 1, "SH-2F", "Empty");
            var a5 = PlanSarRelief(new List<SarHelo> { asw(2), e1 }, new SarDemand { UncoveredRafts = 1, People = 3 }, los, "Arcade", 0, p);
            True("plan best first", Count(a5, "launch") == 1 && a5.Find(x => x.Kind == "launch").Uid == 3 && Count(a5, "rearm") == 0);
            // long preparation (Realistic, 30 min) and nothing airborne -> as is
            var lslow = new List<LoadoutOption> { Lo("ASW", 2), slow };
            var a6 = PlanSarRelief(new List<SarHelo> { asw(2) }, new SarDemand { UncoveredRafts = 1, People = 5 }, lslow, "Realistic", 0, p);
            True("plan prep long", Count(a6, "launch") == 1 && a6.Find(x => x.Kind == "launch").Why == "prep_long");
            // live 0.28.7: 22 rafts, one ready ASW task + one hangar airframe ("landed"): one re-arm, one order of the deck per tick (not 21)
            var hang = Helo(4, "landed", 2); hang.DeckUid = 1;
            var a7 = PlanSarRelief(new List<SarHelo> { asw(2), hang }, new SarDemand { UncoveredRafts = 22, People = 169 }, los, "Realistic", 0, p);
            True("plan one order per deck", Count(a7, "rearm") == 1 && Count(a7, "order") == 1 && Count(a7, "launch") == 0);
            var two = Helo(5, "landed", 2); two.DeckUid = 2;
            var a8 = PlanSarRelief(new List<SarHelo> { hang, two }, new SarDemand { UncoveredRafts = 5, People = 20 }, los, "Arcade", 0, p);
            True("plan one order per deck, two decks", Count(a8, "order") == 2 && a8.FindAll(x => x.Kind == "order").ConvertAll(x => x.DeckUid).Contains(2));
        }


        // T-343: the loadout of each helicopter comes from its own type's list
        private static LoadoutOption LoW(string n, int cap, bool avail = true, params string[] ammo)
        {
            var l = Lo(n, cap, avail); l.Weapons = new Dictionary<string, int>();
            foreach (var a in ammo) l.Weapons[a] = 5;
            return l;
        }

        private static void T343Tests(SarParams p)
        {
            string why;
            True("empty by name", IsEmptyLoadout(Lo("Empty", 8)));
            True("empty by content", IsEmptyLoadout(LoW("AEW", 10)));
            True("armed not empty", !IsEmptyLoadout(LoW("Strike", 16, true, "fab")));
            True("unknown content, by name only", !IsEmptyLoadout(Lo("Transport", 22)));
            var ka29 = new List<LoadoutOption> { LoW("Strike", 16, true, "fab"), LoW("CAS", 16, true, "x"), LoW("Empty", 16) };
            True("ka29 empty", ka29[PickOwnSarLoadout(ka29, p, null, out why)].Name == "Empty" && why == "best");
            var ka25 = new List<LoadoutOption> { LoW("AEW", 10) };
            True("ka25ts aew", ka25[PickOwnSarLoadout(ka25, p, null, out why)].Name == "AEW" && why == "best");
            var un = new List<LoadoutOption> { LoW("ASW", 2, false, "buoy"), LoW("Empty", 8, false) };
            True("own empty", un[PickOwnSarLoadout(un, p, null, out why)].Name == "Empty" && why == "own_empty");
            var ne = new HashSet<string> { "Empty" };
            True("own empty in noammo -> any", un[PickOwnSarLoadout(un, p, ne, out why)].Name == "ASW" && why == "own_any");
            var ch46 = new List<LoadoutOption> { LoW("Transport", 22, false) };
            True("ch46 unavailable", ch46[PickOwnSarLoadout(ch46, p, null, out why)].Name == "Transport" && why == "own_empty");
            True("ch46 noammo -> none", PickOwnSarLoadout(ch46, p, new HashSet<string> { "Transport" }, out why) == -1 && why == "none");
            var armed = new List<LoadoutOption> { LoW("Strike", 16, false, "fab"), LoW("CAS", 10, false, "x") };
            True("only armed unavailable -> any", armed[PickOwnSarLoadout(armed, p, null, out why)].Name == "Strike" && why == "own_any");
            True("no list", PickOwnSarLoadout(new List<LoadoutOption>(), p, null, out why) == -1 && why == "none");
            True("zero seats", PickOwnSarLoadout(new List<LoadoutOption> { LoW("Empty", 0, false) }, p, null, out why) == -1);

            var ka27 = new List<LoadoutOption> { LoW("ASW", 10, true, "buoy"), LoW("Strike", 10, true, "fab"), LoW("Empty", 10) };
            var sh2f = new List<LoadoutOption> { LoW("ASW", 2, true, "buoy"), LoW("Empty", 8) };
            var ch = new List<LoadoutOption> { LoW("Transport", 22) };
            Func<Dictionary<string, IList<LoadoutOption>>, Func<int, string, IList<LoadoutOption>>> by = d => (dk, ty) =>
            { IList<LoadoutOption> l; return ty != null && d.TryGetValue(ty, out l) ? l : new List<LoadoutOption>(); };
            var dem = new SarDemand { UncoveredRafts = 3, People = 6 };
            // (a) anchor Ka-25Ts + Ka-27
            var losA = by(new Dictionary<string, IList<LoadoutOption>> { { "wp_ka-25ts", ka25 }, { "wp_ka-27", ka27 } });
            var ma = new List<SarHelo> { Helo(1, "landed", 10, 1, "wp_ka-25ts", "AEW"), Helo(2, "landed", 10, 61, "wp_ka-27", "Empty") };
            var aa = PlanSarRelief(ma, dem, losA, null, "Arcade", 0, p);
            var o1 = aa.Find(x => x.Kind == "order" && x.DeckUid == 1); var o61 = aa.Find(x => x.Kind == "order" && x.DeckUid == 61);
            True("mixed a", o1 != null && o1.Type == "wp_ka-25ts" && o1.Loadout == "AEW" && o61 != null && o61.Type == "wp_ka-27" && o61.Loadout == "Empty");
            // (b) SH-2F + CH-46
            var losB = by(new Dictionary<string, IList<LoadoutOption>> { { "usn_sh-2f", sh2f }, { "usn_ch-46", ch } });
            var mb = new List<SarHelo> { Helo(1, "landed", 8, 1, "usn_sh-2f", "Empty"), Helo(2, "landed", 22, 62, "usn_ch-46", "Transport") };
            var ab = PlanSarRelief(mb, dem, losB, null, "Arcade", 0, p);
            var ob = ab.Find(x => x.Kind == "order" && x.DeckUid == 62);
            True("mixed b", ob != null && ob.Type == "usn_ch-46" && ob.Loadout == "Transport" && ab.Find(x => x.Kind == "order" && x.DeckUid == 1).Loadout == "Empty");
            // (c) ready Ka-29 Strike (16 seats) next to Ka-27: capacity from its own list -> launch as is, no rearm
            var losC = by(new Dictionary<string, IList<LoadoutOption>> { { "wp_ka-27", ka27 }, { "wp_ka-29", ka29 } });
            var k29 = Helo(3, "deck_ready", 16, 62, "wp_ka-29", "Strike"); k29.CanRearm = true; k29.TaskKey = "t";
            var ac = PlanSarRelief(new List<SarHelo> { Helo(2, "landed", 10, 61, "wp_ka-27", "Empty"), k29 }, new SarDemand { UncoveredRafts = 1, People = 12 }, losC, null, "Arcade", 0, p);
            var lc = ac.Find(x => x.Kind == "launch");
            True("ready ka29", lc != null && lc.Uid == 3 && lc.Why == "best_loadout" && lc.BestSeats == 16 && Count(ac, "rearm") == 0);
            // (d) ready SH-2F ASW next to Ka-27: rearm into its own Empty
            var losD = by(new Dictionary<string, IList<LoadoutOption>> { { "wp_ka-27", ka27 }, { "usn_sh-2f", sh2f } });
            var s2 = Helo(3, "deck_ready", 2, 62, "usn_sh-2f", "ASW"); s2.CanRearm = true; s2.TaskKey = "t";
            var ad = PlanSarRelief(new List<SarHelo> { Helo(2, "landed", 10, 61, "wp_ka-27", "Empty"), s2 }, new SarDemand { UncoveredRafts = 1, People = 6 }, losD, null, "Arcade", 0, p);
            var rd = ad.Find(x => x.Kind == "rearm");
            True("rearm own type", rd != null && rd.Uid == 3 && rd.Loadout == "Empty" && rd.BestSeats == 8);
            // (e) a type without any loadout with seats: no order for its deck; none anywhere -> warn
            var losE = by(new Dictionary<string, IList<LoadoutOption>> { { "wp_ka-27", ka27 } });
            var me = new List<SarHelo> { Helo(1, "landed", 0, 1, "usn_ah-1", "Gun"), Helo(2, "landed", 10, 61, "wp_ka-27", "Empty") };
            var ae = PlanSarRelief(me, dem, losE, null, "Arcade", 0, p);
            True("no loadout deck skipped", ae.Find(x => x.Kind == "order" && x.DeckUid == 1) == null && ae.Find(x => x.Kind == "order" && x.DeckUid == 61) != null);
            var af = PlanSarRelief(new List<SarHelo> { Helo(1, "landed", 0, 1, "usn_ah-1", "Gun") }, dem, losE, null, "Arcade", 0, p);
            True("no loadout anywhere", Count(af, "order") == 0 && af.Find(x => x.Kind == "warn" && x.Code == "no_capacity") != null);
            // own_empty reported; noAmmo respected
            var losG = by(new Dictionary<string, IList<LoadoutOption>> { { "wp_ka-29", new List<LoadoutOption> { LoW("Strike", 16, false, "fab"), LoW("Empty", 16, false) } } });
            var ag = PlanSarRelief(new List<SarHelo> { Helo(3, "landed", 16, 62, "wp_ka-29", "Strike") }, new SarDemand { UncoveredRafts = 1, People = 3 }, losG, null, "Arcade", 0, p);
            var og = ag.Find(x => x.Kind == "order");
            True("order why own_empty", og != null && og.Loadout == "Empty" && og.Why == "own_empty");
            var ah = PlanSarRelief(new List<SarHelo> { Helo(3, "landed", 16, 62, "wp_ka-29", "Strike") }, new SarDemand { UncoveredRafts = 1, People = 3 }, losG, (dk, ty) => dk == 62 && ty == "wp_ka-29" ? new HashSet<string> { "Empty" } : null, "Arcade", 0, p);
            True("order noammo -> any", ah.Find(x => x.Kind == "order").Loadout == "Strike");
            // T-344: a refusal belongs to (deck, type, loadout)
            True("noammo key", NoAmmoKey(62, "wp_ka-29", "Empty") == "62|wp_ka-29|Empty");
            var nk = new List<string> { "62|wp_ka-29|Empty", "61|wp_ka-27|Strike" };
            True("noammo of pair", NoAmmoOf(nk, 61, "wp_ka-27").Count == 1 && NoAmmoOf(nk, 61, "wp_ka-27").Contains("Strike") && NoAmmoOf(nk, 63, "wp_ka-27").Count == 0 && NoAmmoOf(null, 61, "x").Count == 0);
            var losT = by(new Dictionary<string, IList<LoadoutOption>> { { "wp_ka-27", ka27 }, { "wp_ka-29", new List<LoadoutOption> { LoW("Strike", 16, false, "fab"), LoW("Empty", 16, false) } } });
            var at = PlanSarRelief(new List<SarHelo> { Helo(2, "landed", 10, 61, "wp_ka-27", "Empty"), Helo(3, "landed", 16, 62, "wp_ka-29", "Strike") }, new SarDemand { UncoveredRafts = 2, People = 6 }, losT,
                (dk, ty) => NoAmmoOf(new[] { "62|wp_ka-29|Empty" }, dk, ty), "Arcade", 0, p);
            True("noammo other type kept", at.Find(x => x.Kind == "order" && x.DeckUid == 61) != null && at.Find(x => x.Kind == "order" && x.DeckUid == 61).Loadout == "Empty"
                && at.Find(x => x.Kind == "order" && x.DeckUid == 62) != null && at.Find(x => x.Kind == "order" && x.DeckUid == 62).Loadout == "Strike");
            // NextLaunchS by type
            var strikeLand = Lo("Strike", 16); strikeLand.CoolDownMin = 5; strikeLand.ReadyUpMin = 3;
            var losN = by(new Dictionary<string, IList<LoadoutOption>> { { "wp_ka-27", ka27 }, { "wp_ka-29", new List<LoadoutOption> { strikeLand } } });
            Eq("next by type", NextLaunchS(new List<SarHelo> { Helo(3, "landed", 16, 62, "wp_ka-29", "Strike") }, losN, "Realistic"), 8 * 60);
        }

        // ---- selftest (null = ok) ----
        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }
        private static void Eq(string n, double a, double b) { if (Math.Abs(a - b) > 1e-6 && _err == null) _err = n + ": " + a + " != " + b; }

        private static SarRaft Raft(int id, double lat, double lon, int people, string type = "all_life_raft_single")
        {
            return new SarRaft { Id = id, Lat = lat, Lon = lon, People = people, Type = type };
        }
        private static SarRescuer Air(int uid, int seats, double lat = 0, double lon = 0)
        {
            return new SarRescuer { Uid = uid, Cls = "air", Seats = seats, Lat = lat, Lon = lon, RangeNm = 200, SpeedKn = 100 };
        }
        private static SarRescuer Ship(int uid) { return new SarRescuer { Uid = uid, Cls = "water", Seats = int.MaxValue, Lat = 36, Lon = 20, RangeNm = 1000, SpeedKn = 15 }; }
        private static LoadoutOption Lo(string n, int cap, bool avail = true)
        {
            return new LoadoutOption { Name = n, Display = n, Capacity = cap, Available = avail };
        }
        private static SarHelo Helo(int uid, string where, int cap, int deck = 1, string type = "SH-2F", string lo = "ASW")
        {
            return new SarHelo { Uid = uid, DeckUid = deck, Where = where, Capacity = cap, Type = type, Loadout = lo, FuelFrac = 1, EnduranceS = 7200 };
        }
        private static int Count(List<SarAction> a, string kind) { int n = 0; foreach (var x in a) if (x.Kind == kind) n++; return n; }

        public static string SelfTest()
        {
            _err = null;
            var p = new SarParams();
            CandidateTests(p); RegistryTests(p); PickTests(p); LoadoutTests(p); LeaveTests(p); ReliefTests(p); RearmTests(p); T343Tests(p);
            return _err;
        }

        private static void CandidateTests(SarParams p)
        {
            string why;
            True("cand ok", Candidate(Raft(1, 0, 0, 3), out why));
            True("cand destroyed", !Candidate(new SarRaft { Id = 1, People = 3, Destroyed = true }, out why) && why == "destroyed");
            True("cand empty", !Candidate(Raft(1, 0, 0, 0), out why) && why == "empty");
            True("cand land", !Candidate(new SarRaft { Id = 1, People = 3, OnLand = true }, out why) && why == "land");
            Eq("prio 0", PriorityClass("all_life_raft_single", p.PriorityRafts), 0);
            Eq("prio 1", PriorityClass("all_pod_president", p.PriorityRafts), 1);
            Eq("prio other", PriorityClass("big", p.PriorityRafts), 2);
            var r = Raft(1, 36.0, 20.0, 3);
            var far9 = new List<SarThreat> { new SarThreat { Id = 1, Lat = 36.0 + 9.0 / 60, Lon = 20.0, AirDefense = true } };
            var far11 = new List<SarThreat> { new SarThreat { Id = 1, Lat = 36.0 + 11.0 / 60, Lon = 20.0, AirDefense = true } };
            var noAd = new List<SarThreat> { new SarThreat { Id = 1, Lat = 36.0 + 2.0 / 60, Lon = 20.0, AirDefense = false } };
            True("threat 9nm", UnderThreat(r, far9, p.ThreatStandoffNm));
            True("threat 11nm", !UnderThreat(r, far11, p.ThreatStandoffNm));
            True("threat no AD", !UnderThreat(r, noAd, p.ThreatStandoffNm));
            True("threat gone", !UnderThreat(r, new List<SarThreat>(), p.ThreatStandoffNm) && !UnderThreat(r, null, p.ThreatStandoffNm));
            var rect = new SarArea { Kind = "rect", Rect = new[] { 35.0, 19.0, 37.0, 21.0 } };
            True("rect in", rect.Contains(new GeoPt(36, 20)) && !rect.Contains(new GeoPt(38, 20)));
            var circ = new SarArea { Kind = "circle", Center = new GeoPt(36, 20), RadiusNm = 5 };
            True("circle", circ.Contains(new GeoPt(36 + 4.0 / 60, 20)) && !circ.Contains(new GeoPt(36 + 6.0 / 60, 20)));
            True("all", new SarArea().Contains(new GeoPt(1, 1)));
        }

        private static void RegistryTests(SarParams p)
        {
            var reg = new SarRegistry();
            var big = Raft(1, 36, 20, 20);
            True("claim a", reg.TryClaim(big, Air(10, 10), 0, 300) == null);
            True("not covered 10/20", !reg.Covered(big));
            True("claim b", reg.TryClaim(big, Air(11, 10), 0, 300) == null);
            True("covered 20/20", reg.Covered(big));
            True("claim c covered", reg.TryClaim(big, Air(12, 10), 0, 300) == "covered");
            True("busy", reg.TryClaim(Raft(2, 36, 20, 5), Air(10, 10), 0, 300) == "busy");
            Eq("seats", reg.SeatsOn(1), 20);
            True("release", reg.Release(11, "full") == 1 && !reg.Covered(big) && reg.ClaimOf(11) == null);
            foreach (var reason in new[] { "full", "leaving", "lost", "player_took", "released", "stopped" })
            {
                var rr = new SarRegistry();
                rr.TryClaim(big, Air(20, 5), 0, 100);
                True("release " + reason, rr.Release(20, reason) == 1 && rr.Claims.Count == 0);
            }
            True("release none", reg.Release(999, "x") == 0);

            var one = Raft(3, 36, 20, 1);
            reg = new SarRegistry();
            True("one a", reg.TryClaim(one, Air(1, 8), 0, 100) == null);
            True("one b covered", reg.TryClaim(one, Air(2, 8), 0, 100) == "covered");
            reg = new SarRegistry();
            True("ship covers", reg.TryClaim(big, Ship(5), 0, 100) == null && reg.Covered(big));
            True("ship seats no overflow", reg.TryClaim(big, Ship(6), 0, 100) == "covered" && reg.SeatsOn(1) == int.MaxValue);

            // sweep
            reg = new SarRegistry();
            var rafts = new List<SarRaft> { Raft(1, 36, 20, 3), Raft(2, 36, 20.5, 3), Raft(3, 36, 21, 3), Raft(4, 36, 21.5, 3) };
            reg.TryClaim(rafts[0], Air(1, 8), 0, 100); reg.TryClaim(rafts[1], Air(2, 8), 0, 100);
            reg.TryClaim(rafts[2], Air(3, 8), 0, 100); reg.TryClaim(rafts[3], Air(4, 8), 0, 100);
            rafts[0].People = 0;
            rafts[1].Destroyed = true;
            var threats = new List<SarThreat> { new SarThreat { Id = 1, Lat = 36, Lon = 21 + 5.0 / 60, AirDefense = true } };
            Func<int, GeoPt?> pos = uid => new GeoPt(35, 20);
            var sw = reg.Sweep(rafts, threats, pos, 10, p);
            var why = new Dictionary<int, string>();
            foreach (var kv in sw) why[kv.Key.Rescuer] = kv.Value;
            True("sweep picked", why.ContainsKey(1) && why[1] == "picked");
            True("sweep destroyed", why.ContainsKey(2) && why[2] == "picked");
            True("sweep threat", why.ContainsKey(3) && why[3] == "threat");
            True("sweep keeps 4", !why.ContainsKey(4) && reg.ClaimOf(4) != null);
            // stalled: eta 100 -> limit max(200, 220)=220 s without progress
            True("not stalled 200", reg.Sweep(rafts, null, pos, 200, p).Count == 0);
            var st = reg.Sweep(rafts, null, pos, 240, p);
            True("stalled", st.Count == 1 && st[0].Value == "stalled");
            // progress resets the clock
            reg = new SarRegistry();
            reg.TryClaim(rafts[3], Air(4, 8), 0, 100);
            double lat = 35.0;
            Func<int, GeoPt?> moving = uid => new GeoPt(lat, 21.5);
            reg.Sweep(rafts, null, moving, 1, p);
            lat = 35.5; reg.Sweep(rafts, null, moving, 200, p);
            True("progress keeps", reg.Sweep(rafts, null, moving, 400, p).Count == 0);
            True("stalled later", reg.Sweep(rafts, null, moving, 430, p).Count == 1);

            // misses
            reg = new SarRegistry();
            var m = Raft(7, 36, 20, 3);
            reg.TryClaim(m, Air(1, 8), 0, 100);
            True("miss 1", !reg.NoteMiss(1, 10, p));
            True("miss 2", !reg.NoteMiss(1, 20, p));
            True("miss 3", reg.NoteMiss(1, 30, p));
            True("claim dropped", reg.ClaimOf(1) == null);
            True("unreach air", reg.Unreachable(7, "air", 100));
            True("water ok", !reg.Unreachable(7, "water", 100));
            True("try claim air unreachable", reg.TryClaim(m, Air(2, 8), 100, 100) == "unreachable");
            True("water claim ok", reg.TryClaim(m, Ship(9), 100, 100) == null);
            True("unreach expires", !reg.Unreachable(7, "air", 631));
            True("miss no claim", !reg.NoteMiss(55, 0, p));
            reg.Clear(); True("clear", reg.Claims.Count == 0);
        }

        private static void PickTests(SarParams p)
        {
            var reg = new SarRegistry();
            var single = Raft(1, 36.15, 20, 1, "all_life_raft_single");
            var near = Raft(2, 36.1, 20, 20, "other");
            var pres = Raft(3, 36.2, 20, 1, "all_pod_president");
            var rafts = new List<SarRaft> { near, pres, single };
            Func<SarRaft, double> travel = r => Math.Abs(r.Lat - 36.0) * 3600;
            string why;
            var s = Air(10, 8, 36.0, 20);
            True("pick single first", PickRaft(s, rafts, reg, null, null, travel, 0, p, out why) == single);
            single.People = 0;
            True("pick president", PickRaft(s, rafts, reg, null, null, travel, 0, p, out why) == pres);
            pres.Destroyed = true;
            True("pick near", PickRaft(s, rafts, reg, null, null, travel, 0, p, out why) == near);
            // 0.28.1: a priority raft far away does not beat a near raft (class worth PriorityBonusS of travel)
            var farSingle = Raft(6, 38.0, 20, 1, "all_life_raft_single"); var near8 = Raft(7, 36.1, 20, 8, "all_life_raft");
            True("near beats far priority", PickRaft(s, new List<SarRaft> { farSingle, near8 }, reg, null, null, travel, 0, p, out why) == near8);
            // 0.28.1: a raft another rescuer already goes to is taken only when it is clearly nearer
            var shared = Raft(8, 36.1, 20, 20, "other"); var free = Raft(9, 36.2, 20, 20, "other");
            var regS = new SarRegistry();
            True("shared claim", regS.TryClaim(shared, Air(20, 2), 0, 100) == null);
            True("free over shared", PickRaft(s, new List<SarRaft> { shared, free }, regS, null, null, travel, 0, p, out why) == free);
            var freeFar = Raft(10, 36.5, 20, 20, "other");
            True("shared when the free one is far", PickRaft(s, new List<SarRaft> { shared, freeFar }, regS, null, null, travel, 0, p, out why) == shared);
            // within class: smaller travel
            var a = Raft(4, 36.4, 20, 1); var b = Raft(5, 36.3, 20, 1);
            True("pick min travel", PickRaft(s, new List<SarRaft> { a, b }, reg, null, null, travel, 0, p, out why) == b);
            // skips with reasons
            var area = new SarArea { Kind = "circle", Center = new GeoPt(36.3, 20), RadiusNm = 1 };
            True("only in area", PickRaft(s, new List<SarRaft> { a, b }, reg, area, null, travel, 0, p, out why) == b);
            True("out of area", PickRaft(s, new List<SarRaft> { a }, reg, area, null, travel, 0, p, out why) == null && why == "no_rafts");
            reg.MarkUnreachable(4, "air", 0, 600);
            True("unreachable", PickRaft(s, new List<SarRaft> { a }, reg, null, null, travel, 10, p, out why) == null && why == "unreachable");
            True("unreachable for water ok", PickRaft(Ship(1), new List<SarRaft> { a }, reg, null, null, travel, 10, p, out why) == a);
            var th = new List<SarThreat> { new SarThreat { Id = 1, Lat = 36.4, Lon = 20.05, AirDefense = true } };
            True("threat", PickRaft(s, new List<SarRaft> { b }, new SarRegistry(), null, th, travel, 0, p, out why) == null && why == "threat");
            var far = Air(11, 8, 30.0, 20); far.RangeNm = 50;
            True("range", PickRaft(far, new List<SarRaft> { b }, new SarRegistry(), null, null, travel, 0, p, out why) == null && why == "range");
            var reg2 = new SarRegistry(); reg2.TryClaim(b, Air(77, 8), 0, 10);
            True("covered skipped", PickRaft(s, new List<SarRaft> { b }, reg2, null, null, travel, 0, p, out why) == null && why == "no_rafts");
            Eq("demand", Demand(new List<SarRaft> { a, b, near }, reg2, null, null, "air", 10, p), 2);   // b covered
            reg2.MarkUnreachable(4, "air", 0, 600);
            Eq("demand unreachable", Demand(new List<SarRaft> { a, b, near }, reg2, null, null, "air", 10, p), 1);
            Eq("demand water", Demand(new List<SarRaft> { a, b, near }, reg2, null, null, "water", 10, p), 2);
        }

        private static void LoadoutTests(SarParams p)
        {
            var sh2f = new List<LoadoutOption> { Lo("ASW", 2), Lo("ASWBomb", 8), Lo("Transport", 8), Lo("Empty", 8), Lo("Ferry", 8) };
            True("sh2f empty", sh2f[PickSarLoadout(sh2f, p, null)].Name == "Empty");
            var sh3 = new List<LoadoutOption> { Lo("Default", 26), Lo("Transport", 26), Lo("Empty", 26), Lo("Ferry", 26) };
            True("sh3h empty", sh3[PickSarLoadout(sh3, p, null)].Name == "Empty");
            True("none", PickSarLoadout(new List<LoadoutOption> { Lo("A", 0), Lo("B", 0) }, p, null) == -1);
            True("noammo", sh2f[PickSarLoadout(sh2f, p, new HashSet<string> { "Empty" })].Name == "Ferry");
            var un = new List<LoadoutOption> { Lo("Empty", 8, false), Lo("ASW", 2) };
            True("unavailable skipped", un[PickSarLoadout(un, p, null)].Name == "ASW");
        }

        private static void LeaveTests(SarParams p)
        {
            var d = new SarDemand { UncoveredRafts = 0, NoRaftsSince = 0 };
            var h = Helo(1, "air", 8); h.Onboard = 8;
            True("full", LeaveReason(h, new SarDemand { UncoveredRafts = 3 }, 0, p) == "full");
            h.Onboard = 3;
            True("no leave", LeaveReason(h, new SarDemand { UncoveredRafts = 3 }, 0, p) == null);
            True("no_rafts grace", LeaveReason(h, d, 30, p) == null);
            True("no_rafts", LeaveReason(h, d, 61, p) == "no_rafts");
            h.Onboard = 0;
            True("no_rafts empty", LeaveReason(h, d, 61, p) == "no_rafts");
            d.Loiter = true;
            True("loiter hover", LeaveReason(h, d, 600, p) == null);
            h.FuelFrac = 0.05;   // 0.05*7200 = 360 s of fuel, 400 s needed to go home
            h.TransitS = 400;
            True("fuel", LeaveReason(h, d, 600, p) == "fuel");
            h.FuelFrac = 1; h.TransitS = 0; h.HasTarget = true; d.Loiter = false;
            True("has target stays", LeaveReason(h, d, 600, p) == null);
        }

        private static void ReliefTests(SarParams p)
        {
            var los = new List<LoadoutOption> { Lo("ASW", 2), Lo("Empty", 8), Lo("Ferry", 8) };
            string mode = "Realistic";
            // demand 0/1/3 with 1/2/3 helos ready (cap 8)
            foreach (int dem in new[] { 0, 1, 3 })
                foreach (int n in new[] { 1, 2, 3 })
                {
                    var m = new List<SarHelo>();
                    for (int i = 0; i < n; i++) m.Add(Helo(10 + i, "deck_ready", 8, 1, "SH-3H", "Empty"));
                    var a = PlanSarRelief(m, new SarDemand { UncoveredRafts = dem, People = dem, SeatsAir = 100 }, los, mode, 0, p);
                    True("launch <= min " + dem + "/" + n, Count(a, "launch") == Math.Min(dem, n));
                }
            // standby: demand 0 -> one order of the best loadout
            var none = new List<SarHelo> { Helo(1, "air", 8) };
            none[0].HasTarget = false;
            var r0 = PlanSarRelief(none, new SarDemand { SeatsAir = 8 }, los, mode, 0, p);
            True("standby order", Count(r0, "order") == 1 && r0.Find(x => x.Kind == "order").Loadout == "Empty");
            // ready SH-2F with ASW (2 seats), no SAR helo airborne -> launch as is + order 8
            var asw = new List<SarHelo> { Helo(1, "deck_ready", 2) };
            var r1 = PlanSarRelief(asw, new SarDemand { UncoveredRafts = 1, People = 1, SeatsAir = 2, SeatsReserve = 8 }, los, mode, 0, p);
            True("asw launch as is", Count(r1, "launch") == 1 && Count(r1, "order") == 1 && r1.Find(x => x.Kind == "order").Loadout == "Empty");
            // SAR helo airborne with target, 5 people waiting, the ready ASW one cannot be re-armed -> it stays (hold), order 8
            var air = Helo(2, "air", 8); air.HasTarget = true;
            var asw2 = new List<SarHelo> { Helo(1, "deck_ready", 2), air };
            var r2 = PlanSarRelief(asw2, new SarDemand { UncoveredRafts = 1, People = 5, SeatsAir = 0, SeatsReserve = 8 }, los, mode, 0, p);
            True("asw stays", Count(r2, "launch") == 0 && Count(r2, "hold") == 1 && r2.Find(x => x.Kind == "hold").Why == "cannot_rearm");
            // live finding 38: ... but one waiting person fits its 2 seats -> it goes as is even with a SAR helo airborne
            var r2b = PlanSarRelief(asw2, new SarDemand { UncoveredRafts = 1, People = 1, SeatsAir = 0, SeatsReserve = 8 }, los, mode, 0, p);
            True("asw enough seats goes", Count(r2b, "launch") == 1 && r2b.Find(x => x.Kind == "launch").Why == "enough_seats");
            True("order 8", Count(r2, "order") >= 1 && r2.Find(x => x.Kind == "order").Loadout == "Empty");
            // abort surplus own orders; foreign untouched; one kept as reserve
            var ms = new List<SarHelo>();
            for (int i = 0; i < 3; i++) { var h = Helo(20 + i, "deck_service", 8); h.PendingKey = "k" + i; h.Ours = true; ms.Add(h); }
            var foreign = Helo(30, "deck_service", 8); foreign.PendingKey = "kf"; foreign.Ours = false; ms.Add(foreign);
            var r3 = PlanSarRelief(ms, new SarDemand { SeatsAir = 8 }, los, mode, 0, p);
            True("abort 3 of 4 pipeline", Count(r3, "abort") == 3 - 0 - 0 && !r3.Exists(x => x.Kind == "abort" && x.Key == "kf"));
            // timings by mode
            var landed = new List<SarHelo> { Helo(1, "landed", 8, 1, "SH-3H", "Empty") };
            los[1].ReadyUpMin = 15; los[1].CoolDownMin = 20;
            Eq("next 1", NextLaunchS(landed, los, "Realistic"), 1200 + 900);
            Eq("next 5", NextLaunchS(landed, los, "Casual"), (1200 + 900) / 5.0);
            Eq("next 10", NextLaunchS(landed, los, "Arcade"), (1200 + 900) / 10.0);
            // T-226: a hangar row waits only for the preparation; a cool: row for the cool-down left plus the preparation; the anchor row keeps the old time
            var hang = Helo(2, "landed", 8, 1, "SH-3H", "Empty"); hang.PrepS = 900;
            Eq("next hangar", NextLaunchS(new List<SarHelo> { hang }, los, "Realistic"), 900);
            var cool = Helo(3, "landed", 8, 1, "SH-3H", "Empty"); cool.PrepS = 900; cool.CoolLeftS = 300;
            Eq("next cool", NextLaunchS(new List<SarHelo> { cool }, los, "Realistic"), 1200);
            Eq("next anchor", NextLaunchS(new List<SarHelo> { Helo(4, "landed", 8, 1, "SH-3H", "Empty") }, los, "Realistic"), 1200 + 900);
            var r4 = PlanSarRelief(landed, new SarDemand { SeatsAir = 8 }, los, "Casual", 0, p);
            var ord = r4.Find(x => x.Kind == "order");
            True("order eta m5", ord != null && Math.Abs(ord.EtaS - 180) < 1e-6);
            Eq("next none", NextLaunchS(new List<SarHelo>(), los, mode), -1);
            Eq("next ready", NextLaunchS(new List<SarHelo> { Helo(1, "deck_ready", 8) }, los, mode), 0);
            // low capacity
            var r5 = PlanSarRelief(new List<SarHelo> { Helo(1, "air", 8) }, new SarDemand { UncoveredRafts = 4, People = 30, SeatsAir = 8, SeatsReserve = 8 }, los, mode, 0, p);
            True("low_capacity", r5.Exists(x => x.Kind == "warn" && x.Code == "low_capacity"));
            var r6 = PlanSarRelief(new List<SarHelo> { Helo(1, "air", 8) }, new SarDemand { People = 10, SeatsAir = 8, SeatsReserve = 8 }, los, mode, 0, p);
            True("no low_capacity", !r6.Exists(x => x.Kind == "warn"));
            // no capacity at all
            var r7 = PlanSarRelief(new List<SarHelo> { Helo(1, "deck_ready", 0) }, new SarDemand { UncoveredRafts = 1, People = 2 }, new List<LoadoutOption> { Lo("A", 0) }, mode, 0, p);
            True("no_capacity warn", r7.Exists(x => x.Code == "no_capacity") && Count(r7, "launch") == 0);
            // T-147: a helo with seats in the air but no deck to prepare a reserve -> no_reserve_deck, not no_capacity
            var r8 = PlanSarRelief(new List<SarHelo> { Helo(1, "air", 8, 0) }, new SarDemand { UncoveredRafts = 3, People = 6, SeatsAir = 8 }, new List<LoadoutOption>(), mode, 0, p);
            True("no_reserve_deck warn", r8.Exists(x => x.Code == "no_reserve_deck") && !r8.Exists(x => x.Code == "no_capacity"));
            var r9 = PlanSarRelief(new List<SarHelo> { Helo(1, "air", 0, 0) }, new SarDemand { UncoveredRafts = 3, People = 6 }, new List<LoadoutOption>(), mode, 0, p);
            True("no_capacity without seats", r9.Exists(x => x.Code == "no_capacity") && !r9.Exists(x => x.Code == "no_reserve_deck"));
        }
    }

    /// <summary>
    /// 0.27.10: raw counts of one read of the game's raft list (ObjectsManager._listOfAllRafts) by SarGame.Rafts(): what was in the list, what the
    /// reader dropped and why, and how many of the read rafts are rescue candidates (SarMath.Candidate). Pure; the dev state shows it as "raft_scan",
    /// the journal gets a [Sar] raft scan line when the counts change (SarRaftScanLog). A reader error no longer empties the whole list: it is counted per raft.
    /// </summary>
    internal sealed class SarRaftScan
    {
        public int List = -1;                         // -1 = the list itself could not be read (no mission, member missing)
        public int Dead, NoPos, Errors, Read, Candidates, People, Empty, OnLand, Destroyed;
        public string FirstError;                     // "Type: message" of the first exception of this scan
        public double AtS = -1;                       // mission time of the scan

        public void Begin(int listCount, double now)
        {
            List = listCount; AtS = now;
            Dead = NoPos = Errors = Read = Candidates = People = Empty = OnLand = Destroyed = 0; FirstError = null;
        }

        public void Error(Exception e)
        {
            Errors++;
            if (FirstError == null && e != null) FirstError = e.GetType().Name + ": " + e.Message;
        }

        public void Count(SarRaft r)
        {
            Read++;
            string why;
            if (SarMath.Candidate(r, out why)) { Candidates++; People += r.People; }
            else if (why == "destroyed") Destroyed++;
            else if (why == "empty") Empty++;
            else OnLand++;
        }

        /// <summary>The counts without the time: equal strings = nothing changed (used by the log limiter).</summary>
        public string Summary()
        {
            return "list=" + List + " read=" + Read + " candidates=" + Candidates + " people=" + People + " dead=" + Dead + " no_pos=" + NoPos
                 + " errors=" + Errors + " empty=" + Empty + " on_land=" + OnLand + " destroyed=" + Destroyed;
        }

        public string ToJson()
        {
            return "{\"list\":" + List + ",\"read\":" + Read + ",\"candidates\":" + Candidates + ",\"people\":" + People + ",\"dead\":" + Dead
                 + ",\"no_pos\":" + NoPos + ",\"errors\":" + Errors + ",\"empty\":" + Empty + ",\"on_land\":" + OnLand + ",\"destroyed\":" + Destroyed
                 + ",\"first_error\":" + (FirstError == null ? "null" : Json.Str(FirstError)) + ",\"at_s\":" + Json.Num(AtS) + "}";
        }

        public SarRaftScan Copy() { return (SarRaftScan)MemberwiseClone(); }

        public static string SelfTest()
        {
            var s = new SarRaftScan();
            s.Begin(4, 10);
            s.Count(new SarRaft { Id = 1, People = 8 });
            s.Count(new SarRaft { Id = 2, People = 0 });
            s.Count(new SarRaft { Id = 3, People = 1, OnLand = true });
            s.Error(new InvalidCastException("x"));
            s.Error(new ArgumentException("y"));
            if (s.Read != 3 || s.Candidates != 1 || s.People != 8 || s.Empty != 1 || s.OnLand != 1 || s.Errors != 2) return "counts " + s.Summary();
            if (s.FirstError != "InvalidCastException: x") return "first error " + s.FirstError;
            if (s.Summary() != "list=4 read=3 candidates=1 people=8 dead=0 no_pos=0 errors=2 empty=1 on_land=1 destroyed=0") return "summary " + s.Summary();
            var j = s.ToJson();
            if (!j.StartsWith("{\"list\":4,\"read\":3,\"candidates\":1,\"people\":8,", StringComparison.Ordinal) || !j.Contains("\"first_error\":\"InvalidCastException: x\"") || !j.EndsWith(",\"at_s\":10}", StringComparison.Ordinal)) return "json " + j;
            s.Begin(0, 11);
            if (s.Errors != 0 || s.FirstError != null || s.Read != 0 || s.List != 0) return "begin resets";
            if (!new SarRaftScan().ToJson().StartsWith("{\"list\":-1,", StringComparison.Ordinal)) return "unread list";

            var lim = new SarRaftScanLog(60);
            var a = new SarRaftScan(); a.Begin(2, 0); a.Count(new SarRaft { People = 3 });
            if (lim.Next(a, 0) == null) return "log: first scan";
            if (lim.Next(a, 5) != null) return "log: same counts again";
            var b = new SarRaftScan(); b.Begin(3, 10); b.Count(new SarRaft { People = 3 });
            if (lim.Next(b, 10) != null) return "log: change inside the gap";
            if (lim.Next(b, 61) == null) return "log: change after the gap";
            var c = new SarRaftScan(); c.Begin(3, 62); c.Error(new InvalidOperationException("boom"));
            var lc = lim.Next(c, 62);
            if (lc == null || !lc.Contains("InvalidOperationException: boom")) return "log: first error at once " + lc;
            if (lim.Next(c, 200) != null) return "log: same error twice";
            lim.Reset();
            if (lim.Next(a, 0) == null) return "log: after reset";
            // 0.30.1: a new candidate raft is written after GrowthGapS, not after the full gap
            var g1 = new SarRaftScan(); g1.Begin(2, 3); g1.Count(new SarRaft { People = 3 }); g1.Count(new SarRaft { People = 1 });
            if (lim.Next(g1, 3) != null) return "log: growth inside GrowthGapS";
            if (lim.Next(g1, 6) == null) return "log: growth after GrowthGapS";
            var g2 = new SarRaftScan(); g2.Begin(1, 20); g2.Count(new SarRaft { People = 3 });
            if (lim.Next(g2, 20) != null) return "log: a drop waits for the full gap";
            return null;
        }
    }

    /// <summary>Rate limit of the [Sar] raft scan journal line: a line when the counts changed, at most one per gap (mission seconds);
    /// the first error of a mission is written at once (whatever the gap). 0.30.1: more rescue candidates than in the last line (a new raft)
    /// are written after GrowthGapS already (the 60 s gap made the journal look as if new rafts were seen minutes late). Reset at mission change.</summary>
    internal sealed class SarRaftScanLog
    {
        /// <summary>Mission seconds between two lines when the number of rescue candidates grew.</summary>
        public const double GrowthGapS = 5;
        private readonly double _gapS;
        private string _last;
        private double _lastAt = double.NegativeInfinity;
        private int _lastCandidates = -1;
        private bool _errorSeen;

        public SarRaftScanLog(double gapS) { _gapS = gapS; }

        public void Reset() { _last = null; _lastAt = double.NegativeInfinity; _lastCandidates = -1; _errorSeen = false; }

        /// <summary>The line to write now, or null.</summary>
        public string Next(SarRaftScan s, double now)
        {
            if (s == null) return null;
            string sum = s.Summary();
            bool firstError = s.FirstError != null && !_errorSeen;
            double gap = _last != null && s.Candidates > _lastCandidates ? GrowthGapS : _gapS;
            if (!firstError && (sum == _last || (_last != null && now - _lastAt < gap && now >= _lastAt))) return null;
            if (s.FirstError != null) _errorSeen = true;
            _last = sum; _lastAt = now; _lastCandidates = s.Candidates;
            return "raft scan " + sum + (s.FirstError != null ? " first_error=" + s.FirstError : "");
        }
    }

    /// <summary>
    /// 0.30.1: one read of the game's raft list per frame. Every SAR user (the controller tick, each SAR group, each helicopter, the view,
    /// the ship rows, the menu caption) asks for the rafts; before, every call walked the game list again (5-10 reads per game second with a few
    /// helicopters). A call with the same mission time and less than MaxRealAgeS of real time after the read gets a copy of the list read then.
    /// The rafts themselves are shared (read-only data). Pure.
    /// </summary>
    internal sealed class SarRaftCache
    {
        public const double MaxRealAgeS = 0.5;
        private List<SarRaft> _list;
        private double _at, _realAt;

        /// <summary>Reads of the game list / calls answered from the cache (diag, stands).</summary>
        public int Reads { get; private set; }
        public int Hits { get; private set; }

        public IList<SarRaft> Get(double now, double realT, Func<List<SarRaft>> read)
        {
            if (_list != null && now == _at && realT >= _realAt && realT - _realAt < MaxRealAgeS) { Hits++; return new List<SarRaft>(_list); }
            _list = (read == null ? null : read()) ?? new List<SarRaft>();
            _at = now; _realAt = realT; Reads++;
            return new List<SarRaft>(_list);
        }

        /// <summary>The next call reads the game (mission change, a raft taken by the mod's own code).</summary>
        public void Reset() { _list = null; }

        public static string SelfTest()
        {
            var c = new SarRaftCache();
            int reads = 0;
            Func<List<SarRaft>> rd = () => { reads++; return new List<SarRaft> { new SarRaft { Id = reads, People = 2 } }; };
            var a = c.Get(10, 100, rd);
            var b = c.Get(10, 100.2, rd);
            if (reads != 1 || c.Hits != 1 || b.Count != 1 || b[0].Id != 1) return "same frame must hit: reads=" + reads;
            b.Clear();
            if (c.Get(10, 100.3, rd).Count != 1) return "a caller's change must not reach the cache";
            if (c.Get(11, 100.3, rd)[0].Id != 2) return "new mission time must read";
            if (c.Get(11, 100.9, rd)[0].Id != 3) return "paused game: re-read after MaxRealAgeS";
            if (c.Get(11, 50, rd)[0].Id != 4) return "real clock going back must read";
            c.Reset();
            if (c.Get(11, 50, rd)[0].Id != 5) return "reset must read";
            if (c.Get(12, 51, () => null).Count != 0) return "null read = empty list";
            if (c.Reads != 6) return "reads counter " + c.Reads;
            if (a.Count != 1) return "first result changed";
            return null;
        }
    }
}
