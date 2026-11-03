using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 phase A: surface contacts for the strike window. The player's table only: the side from the track, the class (ini name, tonnage, roles, AA) only after
    // Vehicle.Identified (HitEstimateGame.Class rules: never the instance of a foreign unit). AA strength: the points cache of the game (aa_k / aa_s per class), else the file
    // of that cache, else AAW_Capability, else the role. Nothing is written to the game.
    internal sealed partial class GameApi
    {
        private sealed partial class StrikeGameImpl
        {
            private sealed class ClassInfo { public double Displacement; public bool Civilian, Carrier; public string Role; public int AawCap = -1; }

            private readonly Dictionary<string, ClassInfo> _classes = new Dictionary<string, ClassInfo>();
            private readonly Dictionary<string, StrikeAd> _ad = new Dictionary<string, StrikeAd>();
            private readonly Dictionary<string, List<StrikeSam>> _sams = new Dictionary<string, List<StrikeSam>>();
            private bool _adLoaded, _adFileTried;
            private double _adTriedAt = double.NegativeInfinity;                                 // phase B (review of A, finding 3): a failed read of the points cache is repeated at most every 30 s
            private readonly Dictionary<string, double> _classMiss = new Dictionary<string, double>();   // finding 4: a class that was not in the encyclopedia is asked again after 10 s
            private List<StrikeShip> _ships = new List<StrikeShip>();
            private int _shipsAt = int.MinValue;
            // E1b (live probe of 2026-11-01): the age of a lost contact in MISSION seconds. The shared _liveSeen stamps of the overview are on the real-time clock (GameApi.Clock = Time.unscaledTime), and "now - seen" against the mission time was 0 or garbage:
            // a track that stopped being live never aged, so "contact lost -> held -> hold_timeout" never worked on the live game. The strike keeps its own stamps in mission time.
            private readonly Dictionary<int, double> _liveAt = new Dictionary<int, double>();
            private double _liveAtNow = double.NegativeInfinity;

            private static readonly string[] RolePriority = { "Merchant", "SmallCivilian", "Passenger", "Transport", "Landing", "RAS", "Carrier", "CVS", "CVL", "FAC", "Minesweep", "AAW", "ASuW_VLR", "ASuW", "ASW" };

            public IList<StrikeShip> Ships()
            {
                if (Fresh(_shipsAt)) return _ships;
                var res = new List<StrikeShip>();
                try
                {
                    var snap = _a.Snapshot(true);
                    double now = Now;
                    if (now < _liveAtNow - 1) _liveAt.Clear();   // the mission clock went back (Restart Mission, a load): the old stamps are from another run
                    _liveAtNow = now;
                    foreach (var t in snap.Tracks)
                    {
                        try
                        {
                            if (!t.HasId || !t.Lat.HasValue || !t.Lon.HasValue || t.Kind != "Surface") continue;
                            object v;
                            if (snap.VehicleById.TryGetValue(t.Id, out v) && _a.IsGone(v)) continue;
                            var s = new StrikeShip { TrackId = t.Id, Lat = t.Lat.Value, Lon = t.Lon.Value, Side = t.Side, Identified = t.IdentifiedKnown && t.Identified };
                            s.Hostile = t.Side == "hostile";
                            if (t.Vx.HasValue && t.Vz.HasValue)
                            {
                                s.SpeedKn = OverviewMath.SpeedKn(t.Vx.Value, t.Vz.Value);
                                if (s.SpeedKn >= 0.5) s.CourseDeg = OverviewMath.CourseDeg(t.Vx.Value, t.Vz.Value);
                            }
                            double seen;
                            if (t.Live) { _liveAt[t.Id] = now; s.FixAgeS = 0; }
                            else s.FixAgeS = _liveAt.TryGetValue(t.Id, out seen) ? Math.Max(0, now - seen) : 9999;
                            // the class only for an identified track
                            string ini = s.Identified ? _a.RrClassIniName(t.Id) : null;
                            if (ini != null) FillClass(s, ini, t.ClassName);
                            res.Add(s);
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
                _calibSource = Dominant(res);
                _ships = res; _shipsAt = Environment.TickCount;
                return res;
            }

            private static string Dominant(List<StrikeShip> ships)
            {
                bool cache = false, aaw = false, role = false;
                foreach (var s in ships) { string src = s.Ad.Source; if (src == "cache") cache = true; else if (src == "aaw") aaw = true; else if (src == "role") role = true; }
                return cache ? "cache" : aaw ? "aaw" : role ? "role" : "none";
            }

            private void FillClass(StrikeShip s, string ini, string className)
            {
                s.ClassIni = ini; s.ClassName = className ?? ini;
                var c = ClassOf(ini);
                if (c != null)
                {
                    s.DisplacementT = c.Displacement; s.Civilian = c.Civilian; s.Carrier = c.Carrier; s.Role = c.Role; s.AawCap = c.AawCap;
                }
                s.Ad = AdOf(ini, c);
                List<double> nm = null;
                try { nm = _a.AgClassAawNm(ini); } catch (Exception) { }
                s.SamMaxNm = nm == null || nm.Count == 0 ? 0 : nm.Max();
                s.RadarFt = 100;   // the radar height of the class is not readable (no ObjectBaseParameters of a class): the mast height of 100 ft
                string[] rb; double rmax;
                if (ClassRadars(ini, out rb, out rmax)) { s.RadarBands = rb; s.RadarMaxNm = rmax; }
                s.Sams = SamsOf(ini);
                // T-342: the budget of the sinking (5 * maxI of the class, HitEstimateMath: the class data, never the live object) and the AA gun of the class
                try
                {
                    var hc = _a.HeClassOf(ini);
                    s.KillBudget = hc != null && hc.Kind == HeTargetKind.Ship ? HitEstimateMath.Budget(hc) : -1;
                }
                catch (Exception) { s.KillBudget = -1; }
                s.HasCiws = CiwsOf(ini);
            }

            private readonly Dictionary<string, bool> _ciws = new Dictionary<string, bool>();

            /// <summary>The class carries a gun (Projectile) with an AA role whose range is at most StrikeCalib.CiwsMaxNm: a CIWS (the heuristic of the plan, calibrated through KillMargin).</summary>
            private bool CiwsOf(string ini)
            {
                bool res;
                if (_ciws.TryGetValue(ini, out res)) return res;
                try
                {
                    var weapons = new List<ClassWeapon>(); var passive = new List<double>();
                    if (!_a.RrClassEntry(ini, weapons, passive)) return false;   // the encyclopedia is not ready: asked again
                    res = weapons.Any(w => w.Type == RingAmmoType.Projectile && (w.Primary == RingTarget.Aaw || w.Secondary == RingTarget.Aaw) && w.MaxNm > 0 && w.MaxNm <= StrikeCalib.CiwsMaxNm);
                    _ciws[ini] = res;
                }
                catch (Exception) { res = false; }
                return res;
            }

            private static double RealS() { return Environment.TickCount / 1000.0; }

            private ClassInfo ClassOf(string ini)
            {
                ClassInfo c;
                if (_classes.TryGetValue(ini, out c)) return c;
                double missAt, now = RealS();
                if (_classMiss.TryGetValue(ini, out missAt) && now >= missAt && now - missAt < 10) return null;
                try
                {
                    object p = null;
                    if (_a.Present(HitEstimateSpecs.FindCached)) p = _a.CallStatic(HitEstimateSpecs.FindCached, "vessels", ini);
                    if (p == null && _a.Present(HitEstimateSpecs.LoadSimple)) { try { p = _a.CallStatic(HitEstimateSpecs.LoadSimple, ini, "vessels", 0); } catch (Exception) { p = null; } }
                    if (p == null) { _classMiss[ini] = now; return null; }   // not cached: asked again after 10 s
                    c = new ClassInfo { Displacement = GameApi.D(_a.Get(p, HitEstimateSpecs.SopDisplacement)) ?? 0, Civilian = BoolOf(_a.Get(p, HitEstimateSpecs.SopIsCivilian)) };
                    int aaw = (int)(GameApi.D(_a.Get(p, StrikeSpecs.SopAawCapability)) ?? 0);
                    c.AawCap = aaw > 0 ? aaw : -1;   // 0 = no key in the ini: unknown
                    var roles = new HashSet<string>();
                    var list = _a.Get(p, HitEstimateSpecs.SopUnitRoles) as IEnumerable;
                    if (list != null) foreach (var r in list) if (r != null) roles.Add(r.ToString());
                    c.Carrier = roles.Contains("Carrier") || roles.Contains("CVS") || roles.Contains("CVL");
                    foreach (string r in RolePriority) if (roles.Contains(r)) { c.Role = r; break; }
                    _classes[ini] = c;
                    return c;
                }
                catch (Exception) { return null; }
            }

            // ---- AA strength ----
            private void LoadAd()
            {
                if (_adLoaded) return;
                double now = RealS();
                if (now >= _adTriedAt && now - _adTriedAt < 30) return;
                _adTriedAt = now;
                try
                {
                    var cache = _a.Present(StrikeSpecs.CacheData) ? _a.GetStatic(StrikeSpecs.CacheData) : null;
                    var units = cache == null ? null : _a.Get(cache, StrikeSpecs.CacheUnits) as IDictionary;
                    if (units != null)
                    {
                        foreach (DictionaryEntry kv in units)
                        {
                            string key = kv.Key as string;
                            if (key == null || !key.StartsWith("vessels/", StringComparison.Ordinal) || kv.Value == null) continue;
                            var roles = _a.Get(kv.Value, StrikeSpecs.CacheRoles) as IDictionary;
                            if (roles == null) continue;
                            var nums = new Dictionary<string, double>();
                            foreach (DictionaryEntry r in roles)
                            {
                                string rk = r.Key as string;
                                if (rk != null && (rk.StartsWith("aa_k_", StringComparison.Ordinal) || rk.StartsWith("aa_s_", StringComparison.Ordinal)) && r.Value != null) nums[rk] = Convert.ToDouble(r.Value, CultureInfo.InvariantCulture);
                            }
                            var ad = StrikeAdMath.FromRoles(nums);
                            if (ad.Source == "cache") _ad[key.Substring("vessels/".Length)] = ad;
                        }
                        _adLoaded = _ad.Count > 0;
                    }
                }
                catch (Exception) { }
                if (_adLoaded || _adFileTried) return;
                _adFileTried = true;   // once: the file of the same cache next to the saves
                try
                {
                    string path = Path.Combine(Path.Combine(UnityEngine.Application.persistentDataPath, "cache"), "taskforce_pointvalues.json");
                    string err;
                    if (File.Exists(path) && StrikeAdMath.ParseCache(File.ReadAllText(path), _ad, out err)) _adLoaded = _ad.Count > 0;
                }
                catch (Exception) { }
            }

            private StrikeAd AdOf(string ini, ClassInfo c)
            {
                LoadAd();
                StrikeAd ad;
                if (_ad.TryGetValue(ini, out ad)) return ad;
                return StrikeAdMath.Fallback(c == null ? -1 : c.AawCap, c == null ? 0 : c.Displacement, c != null && c.Carrier, c == null ? null : c.Role);
            }

            // ---- the SAMs of a class (for the correction of the missile): the AAW missiles of the class entry, their ammunition parameters ----
            private List<StrikeSam> SamsOf(string ini)
            {
                List<StrikeSam> res;
                if (_sams.TryGetValue(ini, out res)) return res;
                res = new List<StrikeSam>();
                try
                {
                    var weapons = new List<ClassWeapon>(); var passive = new List<double>();
                    if (!_a.RrClassEntry(ini, weapons, passive)) return res;   // not cached: the encyclopedia may not be ready yet
                    var seen = new HashSet<string>();
                    foreach (var w in weapons)
                    {
                        if (w.Type != RingAmmoType.Missile || (w.Primary != RingTarget.Aaw && w.Secondary != RingTarget.Aaw) || string.IsNullOrEmpty(w.Key) || !seen.Add(w.Key)) continue;
                        var sam = ReadSam(w.Key);
                        if (sam != null) { sam.RangeNm = w.MaxNm; res.Add(sam); }
                    }
                }
                catch (Exception) { }
                _sams[ini] = res;
                return res;
            }

            private StrikeSam ReadSam(string ammo)
            {
                if (!_a.Present(StrikeSpecs.AmmoGetShared)) return null;
                try
                {
                    var ap = _a.CallStatic(StrikeSpecs.AmmoGetShared, ammo);
                    if (ap == null) return null;
                    double pk = GameApi.D(_a.Get(ap, StrikeSpecs.AmmoKillProb)) ?? 0;
                    if (pk <= 0) return null;
                    var s = new StrikeSam { Ammo = ammo, Pk = pk };
                    var alts = _a.Get(ap, StrikeSpecs.AmmoAttackAlts);
                    if (alts != null) { s.MinAltFt = Vec(alts, RangeRingSpecs.VecX) * FtPerUnit; s.MaxAltFt = Vec(alts, RangeRingSpecs.VecY) * FtPerUnit; }
                    double vmax = GameApi.D(_a.Get(ap, StrikeSpecs.AmmoMaxAttackVel)) ?? 0;
                    s.MaxAttackKn = vmax > 0 && vmax < 1e6 ? vmax : 0;   // float.MaxValue = no limit
                    s.OutOfAltPen = GameApi.D(_a.Get(ap, StrikeSpecs.AmmoOoaPenalty)) ?? s.OutOfAltPen;
                    s.SpeedMult = GameApi.D(_a.Get(ap, StrikeSpecs.AmmoSpeedMult)) ?? s.SpeedMult;
                    s.SizeMult = GameApi.D(_a.Get(ap, StrikeSpecs.AmmoSizeMult)) ?? s.SizeMult;
                    return s;
                }
                catch (Exception) { return null; }
            }
        }
    }
}
