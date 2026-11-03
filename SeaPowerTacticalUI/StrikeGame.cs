using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 phase A: the game side of the strike window, READ ONLY. GameApi partial, reflection through the registry (StrikeSpecs and the specs of the other modules).
    // Aircraft: the player's own aircraft with the fact ammunition of every mount (Common WeaponRows, R7: the Harpoon type depends on the mission year) and the ammunition fields of the missile.
    // Ships: StrikeGame.Ships.cs. Time of flight: StrikeGame.Tof.cs. Every method is in try / catch: an unclear read is "no data", never an exception; nothing is written to the game.
    internal sealed partial class GameApi
    {
        /// <summary>The game side of the strike window; one per mod instance (the caches live in it).</summary>
        internal IStrikeGame NewStrikeGame() { _strikeImpl = new StrikeGameImpl(this); return _strikeImpl; }

        private StrikeGameImpl _strikeImpl;

        /// <summary>The mission was left: the ammunition parameters, the classes and the cached rows belong to it.</summary>
        internal void StrikeLeaveMission()
        {
            try { var g = _strikeImpl; if (g != null) g.ResetMission(); } catch (Exception) { }
            try { StrikeLandLeave(); } catch (Exception) { }
        }

        private sealed partial class StrikeGameImpl : IStrikeGame
        {
            private const double UnitsPerNm = 27.559496, FtPerUnit = 220.47266, CacheMs = 500;
            private readonly GameApi _a;
            private readonly Dictionary<string, object> _apByAmmo = new Dictionary<string, object>();   // ammunition file -> its AmmunitionParameters (kept for the time of flight)
            private readonly Dictionary<string, double[]> _typeRange = new Dictionary<string, double[]>();   // type -> {full range nm, cruise kn, cruise alt ft} of an aircraft of the type seen in this mission
            private List<StrikeAircraft> _air = new List<StrikeAircraft>();
            private int _airAt = int.MinValue;

            public StrikeGameImpl(GameApi a) { _a = a; }

            /// <summary>Forget everything read from the left mission (the points cache of the game stays: it is the game's own data per build).</summary>
            public void ResetMission()
            {
                _apByAmmo.Clear(); _air = new List<StrikeAircraft>(); _airAt = int.MinValue;
                _ships = new List<StrikeShip>(); _shipsAt = int.MinValue;
                _classes.Clear(); _sams.Clear(); _classMiss.Clear(); _ciws.Clear(); _speedMem.Clear(); _hitCursor = 0; _hitFilter.Clear(); _adTriedAt = double.NegativeInfinity;
                _fighterCls.Clear(); _deck = new List<StrikeDeckItem>(); _deckAt = int.MinValue; _remembered.Clear(); _deckAmmoMiss.Clear(); _decks = new List<StrikeDeckInfo>(); _decksAt = int.MinValue; _typeRange.Clear(); _vehOfKey.Clear();
            }

            public double Now { get { try { var t = _a.Frame.MissionTimeS; return t.HasValue ? t.Value : 0; } catch (Exception) { return 0; } } }

            /// <summary>1 - Globals._missileInterceptChanceReduction (the game applies it to every SAM shot at the player's missiles); 1 when it cannot be read. DiffSrc: game | default.</summary>
            public double Difficulty()
            {
                try
                {
                    if (_a.Present(StrikeSpecs.GlobalMissileReduction))
                    {
                        var v = GameApi.D(_a.GetStatic(StrikeSpecs.GlobalMissileReduction));
                        if (v.HasValue && v.Value >= 0 && v.Value < 1) { DiffSrc = "game"; return 1.0 - v.Value; }
                    }
                }
                catch (Exception) { }
                DiffSrc = "default";
                return 1.0;
            }

            public string DiffSrc { get; private set; } = "default";

            /// <summary>The id of the own aircraft in the event log of the game ([1005]: its map vehicle Id for the player's taskforce); -1 = not readable. Read only when an order is given.</summary>
            public int GameId(int uid)
            {
                try
                {
                    if (!_a.Present(InterceptSpecs.ObjMapVehicle) || !_a.Present(OverviewSpecs.VehId)) return -1;
                    var u = _a.FindOwnUnit(uid);
                    var player = _a.GetStatic(CoreSpecs.PlayerTaskforce);
                    if (u == null || player == null) return -1;
                    var veh = _a.Call(u, InterceptSpecs.ObjMapVehicle, player);
                    var id = veh == null ? null : GameApi.D(_a.Get(veh, OverviewSpecs.VehId));
                    return id.HasValue ? (int)id.Value : -1;
                }
                catch (Exception) { return -1; }
            }

            private static bool Fresh(int at) { return at != int.MinValue && unchecked(Environment.TickCount - at) >= 0 && unchecked(Environment.TickCount - at) < CacheMs; }

            private double Vec(object v, MemberSpec comp) { return GameApi.D(_a.Get(v, comp)) ?? 0; }

            /// <summary>The player's aircraft and helicopters with their anti-ship missiles (classified); cached for half a second of real time.</summary>
            public IList<StrikeAircraft> Aircraft()
            {
                if (Fresh(_airAt)) return _air;
                var res = new List<StrikeAircraft>();
                try
                {
                    foreach (var u in _a.AirUnits())
                    {
                        try
                        {
                            if (!BoolOf(_a.Get(u, CoreSpecs.ObjIsPlayer))) continue;
                            var a = ReadAircraft(u);
                            if (a != null) res.Add(a);
                        }
                        catch (Exception) { }
                    }
                    StrikeAirMath.Classify(res);
                }
                catch (Exception) { }
                _air = res; _airAt = Environment.TickCount;
                return res;
            }

            private StrikeAircraft ReadAircraft(object u)
            {
                var obp = _a.Get(u, AirOpsSpecs.ObjObp);
                var a = new StrikeAircraft
                {
                    Uid = _a.UidOf(u),
                    Name = _a.RxGet(u, CoreSpecs.ObjName) as string,
                    Type = _a.Get(obp, AirOpsSpecs.ObpIniName) as string,
                    ObjType = _a.IsHelicopter(u) ? "Helicopter" : "Aircraft",
                    Airborne = _a.InFlight(u),
                };
                if (a.Uid <= 0) return null;
                double lat, lon, h;
                if (!_a.OwnPos(u, out lat, out lon, out h)) return null;
                a.Lat = lat; a.Lon = lon;
                try { a.AltFt = Math.Abs(GameApi.D(_a.Call(u, AirOpsSpecs.ObjHeightFt)) ?? 0); } catch (Exception) { }
                double kn = 0;
                try { kn = Math.Abs(GameApi.D(_a.Call(u, AirOpsSpecs.ObjVelocityKn)) ?? 0); } catch (Exception) { }
                a.CruiseKn = kn > 150 ? kn : 450;
                a.SpeedKn = kn;
                try { var hd = GameApi.D(_a.RxGet(u, AirOpsSpecs.ObjHeading)); a.HeadingDeg = hd.HasValue && !double.IsNaN(hd.Value) ? ((hd.Value % 360) + 360) % 360 : -1; } catch (Exception) { }   // grid-ok: the aircraft heading for the plan, no course command
                a.CruiseAltFt = a.AltFt > 1000 ? a.AltFt : 25000;
                // fuel as the CAP reads it: the range left over the full range
                double? left = GameApi.D(_a.RxGet(u, AirOpsSpecs.ObjRangeKm)), full = GameApi.D(_a.Get(obp, AirOpsSpecs.ObpMaxRangeKm));
                double frac = left.HasValue && full.HasValue && full.Value > 0 && !double.IsNaN(left.Value) ? Math.Max(0, Math.Min(1, left.Value / full.Value)) : 1;
                a.FuelFrac = frac;
                // E1b (live probe of 2026-11-01): the way-home reserve, the full tanks and the live fraction as the CAP reads them (T-337, CapMath.GameFuel). Without them FuelLeave never fired on the live game (FuelFracLive false, MaxRangeNm 0)
                try
                {
                    double reserveNm, maxNm, liveFrac;
                    CapMath.GameFuel(GameApi.D(_a.RxGet(u, CapSpecs.AircraftActualRangeKm)), GameApi.D(_a.RxGet(u, CapSpecs.ObjRangeOnMap)), 1000.0 * 0.0148809375, left, full, out reserveNm, out maxNm, out liveFrac);
                    a.ReserveNm = reserveNm; a.MaxRangeNm = maxNm;
                    if (liveFrac >= 0) { a.FuelFrac = liveFrac; a.FuelFracLive = true; frac = liveFrac; }
                    if (a.Type != null && maxNm > 0) _typeRange[a.Type] = new[] { maxNm, a.CruiseKn > 150 ? a.CruiseKn : 450, a.CruiseAltFt };   // T-342 B: the deck planner asks the range of a type that is still in the hangar
                }
                catch (Exception) { }
                a.EnduranceS = AswMath.FullEnduranceS(GameApi.D(_a.RxGet(u, AirOpsSpecs.AircraftEndurance)), frac);
                try { a.Ew = ReadEw(u); } catch (Exception) { a.Ew = null; }
                foreach (var row in _a.WeaponRows(u))
                {
                    try
                    {
                        // [file, display, type, target, category, count, rangeNm, canEngage, guidance, midcourse, needsIllumination]
                        if (row.Length < 10 || row[2] != "Missile" || row[3] != "ASuW") continue;
                        var m = ReadMissile(u, row);
                        if (m != null) a.Missiles.Add(m);
                    }
                    catch (Exception) { }
                }
                return a;
            }

            private StrikeMissile ReadMissile(object u, string[] row)
            {
                var m = new StrikeMissile { Ammo = row[0], Name = row[1], Type = row[2], TargetType = row[3], Guidance = row[8], MidCourse = row[9], Operable = row[7] == "true" };
                int n; int.TryParse(row[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                m.Count = Math.Max(0, n);
                double rng; if (double.TryParse(row[6], NumberStyles.Float, CultureInfo.InvariantCulture, out rng)) m.MaxNm = rng;
                m.AntiRadiation = m.Guidance == "arm";
                object ap = null;
                if (!_apByAmmo.TryGetValue(m.Ammo, out ap) || ap == null)
                {
                    var ammo = _a.Call(u, StrikeSpecs.ObjGetAmmoByName, m.Ammo);
                    ap = ammo == null ? null : _a.Get(ammo, StrikeSpecs.AmmoOfAmmunition);
                    if (ap != null) _apByAmmo[m.Ammo] = ap;
                }
                if (ap == null) return m;
                FillFromAp(m, ap);
                return m;
            }

            /// <summary>The ammunition fields of a missile row (ranges, altitudes, speed, size, profile) from the AmmunitionParameters; the row fields (count, operable, guidance text) are the caller's.</summary>
            private void FillFromAp(StrikeMissile m, object ap)
            {
                var ranges = _a.Get(ap, StrikeSpecs.AmmoLaunchRanges);
                if (ranges != null) { m.MinNm = Vec(ranges, RangeRingSpecs.VecX) / UnitsPerNm; double mx = Vec(ranges, RangeRingSpecs.VecY) / UnitsPerNm; if (mx > 0) m.MaxNm = mx; }
                var alts = _a.Get(ap, StrikeSpecs.AmmoLaunchAlts);
                if (alts != null) { m.MinLaunchAltFt = Vec(alts, RangeRingSpecs.VecX) * FtPerUnit; m.MaxLaunchAltFt = Vec(alts, RangeRingSpecs.VecY) * FtPerUnit; }
                m.TargetRequired = BoolOf(_a.Get(ap, StrikeSpecs.AmmoTargetRequired));
                m.SpeedKn = GameApi.D(_a.Get(ap, StrikeSpecs.AmmoMaxVelocity)) ?? 0;
                m.FrontalM2 = GameApi.D(_a.Get(ap, StrikeSpecs.AmmoFrontalArea)) ?? 0;
                m.MassKg = GameApi.D(_a.Get(ap, StrikeSpecs.AmmoMass)) ?? 0;
                m.DefBonus = GameApi.D(_a.Get(ap, StrikeSpecs.AmmoDefenceBonus)) ?? 0;
                double skim = GameApi.D(_a.Get(ap, StrikeSpecs.AmmoSeaSkimFt)) ?? 0, loft = (GameApi.D(_a.Get(ap, StrikeSpecs.AmmoLoftAlt)) ?? 0) * FtPerUnit;
                m.CruiseAltFt = skim > 0 ? skim : loft > 0 ? loft : 3000;   // no profile data: a high one (no sea skimming correction)
                // phase E1: what the game's own chance sees (Utils.GetInterceptionChance on ReferenceTargets.ProfileForMissile): the terminal altitude (the Harpoon: 7 ft), else the final flight phase (the sea skimming altitude by default), else the lowest attack altitude
                double term = (GameApi.D(_a.Get(ap, StrikeSpecs.AmmoTerminalAlt)) ?? -1) * FtPerUnit, fin = (GameApi.D(_a.Get(ap, StrikeSpecs.AmmoFinalAlt)) ?? -1) * FtPerUnit;
                double atk = 0; var aa = _a.Get(ap, StrikeSpecs.AmmoAttackAlts); if (aa != null) atk = Vec(aa, RangeRingSpecs.VecX) * FtPerUnit;
                m.ProfileAltFt = term > 0 ? term : fin > 0 ? fin : atk > 0 ? atk : 15;
                double tkn = GameApi.D(_a.Get(ap, StrikeSpecs.AmmoTerminalVel)) ?? 0;
                m.ProfileKn = tkn > 0 ? tkn : m.SpeedKn;
                // T-342: the warhead as the damage model of the game sees it (HitEstimate reads it by the ammunition file; the same HeAmmo as the hit counter)
                try
                {
                    var he = _a.HeAmmoByFile(m.Ammo);
                    if (he != null)
                    {
                        m.WarheadPower = he.Power; m.DudPower = he.DudPower; m.DamageMult = he.DamageMult > 0 ? he.DamageMult : 1;
                        m.Warhead = he.Warhead == VoiceBotBridge.Ui.HeWarhead.Heat ? "heat" : he.Warhead == VoiceBotBridge.Ui.HeWarhead.Frag ? "frag" : he.Warhead == VoiceBotBridge.Ui.HeWarhead.Cluster ? "cluster" : "normal";
                    }
                }
                catch (Exception) { }
            }

            /// <summary>A missile row of a deck loadout weapon (no aircraft yet: the type, the target class and the guidance come from the ammunition itself).</summary>
            private StrikeMissile ReadMissileFromAp(object ap, string ammo, int count)
            {
                var m = new StrikeMissile { Ammo = ammo, Count = Math.Max(0, count), Operable = true };
                m.Type = (_a.Get(ap, StrikeSpecs.AmmoType) ?? "").ToString();
                m.TargetType = (_a.Get(ap, StrikeSpecs.AmmoTargetType) ?? "").ToString();
                string disp = _a.Get(ap, StrikeSpecs.AmmoDisplayName) as string;
                m.Name = string.IsNullOrEmpty(disp) ? ammo : disp;
                string mid; m.Guidance = _a.GuidanceOf(ap, out mid); m.MidCourse = mid;
                m.AntiRadiation = m.Guidance == "arm";
                FillFromAp(m, ap);
                return m;
            }

            /// <summary>The AmmunitionParameters of an ammunition file the aircraft of the group carry (for the time of flight); null = never seen.</summary>
            internal object ApOf(string ammo) { object ap; return ammo != null && _apByAmmo.TryGetValue(ammo, out ap) ? ap : null; }

            public string CalibSource { get { return _calibSource; } }
            private string _calibSource = "none";
        }
    }
}
