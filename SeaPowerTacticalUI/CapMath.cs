// T-112 §3.2 CapMath (pure, selftest, no Unity/reflection). Owner: stream A.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal sealed class CapParams
    {
        public double MissilePct = 0.20, FuelMarginS = 300, FuelFloorPct = 0, CombatHostileNm = 10, PursuitNm = 25,
                      ThreatNm = 80, ThreatClearMarginNm = 10, ThreatClearS = 60,          // ADR-0041 D1
                      LaunchDelayS = 180, PatrolAltFt = 0, LaneEndMaxNm = 5, OrbitBelowNm = 4;
        /// <summary>T-330 (ADR-0128): ring safety margin with a live fuel fraction: spare path (nm) over the way home (ReserveNm = 2 x ring).</summary>
        public double RingMarginNm = 5;
        /// <summary>T-235 (ADR-0097): the cover radius of a group = CoverAamFrac x the shortest of the longest-AAM ranges of its aircraft; CoverFallbackNm while none is known.</summary>
        public double CoverAamFrac = 0.5, CoverFallbackNm = 10;
        /// <summary>T-244 (ADR-0102): a small zone is one circle for the whole group. Radius >= the turn radius at CircleSpeedKn / CircleBankDeg (no game spec gives it: a constant per type);
        /// the altitude of bird i = base + i x CircleAltStepFt (base = PatrolAltFt, else CircleBaseAltFt); CircleTolNm = how far outside the circle a bird still counts "on station".</summary>
        public double CircleAltStepFt = 1000, CircleSpeedKn = 300, CircleBankDeg = 30, CircleBaseAltFt = 20000, CircleTolNm = 1.0;
        public int CirclePoints = 8;
        /// <summary>T-250: phase keeping on the shared circle (ADR-0102 follow-up). Constants, not config keys.</summary>
        public double CirclePhaseTolDeg = 20, CirclePhaseGain = 0.8, CircleStretchMax = 0.35, CircleCutMax = 0.15, CirclePhaseLogS = 120;
        /// <summary>T-257: step of a bird on station outside combat: "cruise" = the patrol step, "keep" = leave what the game / the player set.</summary>
        public string StationSpeed = "cruise";
        public double StationThrottleCheckS = 30;   // game s between checks per bird
        public double StationKeepS = 5;             // how long after the mod's own setTelegraph Patrol must re-apply that step (stale getTelegraph guard)
        public double StationLogGapS = 300;         // the same 'station speed' line is not repeated sooner (flip-flop guard)
        public int DefaultOnStation = 2;
        public double MaxZoneNm = 200;   // T-178 I4 (ADR-0077): the longest side of a zone the handles allow
        /// <summary>Correction 2026-10-22 (ADR-0040 D5): the game's ring must stay under the fuel threshold this long before "fuel" (an afterburner burst
        /// or a low pass collapses it for a moment). Not a config key.</summary>
        public double FuelHoldS = 60;
        /// <summary>T-337 E (live r34): the hold for by=margin when the live fuel fraction confirms it (FuelOverridesPostpone, <= 10 %): the game's own bingo
        /// came 20-50 game s after the 5 nm margin, so a 60 s hold never let the mod leave first. Not a config key.</summary>
        public double FuelMarginHoldS = 5;
        public bool NeedLongRange, Debug;
        public string Roe = "tight", LoadoutTieBreak = "AirToAirLongRange,AirToAir,AirToAirIntercept",
                      LaunchMissionType = "none", RestoreOnLoad = "auto";
        /// <summary>Total endurance assumed for a bird without any fuel data (controller fallback, T-112 D). Not persisted.</summary>
        public double NoFuelDataOnStationS = 5400;
        /// <summary>ADR-0040 D2: fast return to the deck after missiles are gone; k of the top speed step when the game gives no burn data.</summary>
        public bool DashOnMissileRtb = true;
        public double DashBurnFactor = 2.0;
        // T-175 / ADR-0075: air-only CAP (postponement only for air targets, no fire on the way home, surface targets dropped) and the alert pair
        public bool AirOnly = true, RtbRoeHold = true;
        public int AlertCount = 2;
        public double AlertNm = 80, AlertPursuitNm = 60, ReorderGainS = 60;
        // T-179 / ADR-0079: the "Alert" group kind (per-raid count, readiness level, target filter, dash speed, what to do after the raid, fill from deck, patrol side)
        public int AlertPerRaid = 2;                         // 0 = all ready
        public string AlertLevel = "deck";                   // deck | hangar
        public string AlertTargets = "air";                  // csv of air,helo,unknown
        public string AlertSpeed = "max";                    // max | cruise
        public string AlertAfter = "patrol";                 // alert | patrol
        public bool AlertFill = false, AcceptAlert = true;
        public double AlertHandoverMinS = 600;
        public double UnknownMinKn = 350;                    // copy of [AirGuard] UnknownMinKn (set by the feature); not a [Cap] key
        public double StrikeMixedAamShare = 1.0;             // T-325: a mixed loadout counts as air when AAM / (AAM + strike) >= this (1.0 = any strike item makes it strike); not a [Cap] key
        /// <summary>T-333 (ADR-0129): zone intrusion intercept of a patrol group ([Cap] Intercept*; per-group copy in CapInterceptSpec) and the friendly-fire guard (FriendlyFireGuard, mod-wide).</summary>
        public bool Intercept = true, FriendlyFireGuard = true;
        public double InterceptMarginNm = 10, InterceptLostS = 60;
        public int InterceptPerTarget = 2, InterceptKeepOnStation = 0;
        public string InterceptTargets = "air,helo", InterceptSpeed = "max";
        internal CapParams Clone() { return (CapParams)MemberwiseClone(); }
    }

    internal sealed class CapAmmo { public string Name, Type, TargetType, SecondaryTargetType; public double MaxLaunchRangeNm, FuelKg; public string Label; /* T-325: AmmunitionParameters._displayedName, null = show the key */ }

    internal sealed class CapZone
    {
        public double[] Rect;                 // lat0, lon0, lat1, lon1 (min/max)
        public string Anchor = "geo";         // geo | unit
        public int AnchorUid; public double OffLat, OffLon;
        public bool AnchorLost;
        // T-166: the polar form the order was given in (bearing from the origin, distance, size E-W, width N-S); the window shows it again. Not persisted.
        public bool HasPolar; public double PolarBearing, PolarDistNm, PolarSizeNm, PolarWNm;
    }

    internal sealed class CapLane { public int Slot; public List<GeoPt> Track; public bool Orbit; public GeoPt Center; public int Legs; public double LegSpacingNm, CoverNm; public bool Circle; public double RadiusNm; public int AltFt; public double StartDeg; }
    /// <summary>T-250: phase of a bird on the shared circle, its target, error (&gt; 0 = ahead) and the radius of its next lap.</summary>
    internal sealed class CapPhaseFix { public int Uid; public double PhaseDeg, TargetDeg, ErrDeg, RadiusNm; public bool Fix; }   // T-235: Legs/LegSpacingNm/CoverNm for the log, tests and ui_state

    internal sealed class CapContact { public int Id; public double Lat, Lon; public bool Hostile, Missile; public double CourseDeg, SpeedKn; public bool Unknown, Helo; }   // T-175/T-179 (Helo: a helicopter): course, speed and unknown posture from the air threat service data

    internal sealed class CapBird
    {
        public int Uid, DeckUid, Slot = -1;
        public int HangarK, HangarN;    // T-186 (ADR-0082): k of a hangar slot row (1..MaxSlots) and Numbers of the pair; 0 = not a slot (task / unit / cool row, fake without slots)
        public string Type, Loadout, Where, PendingKey;  // Where: zone|to_zone|leaving|deck_ready|deck_prep|deck_cool|hangar|player|game_zone|lost
        public int AamNow, AamStart, LongNow, LongStart;
        public double Lat, Lon, ReserveNm, MaxRangeNm, PatrolKn, CruiseKn, ReadyInS;
        public bool Engaged, HasTarget, Evading, Controllable = true, GameRtb, GameWinchester, GameBingo, Ours;
        public string AiState;          // T-317: StateMachine.CurrentStateName of the game AI (null = unknown)
        public bool AiActive;           // T-317: the game AI is in its air combat (state or engage tasks)
        public string Name;             // display name (optional): the unit's own name in the air
        public string TypeLabel, ShipName, Serial;   // T-155: display type (not the file name), the ship (deck / home base), the serial number of a machine on a deck
        public bool FuelFracLive;       // T-315 (ADR-0122): true = FuelFrac is the game's live RangeInKm/_maxRangeInKm; false = unknown or the frozen spawn value
        public double FuelFrac = -1;    // game fuel fraction 0..1, -1 = unknown; used by the controller when ReserveNm and MaxRangeNm are both 0
        /// <summary>Correction 2026-10-22: the game's map range ring (RangeOnMap, nm; -1 unknown) and how long the fuel threshold has held (s; -1 = not tracked).</summary>
        public double RingNm = -1, FuelLowS = -1;
        /// <summary>While the ring is under the threshold for less than FuelHoldS: the reserve from before the drop, for the relief planner; -1 = none.</summary>
        public double HeldReserveNm = -1;
        public bool ReadyKnown = true;  // false: ReadyInS is a planning guess (a hangar row without a CAP loadout); the window shows no time for it
        // 0.27.8: on the way home - the order is ReturnToBase or the deck holds it in a return / recovery task (landing circuit: the game clears
        // _isInFlight while it still flies); Recovering = in the deck's recovery task (approach, landing, taxi to the elevator)
        public bool Returning, Recovering;
        public int TargetId = -1;       // T-333: the player's track id of the aircraft's current target (-1 = none / no track)
        public int TargetKind;          // T-175: type of the aircraft's current target: 0 none, 1 air (aircraft, helicopter, missile), 2 surface / ground, -1 unknown
        public bool Alert;              // T-175: the bird belongs to the alert pair (set by the controller on its copy)
        public double AamMaxNm;         // T-175: longest AAM range of its loadout (0 = unknown): the alert intercept hands over to the game AI inside this range + 5 nm
        public Dictionary<string, int> Weapons;      // T-325: current ammunition (air / deck_launch: the unit; task rows: the variant); null = unknown
        public Dictionary<string, int> LoadWeapons;  // T-325: the variant's weapons (deck data) or the max seen for this uid; null = unknown
        public string LoadoutLabel;                  // T-325: the game's display name of the variant (Loadout.DisplayName); null = none
        internal CapBird Copy() { return (CapBird)MemberwiseClone(); }
    }

    /// <summary>A speed step (telegraph) of an aircraft: Step id for SetThrottle, Kn speed, K = game fuel burn per mile coefficient (0 = unknown).</summary>
    internal sealed class CapStep { public int Step; public double Kn, K; public string Name; }   // T-257: Name = Ap._speedValueNames[i], null = unknown

    internal sealed class CapAction
    {
        public string Kind;   // launch|order|allow|abort|rtb|route|warn
        public int Uid, DeckUid, Slot;
        public string Type, Loadout, Key, Code, Reason;
        public double EtaS;
    }


    /// <summary>Hysteresis of ADR-0041 §3 + D1 (one per group).
    /// On: hostile (or hostile missile) &lt;= ThreatNm to the zone or to the deck. Off: no hostile closer than ThreatNm+margin for ThreatClearS.</summary>
    internal sealed class CapThreatLatch
    {
        private double _clearSince = -1;
        public bool On { get; private set; }
        public int ThreatUid { get; private set; }
        public double ThreatNm { get; private set; }

        /// <summary>true when On changed.</summary>
        public bool Update(IList<CapContact> c, CapZone z, GeoPt? deck, double now, CapParams p)
        {
            CapContact near;
            double d = CapMath.NearestHostileNm(c, z, deck, out near);
            if (!On)
            {
                if (near == null || d > p.ThreatNm) return false;
                On = true; ThreatUid = near.Id; ThreatNm = d; _clearSince = -1;
                return true;
            }
            if (near != null) { ThreatUid = near.Id; ThreatNm = d; }
            if (near != null && d < p.ThreatNm + p.ThreatClearMarginNm) { _clearSince = -1; return false; }
            if (_clearSince < 0) { _clearSince = now; return false; }
            if (now - _clearSince >= p.ThreatClearS)
            {
                On = false; _clearSince = -1; ThreatUid = 0; ThreatNm = 0;
                return true;
            }
            return false;
        }
    }

    internal static class CapMath
    {
        private const double Tol = 2.0;   // seconds: tick granularity tolerance for "arrives before leaving"

        /// <summary>T-204: rank of a row a member may take after landing / after its cool-down (lower = better; -1 = not allowed).
        /// landed: deck_cool 0, hangar 1, foreign game task 2.  afterCool (ReadoptDeck from deck_cool): hangar 0, deck_ready w/o key 1, foreign game task 2.
        /// otherwise (ReadoptDeck): hangar 0, deck_ready w/o key 1 (no task rows). foreignTask = PendingKey != null, not the mod's own task.</summary>
        internal static int ReadoptRank(string where, bool hasKey, bool foreignTask, bool landed, bool afterCool)
        {
            if (hasKey)
            {
                if (!foreignTask || !(landed || afterCool)) return -1;
                return where == "deck_prep" || where == "deck_ready" ? 2 : -1;
            }
            if (landed) return where == "deck_cool" ? 0 : where == "hangar" ? 1 : -1;
            return where == "hangar" ? 0 : where == "deck_ready" ? 1 : -1;
        }

        /// <summary>T-218: rank of a free row for a member restored from a file (lower = better, -1 = not allowed).
        /// savedWhere == rowWhere -> 0; deck_ready / deck_prep (a key of a game task is allowed) -> 1; deck_cool -> 2; hangar -> 3; else -1.</summary>
        internal static int RestoreRowRank(string savedWhere, string rowWhere)
        {
            bool deck = rowWhere == "deck_ready" || rowWhere == "deck_prep";
            if (!deck && rowWhere != "deck_cool" && rowWhere != "hangar") return -1;
            if (savedWhere != null && savedWhere == rowWhere) return 0;
            return deck ? 1 : rowWhere == "deck_cool" ? 2 : 3;
        }

        /// <summary>T-218: the best row: same type, deck == 0 or row.DeckUid == deck, free(row); rank, then same loadout, then lower ReadyInS, then lower DeckUid, then lower Uid. null = none.</summary>
        internal static CapBird PickRestoreRow(IEnumerable<CapBird> rows, string type, int deck, string savedWhere, string loadout, Func<CapBird, bool> free)
        {
            CapBird best = null; int bestRank = int.MaxValue;
            foreach (var b in rows)
            {
                if (b == null || b.Type != type || (deck != 0 && b.DeckUid != deck)) continue;
                int rank = RestoreRowRank(savedWhere, b.Where);
                if (rank < 0 || (free != null && !free(b))) continue;
                bool better = best == null || rank < bestRank;
                if (!better && rank == bestRank)
                {
                    bool sameB = loadout != null && b.Loadout == loadout, sameBest = loadout != null && best.Loadout == loadout;
                    if (sameB != sameBest) better = sameB;
                    else if (b.ReadyInS != best.ReadyInS) better = b.ReadyInS < best.ReadyInS;
                    else if (b.DeckUid != best.DeckUid) better = b.DeckUid < best.DeckUid;
                    else better = b.Uid < best.Uid;
                }
                if (better) { best = b; bestRank = rank; }
            }
            return best;
        }

        // ---------- admission (ADR-0040 §3) ----------
        public static bool IsAam(CapAmmo a)
        {
            if (a == null) return false;
            return Eq(a.Type, "Missile") && (Eq(a.TargetType, "AAW") || Eq(a.SecondaryTargetType, "AAW"));
        }

        /// <summary>SR &lt;= 12 &lt; MR &lt;= 45 &lt; LR.</summary>
        public static string AamClass(double maxLaunchRangeNm)
        {
            if (maxLaunchRangeNm <= 12) return "SR";
            return maxLaunchRangeNm <= 45 ? "MR" : "LR";
        }

        private static bool Eq(string a, string b) { return string.Equals(a ?? "", b, StringComparison.OrdinalIgnoreCase); }

        // Guns, drop tanks, sensor / ECM pods and countermeasures are not strike weapons. Unknown ammo (null) is not strike either.
        private static bool IsStrike(CapAmmo a)
        {
            if (a == null || IsAam(a)) return false;
            if (a.FuelKg > 0) return false;
            string t = (a.Type ?? "").ToLowerInvariant();
            foreach (var w in new[] { "projectile", "tank", "pod", "container", "sensor", "jammer", "ecm", "decoy", "countermeasure", "chaff", "flare" })
                if (t.Contains(w)) return false;
            return true;
        }

        // ---------- T-325: strike loadout exclusion ----------
        /// <summary>"aam" | "strike" | "asw" | null (neutral: gun, tank, pod, countermeasure, unknown ammo entry).</summary>
        public static string AmmoRole(CapAmmo a)
        {
            if (a == null) return null;
            if (IsAam(a)) return "aam";
            if (a.FuelKg > 0) return null;
            string t = (a.Type ?? "").Trim().ToLowerInvariant();
            foreach (var w in new[] { "projectile", "tank", "pod", "jammer", "ecm", "decoy", "flare", "chaff", "noisemaker", "paratrooper" })
                if (t.Contains(w)) return null;
            if (t == "" || t == "none" || t == "unknown") return null;
            if (t == "torpedo" || t == "sonobuoy" || t == "airdepthcharge" || t == "asroc" || Eq(a.TargetType, "ASW")) return "asw";
            return "strike";   // bombs, rockets, ASuW / land-attack missiles and any unfamiliar weapon: the careful side
        }

        /// <summary>"air" | "strike" | "none" | null (weapons unknown).</summary>
        public static string LoadoutRole(IDictionary<string, int> w, string variant, Func<string, CapAmmo> ammo, double mixedAamShare)
        {
            if (w == null) return null;
            int aam = 0, s = 0;
            foreach (var kv in w)
            {
                if (kv.Value <= 0) continue;
                string r = AmmoRole(ammo == null ? null : ammo(kv.Key));
                if (r == "aam") aam += kv.Value;
                else if (r == "strike" || r == "asw") s += kv.Value;
            }
            if (s == 0) return aam > 0 ? "air" : "none";
            if ((variant ?? "").StartsWith("AirToAir", StringComparison.OrdinalIgnoreCase)) return "air";
            return (double)aam / (aam + s) >= mixedAamShare ? "air" : "strike";
        }

        /// <summary>"strike_loadout" | null. Hangar and cool-down rows are not checked (the group picks their loadout).</summary>
        public static string StrikeExclusion(CapBird b, Func<string, CapAmmo> ammo, double mixedAamShare)
        {
            if (b == null || b.Where == "hangar" || b.Where == "deck_cool") return null;
            return LoadoutRole(b.LoadWeapons ?? b.Weapons, b.Loadout, ammo, mixedAamShare) == "strike" ? "strike_loadout" : null;
        }

        /// <summary>"R-27R×4, R-73×2": AAM first, then strike / ASW; neutral hidden; more than max items end with ", …".</summary>
        public static string LoadoutSummary(IDictionary<string, int> w, Func<string, CapAmmo> ammo, int max = 4)
        {
            if (w == null) return "";
            var items = new List<KeyValuePair<string, KeyValuePair<int, int>>>();   // text -> (group, count)
            foreach (var kv in w)
            {
                if (kv.Value <= 0) continue;
                var a = ammo == null ? null : ammo(kv.Key);
                string r = AmmoRole(a);
                if (r == null) continue;
                string name = a != null && !string.IsNullOrEmpty(a.Label) ? a.Label : kv.Key;
                items.Add(new KeyValuePair<string, KeyValuePair<int, int>>(name, new KeyValuePair<int, int>(r == "aam" ? 0 : 1, kv.Value)));
            }
            items.Sort((x, y) =>
            {
                int c = x.Value.Key.CompareTo(y.Value.Key);
                if (c != 0) return c;
                c = y.Value.Value.CompareTo(x.Value.Value);
                return c != 0 ? c : string.CompareOrdinal(x.Key, y.Key);
            });
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < items.Count && i < max; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(items[i].Key).Append('\u00D7').Append(items[i].Value.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (items.Count > max) sb.Append(", \u2026");
            return sb.ToString();
        }

        public static bool IsCapLoadout(LoadoutOption lo, Func<string, CapAmmo> ammo, out int aam)
        {
            aam = 0; bool strike = false;
            if (lo == null || lo.Weapons == null) return false;
            foreach (var kv in lo.Weapons)
            {
                if (kv.Value <= 0) continue;
                var a = ammo == null ? null : ammo(kv.Key);
                if (IsAam(a)) aam += kv.Value;
                else if (IsStrike(a)) strike = true;
            }
            if (aam < 1) return false;
            return (lo.Name ?? "").StartsWith("AirToAir", StringComparison.OrdinalIgnoreCase) || !strike;
        }

        /// <summary>VehicleTypeOnBoard._type: Plane | VTOL | STOL (+ legacy "Aircraft") = fixed wing.</summary>
        public static bool IsFixedWingClass(string vehicleClass)
        {
            string vc = (vehicleClass ?? "").Trim();
            return Eq(vc, "Plane") || Eq(vc, "VTOL") || Eq(vc, "STOL") || Eq(vc, "Aircraft");
        }

        /// <summary>null = admitted | helicopter | no_aam | self_defence.</summary>
        public static string TypeAdmission(string vehicleClass, IList<LoadoutOption> los, Func<string, CapAmmo> ammo)
        {
            string vc = (vehicleClass ?? "").Trim();
            if (Eq(vc, "Helicopter")) return "helicopter";
            if (!IsFixedWingClass(vc)) return "no_aam";
            bool anyAam = false;
            if (los != null)
                foreach (var lo in los)
                {
                    int aam;
                    if (IsCapLoadout(lo, ammo, out aam)) return null;
                    if (lo != null && lo.Weapons != null)
                        foreach (var kv in lo.Weapons) if (kv.Value > 0 && IsAam(ammo == null ? null : ammo(kv.Key))) anyAam = true;
                }
            return anyAam ? "self_defence" : "no_aam";
        }

        // ---------- loadout (§3.3) ----------
        /// <summary>LR*1e5 (1 when the loadout has any long-range AAM) + sum(range*count)*1e2 + (AAM&gt;0)*1e4; non-CAP -> 0.</summary>
        public static double LoadoutScore(LoadoutOption lo, Func<string, CapAmmo> ammo)
        {
            int aam;
            if (!IsCapLoadout(lo, ammo, out aam)) return 0;
            double lr = 0, sum = 0;
            foreach (var kv in lo.Weapons)
            {
                var a = ammo(kv.Key);
                if (kv.Value <= 0 || !IsAam(a)) continue;
                if (AamClass(a.MaxLaunchRangeNm) == "LR") lr = 1;
                sum += a.MaxLaunchRangeNm * kv.Value;
            }
            return lr * 1e5 + sum * 1e2 + (aam > 0 ? 1e4 : 0);
        }

        public static int PickLoadout(IList<LoadoutOption> los, Func<string, CapAmmo> ammo, CapParams p, ISet<string> skip)
        {
            return AirOpsMath.PickBest(los, lo => LoadoutScore(lo, ammo), p == null ? "" : p.LoadoutTieBreak, skip);
        }

        // ---------- early leave (§5) ----------
        public static int MissileFloor(int aamStart, double pct) { return (int)Math.Floor(pct * aamStart + 1e-9); }

        /// <summary>null | missiles | long_range.</summary>
        public static string MissileLeave(CapBird b, CapParams p)
        {
            if (b.AamStart > 0 && b.AamNow <= MissileFloor(b.AamStart, p.MissilePct)) return "missiles";
            if (p.NeedLongRange && b.LongStart > 0 && b.LongNow <= 0) return "long_range";
            return null;
        }

        public static double FuelThresholdNm(double patrolKn, double reliefS, double maxRangeNm, CapParams p)
        {
            return Math.Max(patrolKn * (reliefS + p.FuelMarginS) / 3600.0, p.FuelFloorPct * maxRangeNm);
        }

        /// <summary>T-319: EngageAirContact and its manoeuvre sub-states (same names as CapTakeoverMath.CombatStates, T-317); ordinal compare.</summary>
        private static readonly string[] AirFightStates =
            { "EngageAirContact", "Pursuit", "Chandelle", "Immelman", "SplitS", "HighYoYo", "Scissors", "BreakTurn", "Cranking" };

        /// <summary>T-319: the game AI fights an air contact (state name only; null/empty = false).</summary>
        public static bool AirFightState(string state)
        {
            if (string.IsNullOrEmpty(state)) return false;
            foreach (string s in AirFightStates) if (string.Equals(s, state, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>T-319: CapBird.Engaged - engage tasks, MoveToEngage, Aligning or an air fight state (no tasks needed).</summary>
        public static bool EngagedNow(string state, int engageTasks)
        {
            return engageTasks > 0 || state == "MoveToEngage" || state == "Aligning" || AirFightState(state);
        }

        /// <summary>null | combat | hostile_near. Never when AamNow == 0 (nothing to fight with). T-175 AirOnly: "combat" only when the target of the bird is an air one
        /// (TargetKind 1) or the bird evades a missile; a surface, ground or unreadable target does not hold it (the old rule: any target / engagement).</summary>
        public static string Postpone(CapBird b, double nearestHostileNm, CapParams p)
        {
            if (b.AamNow <= 0) return null;
            if (p == null || !p.AirOnly) { if (b.Engaged || b.HasTarget || b.Evading) return "combat"; }
            else if (b.Evading || ((b.Engaged || b.HasTarget) && b.TargetKind == 1) || AirFightState(b.AiState)) return "combat";   // T-319: the air fight state needs no target kind
            if (nearestHostileNm < (p == null ? 10 : p.CombatHostileNm)) return "hostile_near";
            return null;
        }

        /// <summary>T-175 (ADR-0075 item 4): where an interceptor of speed ownKn meets a target (flat nm, x east, y north from the bird; velocities in kn): the smallest t &gt; 0 of
        /// |d + v t| = ownKn t, the same equation as InterceptMath.LeadTime (ADR-0054, not compiled into every stand). false = no lead (the target runs away faster).</summary>
        public static bool LeadPoint(double dxNm, double dyNm, double vxKn, double vyKn, double ownKn, out double ixNm, out double iyNm)
        {
            ixNm = dxNm; iyNm = dyNm;
            double a = vxKn * vxKn + vyKn * vyKn - ownKn * ownKn, b = 2 * (dxNm * vxKn + dyNm * vyKn), c = dxNm * dxNm + dyNm * dyNm;
            double t;
            if (c < 1e-12) return false;
            if (Math.Abs(a) < 1e-6) { if (!(b < 0)) return false; t = -c / b; }
            else
            {
                double disc = b * b - 4 * a * c;
                if (disc < 0) return false;
                double sq = Math.Sqrt(disc), t1 = (-b - sq) / (2 * a), t2 = (-b + sq) / (2 * a), lo = Math.Min(t1, t2), hi = Math.Max(t1, t2);
                t = lo > 0 ? lo : hi;
            }
            if (!(t > 0) || t > 6) return false;
            ixNm = dxNm + vxKn * t; iyNm = dyNm + vyKn * t;
            return true;
        }

        /// <summary>The target of the bird is a surface / ground object (T-175: AirOnly drops such a task).</summary>
        public static bool SurfaceTarget(CapBird b) { return b != null && b.TargetKind == 2; }

        /// <summary>T-175 (ADR-0075 item 8): the loadout with the shortest ready-up among the CAP loadouts (AirOpsMath.ReadyS), ties by the score; -1 = none. Unavailable ones count (the planner skips them itself).</summary>
        public static int AlertFastestLoadout(IList<LoadoutOption> los, Func<string, CapAmmo> ammo, string timingsMode)
        {
            int best = -1; double bestS = double.MaxValue, bestScore = -1;
            if (los == null) return -1;
            for (int i = 0; i < los.Count; i++)
            {
                var lo = los[i];
                int aam;
                if (lo == null || !lo.Available || !IsCapLoadout(lo, ammo, out aam)) continue;
                double s = AirOpsMath.ReadyS(lo, timingsMode, 0), sc = LoadoutScore(lo, ammo);
                if (s < bestS - 1e-9 || (Math.Abs(s - bestS) <= 1e-9 && sc > bestScore)) { best = i; bestS = s; bestScore = sc; }
            }
            return best;
        }

        /// <summary>T-175 (ADR-0075 item 8): a prepared task of the mod is ordered again when the new loadout is ready sooner by more than ReorderGainS.</summary>
        public static bool ReorderWorth(double oldLeftS, double newReadyS, CapParams p)
        {
            return oldLeftS > newReadyS + (p == null ? 60 : p.ReorderGainS);
        }

        /// <summary>Game rules first (game_bingo, game_winchester, game_rtb), then our thresholds (missiles, long_range, fuel).
        /// A threshold leave that must wait gives null and the Postpone code in <paramref name="postponed"/>.</summary>
        public static string LeaveReason(CapBird b, double reliefS, double nearestHostileNm, CapParams p, out string postponed)
        {
            postponed = null;
            if (b.GameBingo) return "game_bingo";
            if (b.GameWinchester) return "game_winchester";
            if (b.GameRtb) return "game_rtb";
            string r = MissileLeave(b, p);
            // correction 2026-10-22 (ADR-0040 D5): the game's ring must stay below the threshold for FuelHoldS (FuelLowS < 0 = not tracked: at once)
            // T-337 E: by=margin at a live fraction <= 10 % holds FuelMarginHoldS (5 s) instead
            string lowBy = r == null ? FuelLowBy(b, reliefS, p) : null;
            if (lowBy != null && (b.FuelLowS < 0 || b.FuelLowS >= FuelHoldFor(b, lowBy, p))) r = "fuel";
            if (r == null) return null;
            if (r == "fuel" && FuelOverridesPostpone(b)) return r;   // T-337: low live fuel is never held by combat / hostile_near
            postponed = Postpone(b, nearestHostileNm, p);
            return postponed != null ? null : r;
        }

        // ---------- zone, lanes, leash (§4, §6) ----------
        /// <summary>0 inside; flat on stable (1 nm = 1/60 deg on both axes), east-west x LonK on the beta globe (T-348).</summary>
        public static double DistToZoneNm(GeoPt g, CapZone z)
        {
            if (z == null || z.Rect == null || z.Rect.Length < 4) return double.MaxValue;
            double n = Math.Max(Math.Max(z.Rect[0] - g.Lat, 0), g.Lat - z.Rect[2]) * 60.0;
            double e = Math.Max(Math.Max(z.Rect[1] - g.Lon, 0), g.Lon - z.Rect[3]) * 60.0 * AswMath.LonK(z.Rect[0], z.Rect[2]);
            return Math.Sqrt(n * n + e * e);
        }

        /// <summary>A unit anchor carries the rect (size kept, no rotation). null position: last rect, AnchorLost.</summary>
        public static double[] AnchoredRect(CapZone z, GeoPt? anchorPos)
        {
            if (z.Anchor != "unit") return z.Rect;
            if (anchorPos == null) { z.AnchorLost = true; return z.Rect; }
            z.AnchorLost = false;
            double hl = (z.Rect[2] - z.Rect[0]) / 2, hw = (z.Rect[3] - z.Rect[1]) / 2;
            double cl = anchorPos.Value.Lat + z.OffLat, cw = anchorPos.Value.Lon + z.OffLon;
            z.Rect = new[] { cl - hl, cw - hw, cl + hl, cw + hw };
            return z.Rect;
        }

        /// <summary>T-235 (ADR-0097): the cover radius of a group: CoverAamFrac x the minimum of the known (&gt; 0) longest-AAM ranges; none known -> CoverFallbackNm; at least 1 nm.</summary>
        public static double CoverNm(IEnumerable<double> aamMaxNm, CapParams p)
        {
            double min = double.MaxValue;
            if (aamMaxNm != null) foreach (var r in aamMaxNm) if (r > 0 && r < min) min = r;
            double rc = min == double.MaxValue ? p.CoverFallbackNm : min * p.CoverAamFrac;
            return Math.Max(1, rc);
        }

        /// <summary>N strips along the long side. T-235 (ADR-0097): each strip gets m = 2q evenly spaced legs, q = max(1, ceil(strip / (4 Rc))), so every point of the zone
        /// is within Rc (coverNm) of the route; the legs are d = strip / m apart with d/2 to the strip edge; q = 1 is the 4-point racetrack, q &gt;= 2 a serpentine closed
        /// along the a0 end. Leg ends keep min(LaneEndMaxNm, 10 % of the length, sqrt(Rc^2 - (d/2)^2)) from the zone ends. An orbit only when a side of the zone is shorter
        /// than OrbitBelowNm (or the legs would be shorter than 1 nm) AND the orbit of the strip covers the strip; otherwise a narrow racetrack.</summary>
        public static List<CapLane> Lanes(CapZone z, int n, CapParams p, double coverNm)
        {
            var res = new List<CapLane>();
            if (z == null || z.Rect == null || z.Rect.Length < 4 || n < 1) return res;
            double hNm = (z.Rect[2] - z.Rect[0]) * 60.0, wNm = (z.Rect[3] - z.Rect[1]) * 60.0 * AswMath.LonK(z.Rect[0], z.Rect[2]);
            bool ns = hNm >= wNm;                       // long axis north-south
            double len = ns ? hNm : wNm, wid = ns ? wNm : hNm;
            double rc = Math.Max(1, coverNm);
            double strip = wid / n;
            int q = Math.Max(1, (int)Math.Ceiling(strip / (4 * rc) - 1e-9)), m = 2 * q;
            double d = strip / m;
            double reach = Math.Sqrt(Math.Max(0, rc * rc - (d / 2) * (d / 2)));
            double inset = Math.Min(Math.Min(p.LaneEndMaxNm, 0.1 * len), reach);
            // T-244 (ADR-0102): a small zone (a side shorter than the circle) is one circle, shared by the group; it must cover the zone from its centre
            double circR = Math.Max(1, TurnRadiusNm(p.CircleSpeedKn, p.CircleBankDeg));
            double diag = Math.Sqrt((len / 2) * (len / 2) + (wid / 2) * (wid / 2));
            bool circleCovers = circR <= rc && diag - circR <= rc;
            bool small = (wid < Math.Max(p.OrbitBelowNm, 2 * circR) && circleCovers) || len - 2 * inset < 1;
            for (int i = 0; i < n; i++)
            {
                double c = (i + 0.5) * strip;
                var lane = new CapLane { Slot = i, Center = Pt(z, ns, len / 2, c), CoverNm = rc };
                if (small)
                {
                    lane.Center = new GeoPt((z.Rect[0] + z.Rect[2]) / 2, (z.Rect[1] + z.Rect[3]) / 2);
                    CircleLane(lane, i, n, circR, p);
                }
                else
                {
                    double a0 = inset, a1 = len - inset;
                    lane.Legs = m; lane.LegSpacingNm = d;
                    lane.Track = new List<GeoPt>();
                    for (int j = 0; j < m; j++)
                    {
                        double cj = i * strip + (j + 0.5) * d;
                        double s = j % 2 == 0 ? a0 : a1, e = j % 2 == 0 ? a1 : a0;
                        lane.Track.Add(Pt(z, ns, s, cj)); lane.Track.Add(Pt(z, ns, e, cj));
                    }
                }
                res.Add(lane);
            }
            return res;
        }

        /// <summary>T-244: radius of a steady turn, nm: v^2 / (g tan(bank)). Bad input falls back to 300 kn / 30 deg.</summary>
        public static double TurnRadiusNm(double speedKn, double bankDeg)
        {
            if (speedKn <= 0) speedKn = 300;
            if (bankDeg <= 0 || bankDeg >= 85) bankDeg = 30;
            double v = speedKn * 1852.0 / 3600.0;
            return v * v / (9.80665 * Math.Tan(bankDeg * Math.PI / 180.0)) / 1852.0;
        }

        /// <summary>T-244: lane i of n on the shared circle: CirclePoints (4..8) points clockwise, the first at i/n of the circle (north = 0), altitude base + i x step.</summary>
        private static void CircleLane(CapLane lane, int i, int n, double radiusNm, CapParams p)
        {
            int k = Math.Max(4, Math.Min(8, p.CirclePoints));
            double a0 = 360.0 * i / Math.Max(1, n);
            lane.Circle = true; lane.RadiusNm = radiusNm; lane.StartDeg = a0; lane.Track = new List<GeoPt>();
            for (int j = 0; j < k; j++)
            {
                double a = (a0 + 360.0 * j / k) * Math.PI / 180.0;
                lane.Track.Add(new GeoPt(lane.Center.Lat + radiusNm * Math.Cos(a) / 60.0, lane.Center.Lon + radiusNm * Math.Sin(a) / (60.0 * AswMath.LonK(lane.Center.Lat))));
            }
            double step = Math.Max(0, p.CircleAltStepFt);
            lane.AltFt = (int)Math.Round((p.PatrolAltFt > 0 ? p.PatrolAltFt : p.CircleBaseAltFt) + i * step);
        }

        /// <summary>T-244: on the circle = within radius + tolerance of the circle centre (flat rule).</summary>
        public static bool OnCircle(GeoPt g, CapLane lane, CapParams p)
        {
            if (lane == null || !lane.Circle) return false;
            double dy = (g.Lat - lane.Center.Lat) * 60.0, dx = (g.Lon - lane.Center.Lon) * 60.0 * AswMath.LonK(lane.Center.Lat);
            return Math.Sqrt(dx * dx + dy * dy) <= lane.RadiusNm + p.CircleTolNm;
        }

        /// <summary>T-250: phase of pos around center: 0 = north, clockwise, [0, 360) (flat degrees, as CircleLane).</summary>
        public static double CirclePhaseDeg(GeoPt pos, GeoPt center)
        {
            double a = Math.Atan2(pos.Lon - center.Lon, pos.Lat - center.Lat) * 180.0 / Math.PI;
            a = (a % 360 + 360) % 360;
            return a >= 360 ? 0 : a;
        }

        /// <summary>T-250: wrap to (-180, 180].</summary>
        public static double Wrap180(double deg)
        {
            double a = ((deg % 360) + 360) % 360;
            return a > 180 ? a - 360 : a;
        }

        /// <summary>T-250: lap time, game s: 2 pi R / v * 3600 (v &lt;= 0 -> CircleSpeedKn).</summary>
        public static double LapS(double radiusNm, double kn, CapParams p)
        {
            if (kn <= 0) kn = p.CircleSpeedKn > 0 ? p.CircleSpeedKn : 300;
            return 2 * Math.PI * Math.Max(0.1, radiusNm) / kn * 3600.0;
        }

        /// <summary>T-250: radius of the next lap: R x speed share x (1 + gain x err / 360), clamped to [max(1, (1 - cut) R); (1 + stretch) R].</summary>
        public static double PhaseRadius(double radiusNm, double errDeg, double speedShare, CapParams p)
        {
            double r = radiusNm * speedShare * (1 + p.CirclePhaseGain * errDeg / 360.0);
            double lo = Math.Max(1, (1 - p.CircleCutMax) * radiusNm), hi = (1 + p.CircleStretchMax) * radiusNm;
            if (lo > hi) lo = hi;
            return Math.Max(lo, Math.Min(hi, r));
        }

        /// <summary>T-250: equal spacing by rank on the circle (360/k); kn may be null (equal speeds); k &lt; 2 -> empty list; result in input order.</summary>
        public static List<CapPhaseFix> PhasePlan(IList<int> uids, IList<double> phasesDeg, IList<double> kn, double radiusNm, CapParams p)
        {
            var res = new List<CapPhaseFix>();
            int k = uids == null ? 0 : uids.Count;
            if (k < 2 || phasesDeg == null || phasesDeg.Count < k) return res;
            var order = new List<int>();
            for (int i = 0; i < k; i++) order.Add(i);
            order.Sort((a, b) => { int c = phasesDeg[a].CompareTo(phasesDeg[b]); return c != 0 ? c : a.CompareTo(b); });
            double sx = 0, sy = 0;
            for (int j = 0; j < k; j++)
            {
                double o = (phasesDeg[order[j]] - 360.0 * j / k) * Math.PI / 180.0;
                sx += Math.Cos(o); sy += Math.Sin(o);
            }
            double mean = Math.Atan2(sy, sx) * 180.0 / Math.PI;
            double vavg = 0; int vn = 0;
            if (kn != null && kn.Count >= k) for (int i = 0; i < k; i++) if (kn[i] > 0) { vavg += kn[i]; vn++; }
            if (vn > 0) vavg /= vn;
            for (int i = 0; i < k; i++) res.Add(null);
            for (int j = 0; j < k; j++)
            {
                int i = order[j];
                double t = ((mean + 360.0 * j / k) % 360 + 360) % 360;
                double e = Wrap180(phasesDeg[i] - t);
                double s = vn > 0 && kn[i] > 0 ? kn[i] / vavg : 1;
                bool fix = Math.Abs(e) > p.CirclePhaseTolDeg || Math.Abs(s - 1) > 0.02;
                double r = fix ? PhaseRadius(radiusNm, e, s, p) : radiusNm;
                if (fix && Math.Abs(r / radiusNm - 1) <= 0.02) { fix = false; r = radiusNm; }
                res[i] = new CapPhaseFix { Uid = uids[i], PhaseDeg = phasesDeg[i], TargetDeg = t, ErrDeg = e, RadiusNm = r, Fix = fix };
            }
            return res;
        }

        /// <summary>T-250: circle route from the first lane grid point at least half a step ahead of fromPhaseDeg, radius radiusNm, laps 1..2, closed.</summary>
        public static List<GeoPt> CircleRouteFrom(CapLane lane, double fromPhaseDeg, double radiusNm, int laps, CapParams p)
        {
            int k = Math.Max(4, lane.Track == null ? 4 : lane.Track.Count);
            laps = Math.Max(1, Math.Min(2, laps));
            double step = 360.0 / k, best = double.MaxValue; int bm = 0;
            for (int m = 0; m < k; m++)
            {
                double d = ((lane.StartDeg + m * step - fromPhaseDeg) % 360 + 360) % 360;
                if (d >= step / 2 - 1e-9 && d < best) { best = d; bm = m; }
            }
            var r = new List<GeoPt>();
            for (int j = 0; j < laps * k; j++)
            {
                double a = (lane.StartDeg + (bm + j) * step) * Math.PI / 180.0;
                r.Add(new GeoPt(lane.Center.Lat + radiusNm * Math.Cos(a) / 60.0, lane.Center.Lon + radiusNm * Math.Sin(a) / (60.0 * AswMath.LonK(lane.Center.Lat))));
            }
            r.Add(r[0]);
            return r;
        }

        /// <summary>T-250: log text "550:12/+35 570:131/-10", max |err|.</summary>
        public static string PhaseText(IList<CapPhaseFix> plan, out double maxErrDeg)
        {
            maxErrDeg = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var f in plan)
            {
                if (sb.Length > 0) sb.Append(' ');
                maxErrDeg = Math.Max(maxErrDeg, Math.Abs(f.ErrDeg));
                sb.Append(f.Uid.ToString(CultureInfo.InvariantCulture)).Append(':').Append(Math.Round(f.PhaseDeg).ToString("0", CultureInfo.InvariantCulture))
                  .Append('/').Append(Math.Round(f.ErrDeg).ToString("+0;-0;+0", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Lanes with the fallback cover radius (old callers, tests).</summary>
        public static List<CapLane> Lanes(CapZone z, int n, CapParams p) { return Lanes(z, n, p, p.CoverFallbackNm); }

        /// <summary>T-235: the waypoint list given to the game: Track + Track[0] for a non-orbit lane (the re-issue at &lt;= 1 point left must not cut a leg).
        /// T-244: a circle is given as two laps + the first point (one re-issue per two laps, at the start point).</summary>
        public static List<GeoPt> PatrolRoute(CapLane lane) { return PatrolRoute(lane, null); }

        /// <summary>T-331: an orbit lane (a spare bird without a slot) is a real circle round the centre (radius = the turn radius, at least 1 nm; 4 points clockwise from the north + the first
        /// again), re-issued at &lt;= 1 point left like every other route. A one-point route is the single waypoint whose end the game reports as "We're on position" at each pass.</summary>
        public static List<GeoPt> PatrolRoute(CapLane lane, CapParams p)
        {
            if (lane.Orbit && !lane.Circle)
            {
                double radius = p == null ? 1 : Math.Max(1, TurnRadiusNm(p.CircleSpeedKn, p.CircleBankDeg));
                var o = new List<GeoPt>();
                for (int i = 0; i < 4; i++) o.Add(AswMath.Offset(lane.Center, 90.0 * i, radius));
                o.Add(o[0]);
                return o;
            }
            var r = new List<GeoPt>(lane.Track);
            if (lane.Circle && r.Count > 1) { r.AddRange(lane.Track); r.Add(r[0]); return r; }
            if (!lane.Orbit && r.Count > 1) r.Add(r[0]);
            return r;
        }

        // a: nm along the long axis from the min edge, c: nm across from the min edge.
        private static GeoPt Pt(CapZone z, bool ns, double a, double c)
        {
            double k = 60.0 * AswMath.LonK(z.Rect[0], z.Rect[2]);   // T-348: nm per degree of longitude (60 on stable)
            return ns ? new GeoPt(z.Rect[0] + a / 60.0, z.Rect[1] + c / k)
                      : new GeoPt(z.Rect[0] + c / 60.0, z.Rect[1] + a / k);
        }

        public static bool BeyondLeash(GeoPt g, CapZone z, CapParams p) { return DistToZoneNm(g, z) > p.PursuitNm; }

        // ---------- deck priority (ADR-0041) ----------
        /// <summary>Nearest hostile (identified enemy, missiles included) to the zone or to the deck; double.MaxValue and null when none.</summary>
        internal static double NearestHostileNm(IList<CapContact> c, CapZone z, GeoPt? deck, out CapContact nearest)
        {
            nearest = null; double best = double.MaxValue;
            if (c == null) return best;
            foreach (var k in c)
            {
                if (k == null || !k.Hostile) continue;
                var g = new GeoPt(k.Lat, k.Lon);
                double d = z == null ? double.MaxValue : DistToZoneNm(g, z);
                if (deck != null) d = Math.Min(d, AswMath.DistNm(deck.Value, g));
                if (d < best) { best = d; nearest = k; }
            }
            return best;
        }

        public static bool AirThreat(IList<CapContact> c, CapZone z, GeoPt? deck, double nm, out CapContact nearest)
        {
            double d = NearestHostileNm(c, z, deck, out nearest);
            return nearest != null && d <= nm;
        }

        /// <summary>onStation &lt; N, or some replacement arrives later than the leaver goes (parallel lists).</summary>
        public static bool HasGap(int onStation, int n, IList<double> reliefS, IList<double> leaveInS)
        {
            if (onStation < n) return true;
            if (reliefS == null || leaveInS == null) return false;
            int m = Math.Min(reliefS.Count, leaveInS.Count);
            for (int i = 0; i < m; i++) if (reliefS[i] > leaveInS[i]) return true;
            return false;
        }

        // ---------- relief (§7) ----------
        /// <summary>Ready + LaunchDelayS (only for a bird still on deck / in hangar) + path to the slot / CruiseKn.</summary>
        public static double ReliefS(CapBird r, GeoPt slot, CapParams p)
        {
            bool airborne = r.Where == "zone" || r.Where == "to_zone" || r.Where == "leaving";
            double kn = r.CruiseKn > 0 ? r.CruiseKn : 450;
            return Math.Max(0, r.ReadyInS) + (airborne ? 0 : p.LaunchDelayS) + AswMath.DistNm(new GeoPt(r.Lat, r.Lon), slot) / kn * 3600.0;
        }

        /// <summary>Index in reserve or -1: ready now &gt; ready sooner &gt; higher score &gt; shorter path.</summary>
        public static int PickReliever(IList<CapBird> reserve, GeoPt slot, Func<CapBird, double> score, CapParams p)
        {
            int best = -1;
            if (reserve == null) return best;
            for (int i = 0; i < reserve.Count; i++)
            {
                if (reserve[i] == null) continue;
                if (best < 0 || Better(reserve[i], reserve[best], slot, score)) best = i;
            }
            return best;
        }

        private static bool Better(CapBird a, CapBird b, GeoPt slot, Func<CapBird, double> score)
        {
            bool ra = a.ReadyInS <= 0, rb = b.ReadyInS <= 0;
            if (ra != rb) return ra;
            if (Math.Abs(a.ReadyInS - b.ReadyInS) > 1e-6) return a.ReadyInS < b.ReadyInS;
            double sa = score == null ? 0 : score(a), sb = score == null ? 0 : score(b);
            if (Math.Abs(sa - sb) > 1e-9) return sa > sb;
            return AswMath.DistNm(new GeoPt(a.Lat, a.Lon), slot) < AswMath.DistNm(new GeoPt(b.Lat, b.Lon), slot) - 1e-9;
        }

        private static bool IsHolder(CapBird b)
        {
            return (b.Where == "zone" || b.Where == "to_zone") && b.Controllable && !b.GameRtb && !b.GameWinchester && !b.GameBingo;
        }

        private static bool IsReserve(CapBird b)
        {
            return b.Where == "deck_ready" || b.Where == "deck_prep" || b.Where == "deck_cool" || b.Where == "hangar";
        }

        /// <summary>Seconds until the bird must leave: now for missiles, else when the fuel reserve hits margin / floor; 1e9 = unknown fuel.</summary>
        private static double LeaveInS(CapBird b, CapParams p)
        {
            return MissileLeave(b, p) != null ? 0 : FuelLeaveInS(b, p);
        }

        /// <summary>T-315: the fuel part of LeaveInS (1e9 = unknown fuel). With a live fuel fraction the floor is the fraction (ADR-0122).</summary>
        public static double FuelLeaveInS(CapBird b, CapParams p)
        {
            if (FracFuelKnown(b) && !ReserveKnown(b)) return b.FuelFrac <= HardFloorPct ? 0 : 1e9;   // ADR-0131: reserve unknown -> hard floor only
            if (b.ReserveNm <= 0 && b.MaxRangeNm <= 0) return 1e9;
            double v = b.PatrolKn > 0 ? b.PatrolKn : 300;
            double res = b.HeldReserveNm >= 0 ? b.HeldReserveNm : b.ReserveNm;   // a ring collapsed for a moment does not call a relief early
            double byMargin = FracFuelKnown(b) ? (res - RingMarginNm(b.PatrolKn, 0, p)) / v * 3600.0 : res / v * 3600.0 - p.FuelMarginS;
            double byFloor;
            if (FracFuelKnown(b))
            {
                double fr = b.FuelFrac;
                byFloor = p.FuelFloorPct <= 0 ? 1e9 : (fr > p.FuelFloorPct ? res * (fr - p.FuelFloorPct) / fr / v * 3600.0 : 0);
            }
            else byFloor = (res - p.FuelFloorPct * b.MaxRangeNm) / v * 3600.0;
            return Math.Max(0, Math.Min(byMargin, byFloor));
        }

        private static LoadoutOption FindLo(IList<LoadoutOption> los, string name)
        {
            if (los == null || name == null) return null;
            foreach (var lo in los) if (lo != null && Eq(lo.Name, name)) return lo;
            return null;
        }

        // Group loadout for (deck, type): explicit choice when available, else best CAP loadout ("auto" or fallback). fell = fallback used.
        private static string ResolveLoadout(int deck, string type, IDictionary<string, string> byType, Func<int, string, IList<LoadoutOption>> deckLos,
                                             Func<string, CapAmmo> ammo, CapParams p, out bool fell)
        {
            fell = false;
            string want = null;
            if (byType != null && type != null) byType.TryGetValue(type, out want);
            bool auto = string.IsNullOrEmpty(want) || Eq(want, "auto");
            var los = deckLos == null ? null : deckLos(deck, type);
            if (los == null) return auto ? null : want;
            var avail = new List<LoadoutOption>();
            foreach (var lo in los) if (lo != null && lo.Available) avail.Add(lo);
            if (!auto)
            {
                var f = FindLo(avail, want);
                if (f != null) return f.Name;
            }
            int i = PickLoadout(avail, ammo, p, auto ? null : new HashSet<string>(new[] { want }));
            fell = !auto && i >= 0;
            return i >= 0 ? avail[i].Name : null;
        }

        public static List<CapAction> PlanRelief(IList<CapBird> m, int n, int k, IList<CapLane> lanes, IDictionary<string, string> loadoutByType,
                                                 Func<int, string, IList<LoadoutOption>> deckLos, Func<string, CapAmmo> ammo, string deckMode,
                                                 double now, CapParams p, out bool gap, out double gapS)
        {
            var acts = new List<CapAction>();
            gap = false; gapS = 0;
            if (m == null || n <= 0 || lanes == null || lanes.Count == 0) return acts;
            if (k < 0) k = 0;
            var air = new List<CapBird>(); var res = new List<CapBird>();
            foreach (var b in m)
            {
                if (b == null) continue;
                if (IsHolder(b)) air.Add(b);
                else if (IsReserve(b)) res.Add(b);
            }
            if (air.Count == 0 && res.Count == 0) return acts;

            // slots of airborne birds (unassigned ones fill the lowest free slots)
            var slotOf = new Dictionary<CapBird, int>();
            var taken = new HashSet<int>();
            foreach (var b in air) if (b.Slot >= 0 && b.Slot < n) { slotOf[b] = b.Slot; taken.Add(b.Slot); }
            var slotTl = new double[n]; var slotHolder = new CapBird[n];
            for (int s = 0; s < n; s++) slotTl[s] = 0;
            Action<CapBird> hold = b =>
            {
                int s = slotOf[b]; double tl = LeaveInS(b, p);
                if (slotHolder[s] == null || tl > slotTl[s]) { slotTl[s] = tl; slotHolder[s] = b; }
            };
            foreach (var b in air) if (slotOf.ContainsKey(b)) hold(b);
            // unassigned ones: the lowest free slot. T-179 (ADR-0079): an extra bird with every slot held is neither a holder nor a relief (the "spare over the zone"
            // of ADR-0075 supplement 1 is gone; an alert bird is lent into a free slot only, CapController.Lend.cs)
            foreach (var b in air)
            {
                if (slotOf.ContainsKey(b)) continue;
                int s = 0; while (s < n && taken.Contains(s)) s++;
                if (s >= n) continue;
                slotOf[b] = s; taken.Add(s);
                hold(b);
            }

            // reserve with effective readiness and group loadout per bird
            var eff = new List<CapBird>(); var compat = new Dictionary<CapBird, bool>(); var warned = new HashSet<string>();
            var keepTypes = new HashSet<string>();   // T-175: types with a prepared task of the mod that stays although another loadout is wanted
            var unkeyed = new List<KeyValuePair<CapBird, CapBird>>();
            foreach (var r in res)
            {
                bool fell;
                string want = ResolveLoadout(r.DeckUid, r.Type, loadoutByType, deckLos, ammo, p, out fell);
                if (fell && warned.Add(r.Type ?? "")) acts.Add(new CapAction { Kind = "warn", Code = "loadout_fallback", Type = r.Type, Loadout = want });
                if (want == null && warned.Add("noammo:" + r.Type)) acts.Add(new CapAction { Kind = "warn", Code = "no_ammo", Type = r.Type });
                var e = r.Copy();
                // T-226 (T-187-Q3): a task with a key of any owner is not a new order: ETA = its remainder, loadout = its own; foreign counts in reserve K but is never aborted
                bool keyed = r.PendingKey != null;
                if (!keyed && r.Where != "deck_ready")
                {
                    var los = deckLos == null ? null : deckLos(r.DeckUid, r.Type);
                    e.ReadyInS = Math.Max(0, r.ReadyInS) + AirOpsMath.ReadyS(FindLo(los, want), deckMode, 0);
                    e.Loadout = want;
                }
                if (!keyed && r.Where != "deck_ready") unkeyed.Add(new KeyValuePair<CapBird, CapBird>(r, e));
                bool fixedLo = keyed || r.Where == "deck_ready";
                // T-175 (ADR-0075 item 8): a prepared task of the mod with another loadout than the wanted one is ordered again only when the wanted one is
                // ready sooner by more than ReorderGainS; otherwise it stays (compatible)
                bool keep = false;
                if (keyed && want != null && !Eq(r.Loadout, want) && !string.IsNullOrEmpty(r.Loadout))
                {
                    var nlo = FindLo(deckLos == null ? null : deckLos(r.DeckUid, r.Type), want);
                    keep = nlo != null && !ReorderWorth(r.ReadyInS, AirOpsMath.ReadyS(nlo, deckMode, 0), p);
                }
                if (keep) keepTypes.Add(r.Type ?? "");
                compat[e] = want != null && (!fixedLo || Eq(r.Loadout, want) || string.IsNullOrEmpty(r.Loadout) || keep);
                if (!fixedLo) e.Loadout = want;
                eff.Add(e);
            }
            // a fresh order of the same type must be ready sooner by more than ReorderGainS than the kept task, else the kept one is taken
            foreach (var kv in unkeyed) if (keepTypes.Contains(kv.Key.Type ?? "")) kv.Value.ReadyInS += p.ReorderGainS;
            var origOf = new Dictionary<CapBird, CapBird>();
            for (int i = 0; i < eff.Count; i++) origOf[eff[i]] = res[i];
            var used = new HashSet<CapBird>();

            Func<CapBird, double> score = b =>
            {
                var los = deckLos == null ? null : deckLos(b.DeckUid, b.Type);
                var lo = FindLo(los, b.Loadout);
                return lo == null ? 0 : LoadoutScore(lo, ammo);
            };

            var order = new List<int>();
            for (int s = 0; s < n; s++) order.Add(s);
            order.Sort((a, b) => slotTl[a] != slotTl[b] ? slotTl[a].CompareTo(slotTl[b]) : a.CompareTo(b));
            bool anyBird = air.Count + res.Count > 0;
            foreach (int s in order)
            {
                var lane = s < lanes.Count ? lanes[s] : lanes[lanes.Count - 1];
                double tl = slotTl[s];
                var pool = new List<CapBird>(); var pool2 = new List<CapBird>();
                foreach (var e in eff) if (!used.Contains(e)) (compat[e] ? pool : pool2).Add(e);
                int ci = PickReliever(pool, lane.Center, score, p);
                int mi = PickReliever(pool2, lane.Center, score, p);
                CapBird pick = ci >= 0 ? pool[ci] : null;
                double T = pick == null ? double.MaxValue : ReliefS(pick, lane.Center, p);
                if (mi >= 0)
                {
                    double T2 = ReliefS(pool2[mi], lane.Center, p);
                    if (T2 < T && T - tl > Tol) { pick = pool2[mi]; T = T2; }   // other ready loadout flies as is only to avoid a gap
                }
                if (pick == null)
                {
                    // rotation: the holder itself comes back, is serviced and relaunches
                    var h = slotHolder[s];
                    if (h == null) { if (anyBird) { gap = true; } continue; }
                    bool f;
                    string nm = ResolveLoadout(h.DeckUid, h.Type, loadoutByType, deckLos, ammo, p, out f);
                    var lo = FindLo(deckLos == null ? null : deckLos(h.DeckUid, h.Type), nm);
                    double rot = AirOpsMath.TurnaroundS(lo, lo, deckMode, 0, p.LaunchDelayS, 0);
                    if (tl <= rot - Tol) { gap = true; gapS = Math.Max(gapS, rot - tl); }
                    continue;
                }
                if (tl > T) continue;           // relief not needed yet
                used.Add(pick);
                var o = origOf[pick];
                var act = new CapAction { Uid = o.Uid, DeckUid = o.DeckUid, Slot = s, Type = o.Type, EtaS = T, Reason = "relief" };
                if (o.PendingKey != null) { act.Kind = "allow"; act.Key = o.PendingKey; act.Loadout = o.Loadout; }
                else if (o.Where == "deck_ready") { act.Kind = "launch"; act.Loadout = o.Loadout; }
                else { act.Kind = "order"; act.Loadout = pick.Loadout; }
                acts.Add(act);
                if (T - tl > Tol) { gap = true; gapS = Math.Max(gapS, T - tl); }
            }

            // reserve K: keep K prepared birds with the group loadout; abort our surplus requests (foreign ones are never touched)
            var ready = new List<CapBird>();
            foreach (var e in eff)
                if (!used.Contains(e) && compat[e] && (e.Where == "deck_ready" || e.PendingKey != null)) ready.Add(e);
            if (ready.Count < k)
            {
                var cand = new List<CapBird>();
                foreach (var e in eff)
                    if (!used.Contains(e) && !ready.Contains(e) && !(e.PendingKey != null)) cand.Add(e);
                cand.Sort((a, b) => a.ReadyInS.CompareTo(b.ReadyInS));
                for (int i = 0; i < cand.Count && ready.Count < k; i++)
                {
                    var o = origOf[cand[i]];
                    if (cand[i].Loadout == null) continue;
                    acts.Add(new CapAction { Kind = "order", Uid = o.Uid, DeckUid = o.DeckUid, Type = o.Type, Loadout = cand[i].Loadout, Slot = -1, EtaS = cand[i].ReadyInS, Reason = "reserve" });
                    ready.Add(cand[i]);
                }
            }
            else
            {
                var extra = new List<CapBird>();
                foreach (var e in ready) if (e.PendingKey != null && e.Ours) extra.Add(e);
                extra.Sort((a, b) => b.ReadyInS.CompareTo(a.ReadyInS));
                int surplus = ready.Count - k;
                for (int i = 0; i < extra.Count && surplus > 0; i++, surplus--)
                {
                    var o = origOf[extra[i]];
                    acts.Add(new CapAction { Kind = "abort", Uid = o.Uid, DeckUid = o.DeckUid, Type = o.Type, Key = o.PendingKey, Slot = -1, Reason = "surplus" });
                }
            }
            foreach (var e in eff)   // own requests with a loadout other than the group one are surplus when reserve is already full
                if (!used.Contains(e) && !compat[e] && e.PendingKey != null && e.Ours && ready.Count >= k)
                {
                    var o = origOf[e];
                    acts.Add(new CapAction { Kind = "abort", Uid = o.Uid, DeckUid = o.DeckUid, Type = o.Type, Key = o.PendingKey, Slot = -1, Reason = "loadout_changed" });
                }
            if (gap) acts.Add(new CapAction { Kind = "warn", Code = "continuity_gap", EtaS = gapS });
            return acts;
        }

        /// <summary>ADR-0040 D2: fastest speed step the fuel allows for the return to the deck. Steps are ascending by speed (afterburner excluded by the
        /// game layer). Condition for step s: Reserve + D - D*k(s) &gt;= max(V_cruise*FuelMarginS/3600; FuelFloorPct*MaxRange), k(s) = K(s)/K(cruise) from
        /// game data, else linear between 1 (cruise) and DashBurnFactor (top step). Only steps with speed &lt;= maxKn are tried (in flight: only step down).
        /// Returns the step id; the cruise step when nothing faster fits, there is no fuel data or no steps. note: why (for the log).</summary>
        public static int DashStep(IList<CapStep> steps, int cruiseStep, double distNm, double reserveNm, double maxRangeNm, double maxKn, CapParams p, out string note)
        {
            note = "cruise";
            CapStep cr = null;
            if (steps != null) foreach (var s in steps) if (s.Step == cruiseStep) cr = s;
            if (cr == null || distNm <= 0) { note = cr == null ? "no_steps" : "no_distance"; return cruiseStep; }
            if (reserveNm <= 0 && maxRangeNm <= 0) { note = "no_fuel_data"; return cruiseStep; }
            double keep = Math.Max(cr.Kn * p.FuelMarginS / 3600.0, p.FuelFloorPct * maxRangeNm);
            double top = cr.Kn;
            foreach (var s in steps) top = Math.Max(top, s.Kn);
            int best = cruiseStep; double bestKn = cr.Kn;
            foreach (var s in steps)
            {
                if (s.Kn <= cr.Kn + 1e-6 || s.Kn > maxKn + 1e-6 || s.Kn <= bestKn) continue;
                double k;
                if (s.K > 0 && cr.K > 0) k = s.K / cr.K;
                else k = top > cr.Kn ? 1 + (p.DashBurnFactor - 1) * (s.Kn - cr.Kn) / (top - cr.Kn) : 1;
                if (reserveNm + distNm - distNm * k >= keep) { best = s.Step; bestKn = s.Kn; note = "k=" + k.ToString("0.##", CultureInfo.InvariantCulture); }
            }
            if (best == cruiseStep) note = "cruise";
            return best;
        }

        private static void DashTest()
        {
            var p = new CapParams { FuelFloorPct = 0.15 };   // T-330: pinned old floor 15 %, margin 300 s, DashBurnFactor 2
            var st = new List<CapStep> { new CapStep { Step = 1, Kn = 300 }, new CapStep { Step = 2, Kn = 400 }, new CapStep { Step = 3, Kn = 500 }, new CapStep { Step = 4, Kn = 600 } };
            string n;
            // keep = max(400*300/3600 = 33, 300) = 300. Linear k: step3 = 1.667, step4 = 2.
            Near("dash plenty -> top", DashStep(st, 2, 100, 2000, 2000, 1e9, p, out n), 4);
            // reserve 380: step4 needs 380+100-200 = 280 < 300 no; step3: 380+100-166.7 = 313 >= 300 yes
            Near("dash tight -> middle", DashStep(st, 2, 100, 380, 2000, 1e9, p, out n), 3);
            Near("dash low -> cruise", DashStep(st, 2, 100, 250, 2000, 1e9, p, out n), 2);
            Near("dash floor kept", DashStep(st, 2, 100, 300, 2000, 1e9, p, out n), 2);   // 300+100-133 = 267 < 300 even for step 3 (k 1.667 -> 133 .. 266.7)
            Near("dash step down only", DashStep(st, 2, 100, 2000, 2000, 500, p, out n), 3);
            Near("dash step down to cruise", DashStep(st, 2, 100, 100, 2000, 500, p, out n), 2);
            Near("dash no fuel data", DashStep(st, 2, 100, 0, 0, 1e9, p, out n), 2);
            Near("dash no steps", DashStep(null, 2, 100, 2000, 2000, 1e9, p, out n), 2);
            // game data: k from K ratio: step4 costs 1.2x cruise only -> top even when linear would refuse
            var st2 = new List<CapStep> { new CapStep { Step = 1, Kn = 300, K = 1 }, new CapStep { Step = 2, Kn = 600, K = 1.2 } };
            Near("dash game k refused", DashStep(st2, 1, 100, 305, 2000, 1e9, p, out n), 1);   // 305+100-120 = 285 < keep 300
            Near("dash game k fits", DashStep(st2, 1, 100, 330, 2000, 1e9, p, out n), 2);   // 330+100-120 = 310 >= 300
        }

        /// <summary>Hours on station: (range - 2*dist) / V. &lt;= 0 means zone_far.</summary>
        /// <summary>0.27.8: fuel of an airborne aircraft from the game's numbers. reserveNm = path left before the return reserve (Aircraft.ActualRangeInKm =
        /// (RangeInKm - homeKm * massFactor) / consumption); without it the old source RangeOnMap (map units; the out-and-back radius, about half that
        /// path) is used. maxRangeNm = full tanks (_maxRangeInKm). fuelFrac = RangeInKm / _maxRangeInKm (the fuel the game burns; Ap._currentFuelMass
        /// stays at its spawn value in flight), -1 unknown. No data: reserve -1 and max 0 (the controller falls back); reserve without max: both 0.</summary>
        public static void GameFuel(double? actualKm, double? rangeOnMap, double mapUnitsPerKm, double? rangeKm, double? maxKm,
                                    out double reserveNm, out double maxRangeNm, out double fuelFrac)
        {
            const double KmPerNm = 1.852;
            Func<double?, bool> ok = v => v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) && Math.Abs(v.Value) < 1e6;
            if (ok(actualKm)) reserveNm = Math.Max(0, actualKm.Value) / KmPerNm;
            else if (ok(rangeOnMap) && mapUnitsPerKm > 0) reserveNm = Math.Max(0, rangeOnMap.Value / mapUnitsPerKm / KmPerNm);
            else reserveNm = -1;
            // reserve <= 0 (already cannot make it) keeps MaxRangeNm: the strictest case must use the floor model, not the fallback
            maxRangeNm = maxKm.HasValue && maxKm.Value > 0 && reserveNm >= 0 ? maxKm.Value / KmPerNm : 0;
            if (maxRangeNm <= 0) reserveNm = 0;
            fuelFrac = ok(rangeKm) && maxKm.HasValue && maxKm.Value > 0 ? Math.Max(0, Math.Min(1, rangeKm.Value / maxKm.Value)) : -1;
        }

        /// <summary>Correction 2026-10-22 (ADR-0040 D5, customer: "take the game's ring, do not guess"): the range ring the game draws on the map around a
        /// selected aircraft is ObjectBase.RangeOnMap (SeapowerUI.RangeOnMapViewModel, radius RangeOnMap * 0.000604752 deg = RangeOnMap * 0.036285 nm), set by
        /// Aircraft.UpdateFuelConsumption = (RangeInKm - home * n) / (n + c): how far the aircraft can still fly out on the current flight mode and come back
        /// home; the game's own Bingo fires when it is ~0. The CAP reserve ("path left before the return", ADR-0040 §5) is the ring out and back: 2 * ring
        /// (at cruise, c = n, it equals the old ActualRangeInKm). ringNm &lt; 0 = unknown: -1.</summary>
        /// <summary>T-197: fuel percent for the window and the log: the game's fuel fraction (0..1) if known, else reserve/max; -1 = unknown.</summary>
        public static double FuelPct(double fuelFrac, double reserveNm, double maxRangeNm)
        {
            if (fuelFrac >= 0 && !double.IsNaN(fuelFrac) && !double.IsInfinity(fuelFrac)) return 100.0 * Math.Min(1.0, fuelFrac);
            return maxRangeNm > 0 ? 100.0 * reserveNm / maxRangeNm : -1;
        }

        public static double RingReserveNm(double ringNm) { return ringNm >= 0 && !double.IsNaN(ringNm) && !double.IsInfinity(ringNm) ? 2 * ringNm : -1; }

        /// <summary>T-337 (ADR-0131): last-resort fuel floor (share of the tank) used ONLY when the way-home reserve cannot be computed.</summary>
        public const double HardFloorPct = 0.10;

        /// <summary>T-337 (ADR-0131): the way-home reserve is usable: range known and reserve a finite number >= 0 (ReserveNm = -1 / NaN / MaxRange 0 = unknown).</summary>
        public static bool ReserveKnown(CapBird b)
        {
            return b != null && b.MaxRangeNm > 0 && !double.IsNaN(b.MaxRangeNm) && !double.IsInfinity(b.MaxRangeNm)
                && b.ReserveNm >= 0 && !double.IsNaN(b.ReserveNm) && !double.IsInfinity(b.ReserveNm);
        }

        /// <summary>T-337 (ADR-0131): any usable fuel fraction (live or frozen spawn value), 0..1.</summary>
        public static bool AnyFracValid(CapBird b)
        {
            return b != null && !double.IsNaN(b.FuelFrac) && b.FuelFrac >= 0 && b.FuelFrac <= 1;
        }

        /// <summary>T-315: the bird has a live game fuel fraction (0..1).</summary>
        public static bool FracFuelKnown(CapBird b)
        {
            return b != null && b.FuelFracLive && !double.IsNaN(b.FuelFrac) && b.FuelFrac >= 0 && b.FuelFrac <= 1;
        }

        /// <summary>T-330 (ADR-0128): ring-based safety margin (nm): RingMarginNm + the flight of the relief (patrol speed * reliefS).</summary>
        public static double RingMarginNm(double patrolKn, double reliefS, CapParams p)
        {
            return Math.Max(0, p.RingMarginNm) + (reliefS > 0 ? (patrolKn > 0 ? patrolKn : 300) * reliefS / 3600.0 : 0);
        }

        /// <summary>T-315: fuel fraction * clean game range (nm), for the log only; -1 if either is unknown.</summary>
        public static double FracReserveNm(double fuelFrac, double gameMaxNm)
        {
            return fuelFrac >= 0 && gameMaxNm > 0 && !double.IsNaN(fuelFrac) ? fuelFrac * gameMaxNm : -1;
        }

        /// <summary>T-337 (ADR-0131 supplement): a fuel leave (margin / floor / hard) is not postponed by combat or hostile_near when the live fuel fraction is
        /// at or below HardFloorPct (10 %); also the combat does not restart the FuelHoldS hold then.</summary>
        public static bool FuelOverridesPostpone(CapBird b) { return FracFuelKnown(b) && b.FuelFrac <= HardFloorPct; }

        /// <summary>T-337 E (live r34): how long the fuel threshold must hold before "fuel": FuelMarginHoldS for by=margin when the live fraction is
        /// at or below HardFloorPct (the reserve really is short, not a ring collapsed by a burst); FuelHoldS for everything else.</summary>
        public static double FuelHoldFor(CapBird b, string lowBy, CapParams p)
        {
            return lowBy == "margin" && FuelOverridesPostpone(b) ? p.FuelMarginHoldS : p.FuelHoldS;
        }

        public static double FuelHoldFor(CapBird b, CapParams p) { return FuelHoldFor(b, FuelLowBy(b, 0, p), p); }

        /// <summary>T-315: the bird is in combat (Postpone gives "combat").</summary>
        public static bool InCombat(CapBird b, CapParams p) { return Postpone(b, double.MaxValue, p) == "combat"; }

        /// <summary>T-315 (ADR-0122): floor | margin | ring | null - why the fuel is low (without the hold of FuelHoldS).</summary>
        public static string FuelLowBy(CapBird b, double reliefS, CapParams p)
        {
            if (FracFuelKnown(b))
            {
                if (p.FuelFloorPct > 0 && b.FuelFrac <= p.FuelFloorPct) return "floor";   // ADR-0131: 0 = floor off, only the reserve rule
                if (ReserveKnown(b)) return b.ReserveNm <= RingMarginNm(b.PatrolKn, reliefS, p) ? "margin" : (b.FuelFrac <= 0 ? "floor" : null);
                return b.FuelFrac <= HardFloorPct ? "hard" : null;   // reserve unknown: never fly dry
            }
            if (b.MaxRangeNm > 0 && b.ReserveNm >= 0 && !double.IsNaN(b.ReserveNm)) return b.ReserveNm <= FuelThresholdNm(b.PatrolKn, reliefS, b.MaxRangeNm, p) ? "ring" : null;
            return AnyFracValid(b) && b.FuelFrac <= HardFloorPct ? "hard" : null;
        }

        /// <summary>The fuel threshold of ADR-0040 §5 / ADR-0122 is reached (without the hold of FuelHoldS).</summary>
        public static bool FuelLow(CapBird b, double reliefS, CapParams p)
        {
            return FuelLowBy(b, reliefS, p) != null;
        }

        private static void FracFuelTest()
        {
            var p = new CapParams { FuelFloorPct = 0.05 };   // ADR-0131: the default floor is off; these cases pin the old 5 %
            string pp;
            var b = new CapBird { AamNow = 7, AamStart = 8, FuelFrac = 0.405, FuelFracLive = true, ReserveNm = RingReserveNm(133.1), MaxRangeNm = 1864, PatrolKn = 420, FuelLowS = 61 };
            True("frac: log case no low", FuelLowBy(b, 0, p) == null && LeaveReason(b, 0, 99, p, out pp) == null);
            double li = FuelLeaveInS(b, p); True("frac: leave in ~2000", Math.Abs(li - 2000) < 10);
            b.FuelFrac = 0.04; b.ReserveNm = RingReserveNm(50);
            True("frac: floor", FuelLowBy(b, 0, p) == "floor");
            b.FuelLowS = 60; True("frac: floor held", LeaveReason(b, 0, 99, p, out pp) == "fuel");
            b.FuelLowS = 30; True("frac: floor not held", LeaveReason(b, 0, 99, p, out pp) == null);
            True("frac: floor leave now", FuelLeaveInS(b, p) == 0);
            b.FuelFrac = 0.35; b.ReserveNm = RingReserveNm(2.5);
            True("frac: margin", FuelLowBy(b, 0, p) == "margin");
            b.FuelFracLive = false; b.FuelFrac = 0.405; b.ReserveNm = RingReserveNm(40);   // T-330: threshold now 5 % of 1864 = 93 nm
            True("frac: no live fraction -> ring", FuelLowBy(b, 0, p) == "ring");
            var u = new CapBird { PatrolKn = 420 }; True("frac: unknown fuel", FuelLeaveInS(u, p) >= 1e9);
            Near("ring margin", RingMarginNm(420, 0, p), 5); Near("ring margin relief", RingMarginNm(420, 60, p), 12);
            Near("frac reserve", FracReserveNm(0.405, 1864), 754.92, 0.01); Near("frac reserve unknown", FracReserveNm(-1, 1864), -1);
            // relief planner: no immediate relief for the log case
            True("frac: in combat", InCombat(new CapBird { AamNow = 4, Engaged = true, TargetKind = 1 }, p) && !InCombat(new CapBird { AamNow = 0, Engaged = true }, p));
        }

        private static void RingFuelTest()
        {
            var p = new CapParams { FuelFloorPct = 0.15 };   // T-330: the no-fraction scenarios keep the old floor
            Near("fuel pct: game fraction", FuelPct(0.92, 300, 2000), 92); Near("fuel pct: no fraction", FuelPct(-1, 500, 2000), 25); Near("fuel pct: no max", FuelPct(-1, 500, 0), -1);
            Near("ring x2", RingReserveNm(150), 300); Near("ring unknown", RingReserveNm(-1), -1);
            // live 0.30.0 19:44: an afterburner burst collapsed the ring (136 nm, path 272) with 85 % fuel: below the threshold, but only for a moment
            var b = new CapBird { AamNow = 7, AamStart = 8, ReserveNm = RingReserveNm(136), MaxRangeNm = 1867, PatrolKn = 420, FuelLowS = 0 };
            string pp;
            True("ring: low now", FuelLow(b, 0, p));
            True("ring: a burst does not send it home", LeaveReason(b, 0, 99, p, out pp) == null);
            b.FuelLowS = p.FuelHoldS - 1; True("ring: still within the hold", LeaveReason(b, 0, 99, p, out pp) == null);
            b.FuelLowS = p.FuelHoldS; True("ring: held below the threshold -> fuel", LeaveReason(b, 0, 99, p, out pp) == "fuel");
            b.FuelLowS = -1; True("no hold tracking (fallback model): at once", LeaveReason(b, 0, 99, p, out pp) == "fuel");
            b.ReserveNm = RingReserveNm(800); b.FuelLowS = 0; True("ring: cruise, no leave", !FuelLow(b, 0, p) && LeaveReason(b, 0, 99, p, out pp) == null);
        }

        public static double ZoneHours(double rangeNm, double distNm, double patrolKn)
        {
            return patrolKn <= 0 ? 0 : (rangeNm - 2 * distNm) / patrolKn;
        }

        // ================= selftest =================
        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }
        private static void Near(string n, double a, double b, double eps = 1e-6) { if (Math.Abs(a - b) > eps && _err == null) _err = n + ": " + a.ToString(CultureInfo.InvariantCulture) + " != " + b.ToString(CultureInfo.InvariantCulture); }

        private static readonly Dictionary<string, CapAmmo> Ammo = BuildAmmo();
        private static Dictionary<string, CapAmmo> BuildAmmo()
        {
            var d = new Dictionary<string, CapAmmo>();
            Action<string, string, string, string, double, double> add = (n, t, tt, st, r, fuel) =>
                d[n] = new CapAmmo { Name = n, Type = t, TargetType = tt, SecondaryTargetType = st, MaxLaunchRangeNm = r, FuelKg = fuel };
            add("AIM-54", "Missile", "AAW", null, 100, 0);
            add("AIM-7", "Missile", "AAW", null, 25, 0);
            add("AIM-9", "Missile", "AAW", null, 10, 0);
            add("R-77", "Missile", "Surface", "AAW", 45, 0);      // AAW in the secondary target type
            add("Mk82", "Bomb", "Surface", null, 0, 0);
            add("Maverick", "Missile", "Surface", null, 12, 0);
            add("Tank", "Tank", "None", null, 0, 1500);
            add("ALQ", "Pod", "None", null, 0, 0);
            add("M61", "Projectile", "AAW", "Surface", 2, 0);
            return d;
        }
        private static CapAmmo Am(string n) { CapAmmo a; return n != null && Ammo.TryGetValue(n, out a) ? a : null; }

        private static LoadoutOption L(string name, params object[] kv)
        {
            var lo = new LoadoutOption { Name = name, Display = name, Capacity = 8, Available = true, Weapons = new Dictionary<string, int>() };
            for (int i = 0; i + 1 < kv.Length; i += 2) lo.Weapons[(string)kv[i]] = (int)kv[i + 1];
            return lo;
        }

        private static List<LoadoutOption> F14()
        {
            return new List<LoadoutOption> {
                L("AirToAirLongRange", "AIM-54", 4, "AIM-7", 2, "AIM-9", 2, "Tank", 2),
                L("AirToAir", "AIM-7", 4, "AIM-9", 4),
                L("Strike", "Mk82", 6, "AIM-9", 2) };
        }

        private static void Admission()
        {
            Func<string, CapAmmo> am = Am;
            True("aam missile aaw", IsAam(Am("AIM-9")));
            True("aam secondary aaw", IsAam(Am("R-77")));
            True("gun not aam", !IsAam(Am("M61")));
            True("bomb not aam", !IsAam(Am("Mk82")));
            True("null not aam", !IsAam(null));
            True("class 12", AamClass(12) == "SR"); True("class 12.1", AamClass(12.1) == "MR");
            True("class 45", AamClass(45) == "MR"); True("class 45.1", AamClass(45.1) == "LR");
            int aam;
            True("cap name ci", IsCapLoadout(L("airtoairlongrange", "AIM-9", 2, "Mk82", 2), am, out aam) && aam == 2);
            True("cap default all aam", IsCapLoadout(L("Default", "AIM-7", 4, "Tank", 1, "ALQ", 1), am, out aam) && aam == 4);
            True("strike with aam no", !IsCapLoadout(L("Strike", "Mk82", 4, "AIM-9", 2), am, out aam));
            True("no aam no", !IsCapLoadout(L("AirToAir", "Mk82", 2), am, out aam));
            True("gun only no", !IsCapLoadout(L("Default", "M61", 1), am, out aam));
            True("f14", TypeAdmission("Plane", F14(), am) == null);
            True("f14 stol", TypeAdmission("STOL", F14(), am) == null);
            True("f14 vtol", TypeAdmission("VTOL", F14(), am) == null);
            True("f14 legacy aircraft", TypeAdmission("Aircraft", F14(), am) == null);
            True("f14 none", TypeAdmission("None", F14(), am) == "no_aam");
            True("f14 null", TypeAdmission(null, F14(), am) == "no_aam");
            True("f14 heli", TypeAdmission("Helicopter", F14(), am) == "helicopter");
            True("f15", TypeAdmission("Plane", new List<LoadoutOption> { L("AirToAir", "AIM-7", 4, "AIM-9", 4) }, am) == null);
            True("su35", TypeAdmission("Plane", new List<LoadoutOption> { L("AirToAirLongrange", "R-77", 6, "AIM-9", 2) }, am) == null);
            True("synthetic default", TypeAdmission("VTOL", new List<LoadoutOption> { L("Default", "AIM-9", 2, "Tank", 1) }, am) == null);
            var strike = new List<LoadoutOption> { L("Strike", "Mk82", 6, "AIM-9", 2), L("CAS", "Maverick", 4, "AIM-9", 2) };
            True("a7e", TypeAdmission("Plane", strike, am) == "self_defence");
            True("su24", TypeAdmission("Plane", new List<LoadoutOption> { L("Strike", "Mk82", 8, "AIM-9", 2) }, am) == "self_defence");
            True("ah1t", TypeAdmission("Helicopter", new List<LoadoutOption> { L("AirToAir", "AIM-9", 2) }, am) == "helicopter");
            True("gun only type", TypeAdmission("Plane", new List<LoadoutOption> { L("Default", "M61", 1) }, am) == "no_aam");
            True("no weapons type", TypeAdmission("Plane", new List<LoadoutOption>(), am) == "no_aam");
            // loadout choice
            var los = F14();
            Near("score order", LoadoutScore(los[0], am) > LoadoutScore(los[1], am) ? 1 : 0, 1);
            Near("score strike 0", LoadoutScore(los[2], am), 0);
            var p = new CapParams { AirOnly = false, FuelFloorPct = 0.05 };   // ADR-0131: explicit 5 % floor (default off); T-175: the legacy postponement rules; the air-only ones are in Roe()
            True("pick lr", PickLoadout(los, am, p, null) == 0);
            True("pick skip lr", PickLoadout(los, am, p, new HashSet<string> { "AirToAirLongRange" }) == 1);
            True("pick none", PickLoadout(new List<LoadoutOption> { los[2] }, am, p, null) == -1);
            var tie = new List<LoadoutOption> { L("AirToAirIntercept", "AIM-7", 2), L("AirToAir", "AIM-7", 2), L("Zeta", "AIM-7", 2) };
            True("tie break order", tie[PickLoadout(tie, am, p, null)].Name == "AirToAir");
            tie.RemoveAt(1);
            True("tie break order 2", tie[PickLoadout(tie, am, p, null)].Name == "AirToAirIntercept");
            True("tie by name", PickLoadout(new List<LoadoutOption> { L("B", "AIM-7", 2), L("A", "AIM-7", 2) }, am, p, null) == 1);
            // early leave
            True("floor 8", MissileFloor(8, .25) == 2); True("floor 6", MissileFloor(6, .25) == 1);
            True("floor 4", MissileFloor(4, .25) == 1); True("floor 2", MissileFloor(2, .25) == 0);
            var b = new CapBird { AamStart = 8, AamNow = 1 };
            True("leave 1/8", MissileLeave(b, p) == "missiles");
            b.AamNow = 3; True("stay 3/8", MissileLeave(b, p) == null);
            b.LongStart = 4; b.LongNow = 0; True("lr ignored by default", MissileLeave(b, p) == null);
            p.NeedLongRange = true; True("lr needed", MissileLeave(b, p) == "long_range");
            p.NeedLongRange = false;
            Near("fuel 175", FuelThresholdNm(420, 1200, 1000, p), 175);
            Near("fuel floor 300", FuelThresholdNm(420, 1200, 6000, p), 300);
            var c = new CapBird { AamStart = 8, AamNow = 6, Engaged = true };
            True("postpone combat", Postpone(c, 99, p) == "combat");
            c.Engaged = false;
            True("postpone near 9", Postpone(c, 9, p) == "hostile_near"); True("no postpone 11", Postpone(c, 11, p) == null);
            c.AamNow = 0; True("no postpone empty", Postpone(c, 5, p) == null);
            string pp;
            var g = new CapBird { AamStart = 8, AamNow = 8, ReserveNm = 1000, MaxRangeNm = 2000, PatrolKn = 420, GameBingo = true, GameRtb = true, GameWinchester = true };
            True("bingo first", LeaveReason(g, 0, 99, p, out pp) == "game_bingo");
            g.GameBingo = false; True("winchester", LeaveReason(g, 0, 99, p, out pp) == "game_winchester");
            g.GameWinchester = false; True("rtb", LeaveReason(g, 0, 99, p, out pp) == "game_rtb");
            g.GameRtb = false; True("none", LeaveReason(g, 0, 99, p, out pp) == null && pp == null);
            g.ReserveNm = 90; True("fuel", LeaveReason(g, 0, 99, p, out pp) == "fuel");
            g.Engaged = true; True("fuel postponed", LeaveReason(g, 0, 99, p, out pp) == null && pp == "combat");
            g.Engaged = false; g.AamNow = 1; True("missiles", LeaveReason(g, 0, 99, p, out pp) == "missiles");
        }

        private static CapZone Zone40x20()
        {
            return new CapZone { Rect = new[] { 10.0, 20.0, 10.0 + 40 / 60.0, 20.0 + 20 / 60.0 } };
        }

        private static void LanesTest()
        {
            var p = new CapParams(); var z = Zone40x20();
            Near("dist inside", DistToZoneNm(new GeoPt(10.2, 20.1), z), 0);
            Near("dist edge", DistToZoneNm(new GeoPt(10.0 - 3 / 60.0, 20.1), z), 3, 1e-6);
            Near("dist corner", DistToZoneNm(new GeoPt(10.0 - 3 / 60.0, 20.0 - 4 / 60.0), z), 5, 1e-6);
            for (int n = 1; n <= 3; n++)
            {
                var ls = Lanes(z, n, p);
                True("lanes count " + n, ls.Count == n);
                double strip = 20.0 / n, sep = strip / 2;   // T-235: q = 1 -> two legs d = strip / 2 apart
                for (int i = 0; i < ls.Count; i++)
                {
                    var l = ls[i];
                    True("lane slot " + n + i, l.Slot == i && !l.Orbit && l.Track.Count == 4);
                    foreach (var pt in l.Track) True("lane inside " + n + i, DistToZoneNm(pt, z) == 0);
                    // long side is north-south (40 nm): legs run along latitude
                    Near("lane inset " + n + i, (l.Track[0].Lat - z.Rect[0]) * 60, 4, 1e-6);          // min(5, 0.1*40)
                    Near("lane end " + n + i, (z.Rect[2] - l.Track[1].Lat) * 60, 4, 1e-6);
                    Near("lane sep " + n + i, (l.Track[2].Lon - l.Track[1].Lon) * 60, sep, 1e-6);
                    Near("lane first leg " + n + i, (l.Track[0].Lon - z.Rect[1]) * 60, i * strip + sep / 2, 1e-6);
                    Near("lane centre " + n + i, (l.Center.Lon - z.Rect[1]) * 60, (i + 0.5) * strip, 1e-6);
                }
            }
            // inset capped at 5 nm on a long zone
            var big = new CapZone { Rect = new[] { 10.0, 20.0, 10.0 + 100 / 60.0, 20.0 + 30 / 60.0 } };
            Near("inset cap", (Lanes(big, 1, p)[0].Track[0].Lat - 10.0) * 60, 5, 1e-6);
            // east-west long side
            var ew = new CapZone { Rect = new[] { 10.0, 20.0, 10.0 + 20 / 60.0, 20.0 + 40 / 60.0 } };
            var le = Lanes(ew, 2, p);
            True("ew legs along lon", Math.Abs(le[0].Track[0].Lat - le[0].Track[1].Lat) < 1e-9 && le[0].Track[1].Lon > le[0].Track[0].Lon);
            // narrow zone -> orbit
            var nar = new CapZone { Rect = new[] { 10.0, 20.0, 10.0 + 30 / 60.0, 20.0 + 3 / 60.0 } };
            var lo = Lanes(nar, 1, p, 50);   // T-235: the orbit only when it covers the strip (Rc 50)
            True("narrow zone, short cover: racetrack", !Lanes(nar, 1, p, 5)[0].Circle);
            True("narrow circle (T-244)", lo[0].Circle && lo[0].Track.Count >= 4);
            // ADR-0040 §4 (fix 2026-10-22): narrow STRIPS of a normal zone keep racetracks; legs min(strip, 4) apart, inside their strip
            var six = Lanes(z, 6, p);   // 20/6 = 3.3 nm strips
            True("narrow strips racetrack", !six[0].Orbit && six[0].Track.Count == 4);
            Near("narrow strips sep", (six[0].Track[2].Lon - six[0].Track[1].Lon) * 60, 20.0 / 12, 1e-6);
            // live 0.30.0: CAP-1 6.1 x 20.5 nm (long side north-south), N = 2 -> two racetracks covering the zone, not two circles
            var live = new CapZone { Rect = new[] { 11.814392588663512, 44.178933035385555, 12.156385091219457, 44.28129813819142 } };
            var ll = Lanes(live, 2, p);
            True("live zone racetracks", ll.Count == 2 && !ll[0].Orbit && !ll[1].Orbit && ll[0].Track.Count == 4);
            double legNm = (ll[0].Track[1].Lat - ll[0].Track[0].Lat) * 60, zoneNm = (live.Rect[2] - live.Rect[0]) * 60;
            True("live legs cover the lane " + legNm, legNm >= 0.75 * zoneNm);
            foreach (var l in ll) foreach (var pt in l.Track) True("live lane inside", DistToZoneNm(pt, live) == 0);
            True("lanes apart", ll[0].Track[2].Lon <= ll[1].Track[0].Lon + 1e-9);
            True("zone side < 4 nm: circle", Lanes(new CapZone { Rect = new[] { 10.0, 20.0, 10.0 + 30 / 60.0, 20.0 + 3.5 / 60.0 } }, 2, p, 50)[0].Circle);
            Near("cover nm min", CoverNm(new[] { 25.0, 10.0, 0.0 }, p), 5, 1e-9); Near("cover nm fallback", CoverNm(new double[0], p), 10, 1e-9); Near("cover nm floor", CoverNm(new[] { 1.0 }, p), 1, 1e-9);
            // anchored rect
            var az = new CapZone { Anchor = "unit", AnchorUid = 5, Rect = (double[])z.Rect.Clone() };
            var ship0 = new GeoPt(10.0, 19.8); az.OffLat = z.Rect[0] + 20 / 60.0 - ship0.Lat; az.OffLon = z.Rect[1] + 10 / 60.0 - ship0.Lon;
            var r0 = AnchoredRect(az, ship0);
            Near("anchor same", r0[0], z.Rect[0], 1e-9);
            var r1 = AnchoredRect(az, new GeoPt(ship0.Lat, ship0.Lon + 10 / 60.0));
            Near("anchor moved", (r1[1] - r0[1]) * 60, 10, 1e-6);
            Near("anchor size", (r1[2] - r1[0]) * 60, 40, 1e-6); Near("anchor width", (r1[3] - r1[1]) * 60, 20, 1e-6);
            var r2 = AnchoredRect(az, null);
            True("anchor lost", az.AnchorLost && r2[1] == r1[1]);
            AnchoredRect(az, ship0); True("anchor found", !az.AnchorLost);
            var gz = Zone40x20(); True("geo fixed", AnchoredRect(gz, new GeoPt(50, 50)) == gz.Rect);
            // leash
            True("leash 24.9", !BeyondLeash(new GeoPt(10.0 - 24.9 / 60.0, 20.1), z, p));
            True("leash 25.1", BeyondLeash(new GeoPt(10.0 - 25.1 / 60.0, 20.1), z, p));
        }

        private static CapContact Ct(int id, double nmNorthOfZoneTop, bool hostile = true, bool missile = false)
        {
            var z = Zone40x20();
            return new CapContact { Id = id, Lat = z.Rect[2] + nmNorthOfZoneTop / 60.0, Lon = 20.1, Hostile = hostile, Missile = missile };
        }

        private static void Threat()
        {
            var p = new CapParams(); var z = Zone40x20(); CapContact nr;
            True("p default", p.ThreatNm == 80 && p.ThreatClearMarginNm == 10 && p.ThreatClearS == 60);
            True("threat 79", AirThreat(new[] { Ct(1, 79) }, z, null, p.ThreatNm, out nr) && nr.Id == 1);
            True("no threat 81", !AirThreat(new[] { Ct(1, 81) }, z, null, p.ThreatNm, out nr));
            True("inside zone", AirThreat(new[] { new CapContact { Id = 2, Lat = 10.1, Lon = 20.1, Hostile = true } }, z, null, p.ThreatNm, out nr));
            var deck = new GeoPt(5.0, 20.0);
            var near = new CapContact { Id = 3, Lat = 5.0 + 79 / 60.0, Lon = 20.0 - 100 / 60.0, Hostile = true };   // far from zone
            True("far from zone", !AirThreat(new[] { near }, z, null, 80, out nr));
            near.Lon = 20.0; near.Lat = 5.0 - 79 / 60.0;
            True("79 from deck", AirThreat(new[] { near }, z, deck, 80, out nr));
            True("unidentified 30", !AirThreat(new[] { Ct(4, 30, false) }, z, null, 80, out nr));
            True("missile 50", AirThreat(new[] { Ct(5, 50, true, true) }, z, null, 80, out nr) && nr.Id == 5);
            True("nearest picked", AirThreat(new[] { Ct(6, 70), Ct(7, 40), Ct(8, 60) }, z, null, 80, out nr) && nr.Id == 7);
            True("empty", !AirThreat(new List<CapContact>(), z, null, 80, out nr) && nr == null);

            var latch = new CapThreatLatch();
            True("latch off", !latch.Update(new CapContact[0], z, null, 0, p) && !latch.On);
            True("latch on 70", latch.Update(new[] { Ct(1, 70) }, z, null, 1, p) && latch.On && latch.ThreatUid == 1);
            Near("latch nm", latch.ThreatNm, 70);
            True("holds at 85", !latch.Update(new[] { Ct(1, 85) }, z, null, 100, p) && latch.On);
            True("holds long at 85", !latch.Update(new[] { Ct(1, 85) }, z, null, 500, p) && latch.On);
            True("91 starts timer", !latch.Update(new[] { Ct(1, 91) }, z, null, 1000, p) && latch.On);
            True("91 at 59s on", !latch.Update(new[] { Ct(1, 91) }, z, null, 1059, p) && latch.On);
            True("89 resets timer", !latch.Update(new[] { Ct(1, 89) }, z, null, 1059.5, p) && latch.On);
            True("91 restarts", !latch.Update(new[] { Ct(1, 91) }, z, null, 1060, p) && latch.On);
            True("still on 1119", !latch.Update(new CapContact[0], z, null, 1119, p) && latch.On);
            True("off at 60 s", latch.Update(new CapContact[0], z, null, 1120, p) && !latch.On);
            True("on again", latch.Update(new[] { Ct(9, 79.9) }, z, null, 1121, p) && latch.On);   // exactly ThreatNm
            // gap
            True("gap empty slot", HasGap(1, 2, new double[0], new double[0]));
            True("gap late relief", HasGap(2, 2, new[] { 900.0 }, new[] { 600.0 }));
            True("no gap in time", !HasGap(2, 2, new[] { 500.0 }, new[] { 600.0 }));
            True("no gap none leaving", !HasGap(2, 2, new double[0], new double[0]));
        }

        private static CapBird Air(int uid, int slot, double reserveNm, int aam = 8)
        {
            return new CapBird { Uid = uid, Slot = slot, Where = "zone", Type = "F-14A", Loadout = "AirToAirLongRange", AamStart = 8, AamNow = aam,
                                 ReserveNm = reserveNm, MaxRangeNm = 100, PatrolKn = 400, CruiseKn = 450, Lat = 10.3, Lon = 20.15, Ours = true };
        }

        private static CapBird Deck(int uid, string where, double readyS, string key = null, string loadout = "AirToAirLongRange")
        {
            return new CapBird { Uid = uid, DeckUid = 100, Where = where, Type = "F-14A", Loadout = loadout, ReadyInS = readyS, PendingKey = key,
                                 CruiseKn = 450, Lat = 9.0, Lon = 20.0, Ours = true, MaxRangeNm = 2000 };
        }

        private static CapAction Find(List<CapAction> a, string kind, int uid = -1)
        {
            foreach (var x in a) if (x.Kind == kind && (uid < 0 || x.Uid == uid)) return x;
            return null;
        }

        private static void Relief()
        {
            var p = new CapParams(); var z = Zone40x20();
            var lanes = Lanes(z, 2, p);
            var byType = new Dictionary<string, string> { { "F-14A", "AirToAirLongRange" } };
            Func<int, string, IList<LoadoutOption>> dl = (d, t) => F14();
            Func<string, CapAmmo> am = Am;
            bool gap = false; double gapS = 0;
            Func<List<CapBird>, int, int, string, List<CapAction>> plan = (m, n, k, mode) => PlanRelief(m, n, k, lanes, byType, dl, am, mode, 0, p, out gap, out gapS);

            // ReliefS / PickReliever
            var r1 = Deck(1, "deck_ready", 0); var r2 = Deck(2, "deck_prep", 100); var r3 = Deck(3, "deck_prep", 50);
            double t1 = ReliefS(r1, lanes[0].Center, p);
            True("reliefs", t1 > p.LaunchDelayS && Math.Abs(t1 - (p.LaunchDelayS + AswMath.DistNm(new GeoPt(9.0, 20.0), lanes[0].Center) / 450 * 3600)) < 1e-6);
            True("pick ready first", PickReliever(new[] { r2, r1, r3 }, lanes[0].Center, null, p) == 1);
            True("pick sooner", PickReliever(new[] { r2, r3 }, lanes[0].Center, null, p) == 1);
            var s1 = Deck(4, "deck_prep", 50); var s2 = Deck(5, "deck_prep", 50);
            True("pick score", PickReliever(new[] { s1, s2 }, lanes[0].Center, b => b.Uid == 5 ? 2 : 1, p) == 1);
            s2.Lat = 10.0; s1.Lat = 9.0;
            True("pick path", PickReliever(new[] { s1, s2 }, lanes[0].Center, null, p) == 1);
            True("pick none", PickReliever(new List<CapBird>(), lanes[0].Center, null, p) == -1);

            // ZoneHours
            Near("zone hours", ZoneHours(2000, 200, 400), 4);
            True("zone far", ZoneHours(300, 200, 400) <= 0);

            // plan: relief not needed yet (tl large); K=1, surplus request aborted, foreign untouched
            var a1 = Air(11, 0, 1500); var a2 = Air(12, 1, 1500);
            var foreign = Deck(9, "deck_prep", 700, "foreign"); foreign.Ours = false;
            var acts = plan(new List<CapBird> { a1, a2, Deck(1, "deck_ready", 0), Deck(2, "deck_prep", 600, "k2"), foreign }, 2, 1, "Realistic");
            True("early: no launch", Find(acts, "launch") == null && Find(acts, "allow") == null && Find(acts, "order") == null);
            True("early: abort own surplus", Find(acts, "abort", 2) != null && Find(acts, "abort", 2).Key == "k2");
            True("foreign untouched", Find(acts, "abort", 9) == null);
            True("early: no gap", !gap && Find(acts, "warn") == null);

            // plan: relief launches in time (tl <= T), arrives before leaving
            double T = ReliefS(Deck(1, "deck_ready", 0), lanes[0].Center, p);
            a1.ReserveNm = ((T - 0.5) + p.FuelMarginS) * 400 / 3600.0;
            acts = plan(new List<CapBird> { a1, a2, Deck(1, "deck_ready", 0), Deck(2, "deck_ready", 0) }, 2, 1, "Realistic");
            var la = Find(acts, "launch", 1);
            True("planned launch", la != null && la.Slot == 0 && la.Reason == "relief" && !gap);
            Near("planned eta", la == null ? -1 : la.EtaS, T, 1e-6);
            a1.ReserveNm = ((T + 60) + p.FuelMarginS) * 400 / 3600.0;
            acts = plan(new List<CapBird> { a1, a2, Deck(1, "deck_ready", 0) }, 2, 1, "Realistic");
            True("not yet", Find(acts, "launch") == null);
            // T-187: a foreign ready deck task (the game's own FlightDeck_ReadyUpTasks) is allowed by its key, never re-ordered
            a1.ReserveNm = ((T - 0.5) + p.FuelMarginS) * 400 / 3600.0;
            var fr = Deck(9, "deck_ready", 0, "task:9"); fr.Ours = false;
            acts = plan(new List<CapBird> { a1, a2, fr }, 2, 1, "Realistic");
            var fa = Find(acts, "allow", 9);
            True("foreign ready: allow by key", fa != null && fa.Key == "task:9" && Find(acts, "launch", 9) == null && Find(acts, "order", 9) == null);

            // T-179: an extra airborne bird without a slot (every slot held) does not cover the holder that leaves: the relief launches as if it were not there
            var ex = Air(13, 0, 1500); ex.Slot = -1;
            a1.ReserveNm = ((T - 0.5) + p.FuelMarginS) * 400 / 3600.0;
            acts = plan(new List<CapBird> { a1, a2, ex, Deck(1, "deck_ready", 0) }, 2, 1, "Realistic");
            True("extra bird is not a relief", Find(acts, "launch", 1) != null && Find(acts, "launch", 1).Slot == 0);
            a1.ReserveNm = 1500;

            // missile leave: immediate launch of the best reliever
            a1.ReserveNm = 1500; a1.AamNow = 1;
            acts = plan(new List<CapBird> { a1, a2, Deck(2, "deck_prep", 300, "k2"), Deck(1, "deck_ready", 0) }, 2, 1, "Realistic");
            True("missile: launch ready", Find(acts, "launch", 1) != null && Find(acts, "allow") == null);
            True("missile: gap warned", gap && Find(acts, "warn") != null && Find(acts, "warn").Code == "continuity_gap");
            a1.AamNow = 8;

            // prepped request is allowed instead of a new order
            a1.AamNow = 1;
            acts = plan(new List<CapBird> { a1, a2, Deck(2, "deck_prep", 300, "k2") }, 2, 1, "Realistic");
            True("allow prepped", Find(acts, "allow", 2) != null && Find(acts, "allow", 2).Key == "k2" && Find(acts, "order") == null);
            a1.AamNow = 8;

            // empty slot, hangar bird: order with the group loadout, M changes ETA
            var q = new CapParams { LaunchDelayS = 0 };
            double etaReal = 0, etaCasual = 0, etaArcade = 0;
            foreach (var mode in new[] { "Realistic", "Casual", "Arcade" })
            {
                var hb = Deck(7, "hangar", 0); hb.Lat = lanes[0].Center.Lat; hb.Lon = lanes[0].Center.Lon; hb.CruiseKn = 1e9; hb.Loadout = null;
                var acts2 = PlanRelief(new List<CapBird> { Air(11, 0, 1500), hb }, 2, 0, lanes, byType, dl, am, mode, 0, q, out gap, out gapS);
                var ord = Find(acts2, "order", 7);
                True("order hangar " + mode, ord != null && ord.Loadout == "AirToAirLongRange" && ord.Slot == 1 && ord.Reason == "relief");
                double eta = ord == null ? 0 : ord.EtaS;
                if (mode == "Realistic") etaReal = eta; else if (mode == "Casual") etaCasual = eta; else etaArcade = eta;
            }
            Near("eta M1", etaReal, 1800, 0.01); Near("eta M5 ratio", etaReal / etaCasual, 5, 0.01); Near("eta M10 ratio", etaReal / etaArcade, 10, 0.01);

            // reserve K: order from hangar with group loadout ("auto" resolves to LR)
            var autoMap = new Dictionary<string, string> { { "F-14A", "auto" } };
            acts = PlanRelief(new List<CapBird> { Air(11, 0, 1500), Air(12, 1, 1500), Deck(7, "hangar", 0), Deck(8, "deck_cool", 600) }, 2, 1, lanes, autoMap, dl, am, "Realistic", 0, p, out gap, out gapS);
            var rs = Find(acts, "order");
            True("reserve order", rs != null && rs.Reason == "reserve" && rs.Loadout == "AirToAirLongRange" && rs.Uid == 7 && rs.Slot == -1);
            True("reserve only one", acts.FindAll(x => x.Kind == "order").Count == 1);
            // explicit loadout unavailable -> fallback + warn
            var los2 = F14(); los2[0].Available = false;
            acts = PlanRelief(new List<CapBird> { Air(11, 0, 1500), Air(12, 1, 1500), Deck(7, "hangar", 0) }, 2, 1, lanes, byType, (d, t) => los2, am, "Realistic", 0, p, out gap, out gapS);
            True("fallback order", Find(acts, "order") != null && Find(acts, "order").Loadout == "AirToAir");
            True("fallback warn", Find(acts, "warn") != null && Find(acts, "warn").Code == "loadout_fallback");

            // other ready loadout flies as is only when a gap would follow
            var odd = Deck(1, "deck_ready", 0, null, "AirToAir");
            a1.ReserveNm = 1500; a1.AamNow = 8;
            acts = plan(new List<CapBird> { a1, a2, odd, Deck(7, "hangar", 0) }, 2, 0, "Realistic");
            True("odd stays", Find(acts, "launch") == null && Find(acts, "order") == null);
            a1.AamNow = 1;   // leaving now: hangar bird needs 30 min, odd one is ready
            acts = plan(new List<CapBird> { a1, a2, odd, Deck(7, "hangar", 0) }, 2, 0, "Realistic");
            True("odd flies if gap", Find(acts, "launch", 1) != null && Find(acts, "order") == null);
            a1.AamNow = 8;

            // rotation without reserve
            a1.AamNow = 1;
            acts = plan(new List<CapBird> { a1, a2 }, 2, 0, "Realistic");
            True("rotation gap", gap && gapS > 0 && Find(acts, "warn") != null && Find(acts, "warn").Code == "continuity_gap" && Find(acts, "launch") == null);
            a1.AamNow = 8;
            acts = plan(new List<CapBird> { a1, a2 }, 2, 0, "Realistic");
            True("rotation far", !gap && acts.Count == 0);
            // game flags free the slot at once
            a2.GameWinchester = true;
            acts = plan(new List<CapBird> { a1, a2, Deck(1, "deck_ready", 0) }, 2, 0, "Realistic");
            True("winchester slot", Find(acts, "launch", 1) != null && Find(acts, "launch", 1).Slot == 1);
            a2.GameWinchester = false;
            // empty plan
            acts = plan(new List<CapBird>(), 2, 1, "Realistic");
            True("empty group", acts.Count == 0 && !gap);
        }

        // T-175 / ADR-0075 item 5 and 8: air-only postponement, the fastest alert loadout, the reorder rule
        private static void Roe()
        {
            var p = new CapParams { FuelFloorPct = 0.05 };   // AirOnly = true; ADR-0131: explicit 5 % floor
            True("air only is the default", p.AirOnly && p.RtbRoeHold);
            var b = new CapBird { AamStart = 8, AamNow = 6, Engaged = true, HasTarget = true, TargetKind = 2 };
            True("surface target: no postponement", Postpone(b, 99, p) == null);
            True("surface target: SurfaceTarget", SurfaceTarget(b));
            b.TargetKind = 1;
            True("air target: combat", Postpone(b, 99, p) == "combat" && !SurfaceTarget(b));
            b.TargetKind = -1;
            True("unknown target type: no postponement", Postpone(b, 99, p) == null);
            b.TargetKind = 0; b.Engaged = false; b.HasTarget = false;
            True("no target: none", Postpone(b, 99, p) == null);
            b.TargetKind = 2; b.Evading = true;
            True("evading beats the target type", Postpone(b, 99, p) == "combat");
            b.Evading = false;
            True("hostile near still holds (air contacts only)", Postpone(b, 5, p) == "hostile_near");
            b.AamNow = 0; True("empty: never", Postpone(b, 5, p) == null);
            var old = new CapParams { AirOnly = false };
            var c = new CapBird { AamStart = 8, AamNow = 6, Engaged = true, TargetKind = 2 };
            True("AirOnly=false: the old behaviour", Postpone(c, 99, old) == "combat");
            string pp;
            var g = new CapBird { AamStart = 8, AamNow = 8, ReserveNm = 90, MaxRangeNm = 2000, PatrolKn = 420, Engaged = true, HasTarget = true, TargetKind = 2 };
            True("fuel leave is not held by a surface target", LeaveReason(g, 0, 99, p, out pp) == "fuel" && pp == null);
            // fastest alert loadout
            var am = Ammo;
            var los = new List<LoadoutOption>
            {
                new LoadoutOption { Name = "AirToAirLongRange", Available = true, ReadyUpMin = 30, Weapons = new Dictionary<string, int> { { "AIM-54", 4 } } },
                new LoadoutOption { Name = "AirToAirIntercept", Available = true, ReadyUpMin = 15, Weapons = new Dictionary<string, int> { { "AIM-7", 4 } } },
                new LoadoutOption { Name = "Strike", Available = true, ReadyUpMin = 5, Weapons = new Dictionary<string, int> { { "Mk82", 6 } } },
            };
            Func<string, CapAmmo> f = n => am.ContainsKey(n) ? am[n] : null;
            True("fastest = intercept (strike is no CAP loadout)", AlertFastestLoadout(los, f, "Realistic") == 1);
            los[1].Available = false;
            True("unavailable skipped", AlertFastestLoadout(los, f, "Realistic") == 0);
            True("none", AlertFastestLoadout(new List<LoadoutOption>(), f, "Realistic") == -1);
            // intercept lead: target 100 nm north flying south at 400 kn, own 400 kn: meets halfway
            double ix, iy;
            True("lead head-on", LeadPoint(0, 100, 0, -400, 400, out ix, out iy) && Math.Abs(iy - 50) < 1e-6 && Math.Abs(ix) < 1e-6);
            True("no lead: running away faster", !LeadPoint(0, 100, 0, 600, 400, out ix, out iy));
            // reorder: only when the gain exceeds ReorderGainS (60 s)
            True("reorder 30 min -> 15 min", ReorderWorth(1800, 900, p));
            True("reorder gain 59 s: no", !ReorderWorth(960, 901, p));
            True("reorder gain 61 s: yes", ReorderWorth(962, 900, p));
            True("t204 rank landed cool", ReadoptRank("deck_cool", false, false, true, false) == 0);
            True("t204 rank landed hangar", ReadoptRank("hangar", false, false, true, false) == 1);
            True("t204 rank landed game task", ReadoptRank("deck_prep", true, true, true, false) == 2);
            True("t204 rank landed ready game task", ReadoptRank("deck_ready", true, true, true, false) == 2);
            True("t204 rank landed own task", ReadoptRank("deck_prep", true, false, true, false) == -1);
            True("t204 rank landed deck_ready no key", ReadoptRank("deck_ready", false, false, true, false) == -1);
            True("t204 rank after cool hangar", ReadoptRank("hangar", false, false, false, true) == 0);
            True("t204 rank after cool ready", ReadoptRank("deck_ready", false, false, false, true) == 1);
            True("t204 rank after cool cool", ReadoptRank("deck_cool", false, false, false, true) == -1);
            True("t204 rank after cool game task", ReadoptRank("deck_prep", true, true, false, true) == 2);
            True("t204 rank plain hangar", ReadoptRank("hangar", false, false, false, false) == 0);
            True("t204 rank plain game task", ReadoptRank("deck_prep", true, true, false, false) == -1);
        }

        /// <summary>part: math | lanes | threat | relief | roe | all. null = ok, otherwise error text.</summary>
        /// <summary>T-257: patrol step of a bird on station: the step named "Cruise" (case-insensitive); else the step below the top (>= 3 steps); else the slowest; -1 = no steps. src: name | below_top | slowest | no_steps.</summary>
        public static int StationStep(IList<CapStep> steps, out string src)
        {
            src = "no_steps";
            if (steps == null || steps.Count == 0) return -1;
            foreach (var s in steps) if (s.Name != null && string.Equals(s.Name.Trim(), "cruise", StringComparison.OrdinalIgnoreCase)) { src = "name"; return s.Step; }
            var sorted = new List<CapStep>(steps);
            sorted.Sort((x, y) => x.Kn.CompareTo(y.Kn));
            if (sorted.Count >= 3) { src = "below_top"; return sorted[sorted.Count - 2].Step; }
            src = "slowest"; return sorted[0].Step;
        }

        /// <summary>T-257: step a is faster than step b (Kn from steps); false when either is unknown.</summary>
        public static bool FasterThan(IList<CapStep> steps, int a, int b)
        {
            if (steps == null) return false;
            double ka = -1, kb = -1;
            foreach (var s in steps) { if (s.Step == a) ka = s.Kn; if (s.Step == b) kb = s.Kn; }
            return ka > 0 && kb > 0 && ka > kb + 1e-9;
        }

        private static void StationStepTest()
        {
            string src;
            var named = new List<CapStep> { new CapStep { Step = 1, Kn = 240, Name = "Landing" }, new CapStep { Step = 2, Kn = 270, Name = "Loiter" }, new CapStep { Step = 3, Kn = 600, Name = "CRUISE" }, new CapStep { Step = 4, Kn = 1050, Name = "Max" } };
            True("station by name", StationStep(named, out src) == 3 && src == "name");
            var anon = new List<CapStep>(); foreach (var s in named) anon.Add(new CapStep { Step = s.Step, Kn = s.Kn });
            True("station below_top", StationStep(anon, out src) == 3 && src == "below_top");
            anon.RemoveRange(2, 2);
            True("station slowest", StationStep(anon, out src) == 1 && src == "slowest");
            True("station none", StationStep(new List<CapStep>(), out src) == -1 && src == "no_steps");
            True("faster max>cruise", FasterThan(named, 4, 3));
            True("faster loiter<cruise", !FasterThan(named, 2, 3));
            True("faster same", !FasterThan(named, 3, 3));
            True("faster unknown", !FasterThan(named, 9, 3) && !FasterThan(named, 3, 9));
        }

        private static Dictionary<string, int> W(params object[] kv)
        {
            var d = new Dictionary<string, int>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = (int)kv[i + 1];
            return d;
        }

        private static void LoadoutTest()
        {
            Func<string, CapAmmo> am = n =>
            {
                if (n == "R-27R") return new CapAmmo { Name = n, Type = "Missile", TargetType = "AAW", Label = "R-27R" };
                if (n == "FAB-500") return new CapAmmo { Name = n, Type = "Bomb", TargetType = "ASuW" };
                if (n == "Kh-31A") return new CapAmmo { Name = n, Type = "Missile", TargetType = "ASuW" };
                if (n == "Mk46") return new CapAmmo { Name = n, Type = "Torpedo", TargetType = "ASW" };
                if (n == "Rocket") return new CapAmmo { Name = n, Type = "AerialRocket", TargetType = "UNKNOWN" };
                if (n == "Chaff") return new CapAmmo { Name = n, Type = "Chaff" };
                if (n == "Shown") return new CapAmmo { Name = n, Type = "Bomb", Label = "Nice bomb" };
                return Am(n);
            };
            True("role aim9", AmmoRole(Am("AIM-9")) == "aam");
            True("role mk82", AmmoRole(Am("Mk82")) == "strike");
            True("role fab", AmmoRole(am("FAB-500")) == "strike");
            True("role kh31", AmmoRole(am("Kh-31A")) == "strike");
            True("role torpedo", AmmoRole(am("Mk46")) == "asw");
            True("role gun", AmmoRole(Am("M61")) == null);
            True("role tank", AmmoRole(Am("Tank")) == null);
            True("role pod", AmmoRole(Am("ALQ")) == null);
            True("role chaff", AmmoRole(am("Chaff")) == null);
            True("role null", AmmoRole(null) == null);
            True("role fuelkg", AmmoRole(new CapAmmo { Type = "Bomb", FuelKg = 10 }) == null);
            True("role unknown type strike", AmmoRole(new CapAmmo { Type = "Weirdo" }) == "strike");
            True("f14 air", LoadoutRole(W("AIM-54", 4, "AIM-7", 2, "AIM-9", 2, "Tank", 2), "AirToAirLongRange", am, 1.0) == "air");
            var su33 = W("R-27R", 2, "AIM-9", 2, "FAB-500", 4);
            True("mixed strike", LoadoutRole(su33, "Default", am, 1.0) == "strike");
            True("mixed share 0.5", LoadoutRole(su33, "Default", am, 0.5) == "air");
            True("bombs only", LoadoutRole(W("FAB-500", 4), null, am, 1.0) == "strike");
            True("gun tank none", LoadoutRole(W("M61", 1, "Tank", 2), null, am, 1.0) == "none");
            True("unknown null", LoadoutRole(null, null, am, 1.0) == null);
            True("a2a with rockets", LoadoutRole(W("AIM-9", 2, "Rocket", 4), "AirToAirX", am, 1.0) == "air");
            True("zeros ignored", LoadoutRole(W("AIM-9", 2, "FAB-500", 0, "Mk82", -3), "Default", am, 1.0) == "air");
            True("asw is strike", LoadoutRole(W("Mk46", 2, "AIM-9", 2), "Default", am, 1.0) == "strike");
            var bomb = W("AIM-9", 2, "Mk82", 4);
            True("excl strike", StrikeExclusion(new CapBird { Where = "game_zone", Weapons = bomb }, am, 1.0) == "strike_loadout");
            True("excl hangar", StrikeExclusion(new CapBird { Where = "hangar", Weapons = bomb }, am, 1.0) == null);
            True("excl cool", StrikeExclusion(new CapBird { Where = "deck_cool", Weapons = bomb }, am, 1.0) == null);
            True("excl LoadWeapons wins", StrikeExclusion(new CapBird { Where = "deck_ready", Weapons = W("AIM-9", 2), LoadWeapons = bomb }, am, 1.0) == "strike_loadout");
            True("excl falls back to Weapons", StrikeExclusion(new CapBird { Where = "deck_ready", Weapons = bomb }, am, 1.0) == "strike_loadout");
            True("excl air", StrikeExclusion(new CapBird { Where = "deck_ready", Weapons = W("AIM-9", 2) }, am, 1.0) == null);
            True("excl unknown", StrikeExclusion(new CapBird { Where = "deck_ready" }, am, 1.0) == null);
            True("sum order", LoadoutSummary(W("Mk82", 4, "AIM-9", 2, "R-27R", 4, "M61", 1), am) == "R-27R\u00D74, AIM-9\u00D72, Mk82\u00D74");
            True("sum tie by name", LoadoutSummary(W("AIM-9", 2, "AIM-7", 2), am) == "AIM-7\u00D72, AIM-9\u00D72");
            True("sum max", LoadoutSummary(W("AIM-9", 5, "AIM-7", 4, "AIM-54", 3, "Mk82", 2, "Maverick", 1), am, 4) == "AIM-9\u00D75, AIM-7\u00D74, AIM-54\u00D73, Mk82\u00D72, \u2026");
            True("sum label", LoadoutSummary(W("Shown", 2), am) == "Nice bomb\u00D72");
            True("sum neutral hidden", LoadoutSummary(W("M61", 1, "Tank", 2), am) == "");
            True("sum null", LoadoutSummary(null, am) == "");
            True("params clone", new CapParams { StrikeMixedAamShare = 0.5 }.Clone().StrikeMixedAamShare == 0.5);
        }

        private static void EngagedStateTest()
        {
            foreach (string st in new[] { "EngageAirContact", "Pursuit", "Cranking", "MoveToEngage", "Aligning" }) True("engaged " + st, EngagedNow(st, 0));
            True("engaged tasks", EngagedNow("CAP", 1));
            foreach (string st in new[] { "CAP", null, "", "EngageSurfaceContact", "IdentifyAirContact", "engageAirContact" }) True("not engaged " + (st ?? "null"), !EngagedNow(st, 0));
            True("approach not air fight", !AirFightState("MoveToEngage") && !AirFightState("Aligning"));
            var p = new CapParams();
            True("postpone -1", Postpone(new CapBird { AamNow = 6, AiState = "EngageAirContact", TargetKind = -1 }, 999, p) == "combat");
            True("postpone 0", Postpone(new CapBird { AamNow = 6, AiState = "EngageAirContact", TargetKind = 0 }, 999, p) == "combat");
            True("postpone 2", Postpone(new CapBird { AamNow = 6, AiState = "EngageAirContact", TargetKind = 2 }, 999, p) == "combat");
            True("postpone calm", Postpone(new CapBird { AamNow = 6, AiState = "CAP" }, 999, p) == null);
            True("postpone no aam", Postpone(new CapBird { AamNow = 0, AiState = "EngageAirContact" }, 999, p) == null);
            True("incombat Scissors", InCombat(new CapBird { AamNow = 4, AiState = "Scissors" }, p));
        }

        public static string SelfTest(string part)
        {
            _err = null;
            try
            {
                if (part == "math" || part == "all") { Admission(); DashTest(); RingFuelTest(); FracFuelTest(); StationStepTest(); EngagedStateTest(); }
                if (part == "loadout" || part == "all") LoadoutTest();
                if (part == "lanes" || part == "all") LanesTest();
                if (part == "threat" || part == "all") Threat();
                if (part == "relief" || part == "all") Relief();
                if (part == "roe" || part == "all") Roe();
            }
            catch (Exception e) { return part + ": " + e.GetType().Name + ": " + e.Message; }
            return _err;
        }
    }
}
