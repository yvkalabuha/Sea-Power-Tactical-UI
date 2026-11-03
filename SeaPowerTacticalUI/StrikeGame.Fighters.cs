using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-342 phase D (plan 34.1): the hostile air tracks as the strike sees them. The source is the same track snapshot as the air threat service (AirGuard): Kind "Air" (helicopters are skipped), hostile side, not gone.
    // The class (and with it R_t and the fighter flag) is read only for an identified track; an unidentified fast track is never jammed (decision of the customer). Whether a class is a fighter is cached per class and
    // mission: the class roles (Fighter | Interceptor) when the game gives them, else the fallback sign "a missile with an anti-air range + a radar of the X band". Nothing hidden is read: the object of a track
    // is touched only to give the JamTask (StrikeGame.Ew.cs). Every method catches inside.
    internal sealed partial class GameApi
    {
        private sealed partial class StrikeGameImpl
        {
            private sealed class FighterClass { public bool IsFighter; public string Src = "none"; public bool IrOnly; public string Roles; }
            private readonly Dictionary<string, FighterClass> _fighterCls = new Dictionary<string, FighterClass>();

            private FighterClass FighterClassOf(string ini, double rNm)
            {
                FighterClass c;
                if (_fighterCls.TryGetValue(ini, out c)) return c;
                c = new FighterClass();
                try
                {
                    // 1. the roles of the class (optional member; a class that is not a ship may not carry them)
                    try
                    {
                        object p = _a.Present(HitEstimateSpecs.FindCached) ? _a.CallStatic(HitEstimateSpecs.FindCached, "aircraft", ini) : null;
                        var list = p == null ? null : _a.Get(p, HitEstimateSpecs.SopUnitRoles) as IEnumerable;
                        if (list != null)
                        {
                            var names = new List<string>();
                            foreach (var r in list) names.Add(r == null ? "" : r.ToString());
                            c.Roles = string.Join(",", names);
                            if (StrikeEwFighterMath.RoleIsFighter(c.Roles)) { c.IsFighter = true; c.Src = "role"; }
                        }
                    }
                    catch (Exception) { }
                    // 1b. T-346: an aircraft class keeps its roles in the ini ([AI] Role=Fighter,Bomber), not in the role list of the metadata
                    if (!c.IsFighter)
                    {
                        try
                        {
                            if (_a.Present(AswSpecs.MetaFindCached) && _a.Present(AswSpecs.SopIni) && _a.Present(AswSpecs.IniReadStr))
                            {
                                object sop = _a.CallStatic(AswSpecs.MetaFindCached, "aircraft", ini);
                                if (sop == null && _a.Present(AswSpecs.SopLoadSimple)) sop = _a.CallStatic(AswSpecs.SopLoadSimple, ini, "aircraft", 0);
                                object ih = sop == null ? null : _a.Get(sop, AswSpecs.SopIni);
                                string csv = ih == null ? null : _a.Call(ih, AswSpecs.IniReadStr, "AI", "Role", "", false) as string;
                                if (!string.IsNullOrEmpty(csv)) { c.Roles = csv; if (StrikeEwFighterMath.RoleIsFighter(csv)) { c.IsFighter = true; c.Src = "role_ini"; } }
                            }
                        }
                        catch (Exception) { }
                    }
                    // 2. the fallback sign
                    if (!c.IsFighter && rNm > 0)
                    {
                        string[] bands; double mx;
                        if (ClassRadars(ini, out bands, out mx) && bands != null && bands.Contains("X")) { c.IsFighter = true; c.Src = "aam_radar"; }
                    }
                }
                catch (Exception) { }
                _fighterCls[ini] = c;
                _a.StrikeLog("[Strike] strike ew fighter class ini=" + ini + " fighter=" + (c.IsFighter ? "true" : "false") + " src=" + c.Src + " roles=" + (string.IsNullOrEmpty(c.Roles) ? "none" : c.Roles) + " ir_only=" + (c.IrOnly ? "true" : "false"));
                return c;
            }

            public IList<StrikeFighter> Fighters()
            {
                var res = new List<StrikeFighter>();
                try
                {
                    var snap = _a.Snapshot(true);
                    foreach (var t in snap.Tracks)
                    {
                        try
                        {
                            if (!t.HasId || !t.Lat.HasValue || !t.Lon.HasValue || !t.Live || t.Kind != "Air" || t.Side != "hostile") continue;
                            object v;
                            if (snap.VehicleById.TryGetValue(t.Id, out v) && _a.IsGone(v)) continue;
                            double kn = 0, course = 0;
                            if (t.Vx.HasValue && t.Vz.HasValue)
                            {
                                kn = Math.Sqrt(t.Vx.Value * t.Vx.Value + t.Vz.Value * t.Vz.Value) * OverviewMath.KnPerUnity;
                                course = kn < 1e-6 ? 0 : (Math.Atan2(t.Vx.Value, t.Vz.Value) * 180.0 / Math.PI + 360.0) % 360.0;
                            }
                            var f = new StrikeFighter { TrackId = t.Id, Lat = t.Lat.Value, Lon = t.Lon.Value, CourseDeg = course, SpeedKn = kn, Hostile = true, Helicopter = false, Globe = _a.WorldGlobeAt(t.Lat.Value, t.Lon.Value) };
                            string ini = _a.RrClassIniName(t.Id);
                            if (ini != null)
                            {
                                f.Identified = true; f.ClassIni = ini; f.ClassName = t.ClassName ?? ini;
                                string src;
                                f.RNm = AirGuardMath.ThreatRadiusNm(true, _a.AgClassAawNm(ini), _a.AgParams, out src);
                                f.RSrc = src;
                                var c = FighterClassOf(ini, src == "class" ? f.RNm : 0);
                                f.IsFighter = c.IsFighter; f.IrOnly = c.IrOnly; f.FighterSrc = c.Src;
                            }
                            res.Add(f);
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
                return res;
            }
        }
    }
}
