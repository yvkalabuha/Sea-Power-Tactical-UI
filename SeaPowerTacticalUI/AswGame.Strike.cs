using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-173 / ADR-0074: the game side of the strike group: what the player knows about a submarine track, own submarines and ships, own torpedoes in the water,
    // the hit ledger, the drop window of the torpedo. Every read is optional (a missing member = a line of degradation in ADR-0074 s.10, never an exception).
    internal sealed partial class GameApi
    {
        private sealed partial class AswGameImpl
        {
            private readonly Dictionary<int, double> _liveSince = new Dictionary<int, double>();
            private readonly Dictionary<int, double> _lastFix = new Dictionary<int, double>();

            /// <summary>The fields of a submarine track the strike group reads: class, classification, biologic mark, posture, motion, depth, the last live fix.</summary>
            private void StrikeFill(AswContact c, OvTrack t, object vv, string objCls)
            {
                try
                {
                    c.ClassName = t.ClassName; c.Classified = t.Classified;
                    c.MapPosture = t.MapPosture; c.VmPosture = t.VmPosture;
                    c.Biologic = AswStrikeMath.BiologicName(t.Class, t.ClassName, t.ObjType, objCls);
                    if (t.Vx.HasValue && t.Vz.HasValue)
                    {
                        double sp = OverviewMath.SpeedKn(t.Vx.Value, t.Vz.Value);
                        c.SpeedKn = sp;
                        if (sp >= 0.5) c.CourseDeg = OverviewMath.CourseDeg(t.Vx.Value, t.Vz.Value);
                    }
                    if (vv != null && _a.Present(SmartAttackSpecs.VehAltitude))
                    {
                        var alt = _a.Get(vv, SmartAttackSpecs.VehAltitude);   // leak-ok: the depth estimate of the player's track
                        if (alt != null)
                        {
                            var est = D(_a.Get(_a.Get(alt, SmartAttackSpecs.SourcedValue), SmartAttackSpecs.EstimateOf));
                            if (est.HasValue) { c.DepthFt = -est.Value * GameApi.SaFtPerUnity; c.Surfaced = c.DepthFt <= 10; }
                        }
                    }
                    c.MadFixS = AswContact.MadNotRead;   // no reader of the game's MAD marks exists: ADR-0072 s.5.2 / ADR-0123
                    double now = Now;
                    if (t.Live) { _lastFix[t.Id] = now; c.FixS = now; }
                    else { double f; c.FixS = _lastFix.TryGetValue(t.Id, out f) ? f : -1; }
                }
                catch { }
            }

            /// <summary>Own submarines (true place) and allied ones (only as the friendly track of the player's map); null = the submarine class is not readable.</summary>
            public IList<AswOwnSub> OwnSubs()
            {
                if (!_a.Present(SmartAttackSpecs.SubType)) return null;
                var res = new List<AswOwnSub>();
                try
                {
                    foreach (var u in _a.OwnUnits())
                    {
                        if (!_a.IsOfType(u, SmartAttackSpecs.SubType) || !GameApi.IsAlive(u)) continue;
                        double lat, lon, h;
                        if (!_a.OwnPos(u, out lat, out lon, out h)) continue;
                        res.Add(new AswOwnSub { Uid = _a.UidOf(u), Name = _a.NameOf(u), Lat = lat, Lon = lon });
                    }
                    var snap = _a.Snapshot(false);
                    foreach (var t in snap.Tracks)
                    {
                        if ((t.Kind != "Submarine" && t.ObjType != "Submarine") || !t.Lat.HasValue || !t.Lon.HasValue) continue;
                        if (t.Side == null || (t.Side != "friendly" && t.Side != "allied")) continue;
                        res.Add(new AswOwnSub { TrackId = t.Id, Name = "Sub #" + t.Id, Lat = t.Lat.Value, Lon = t.Lon.Value, Allied = true });
                    }
                }
                catch { return null; }
                return res;
            }

            public IList<AswShipPos> OwnShips()
            {
                var res = new List<AswShipPos>();
                try
                {
                    foreach (var o in CourseTargets())
                    {
                        if (o.Kind != "ship") continue;
                        var t = Target(o.Uid);
                        if (t != null) res.Add(new AswShipPos { Uid = t.Uid, Name = t.Name, Lat = t.Lat, Lon = t.Lon });
                    }
                }
                catch { }
                return res;
            }

            /// <summary>The player's torpedoes in the water (launched weapons of own aircraft and submarines); the target is not readable: -1. null = the torpedo class is not readable.</summary>
            public IList<AswTorpObs> TorpedoesInWater()
            {
                if (!_a.Present(SmartAttackSpecs.TorpType) || !_a.Present(SmartAttackSpecs.WsLaunched)) return null;
                var res = new List<AswTorpObs>();
                try
                {
                    var units = new List<object>(_a.AirUnitsTick());
                    foreach (var u in _a.OwnUnits()) if (_a.IsOfType(u, SmartAttackSpecs.SubType)) units.Add(u);
                    foreach (var u in units)
                    {
                        var systems = Get(Get(u, AirOpsSpecs.ObjObp), SmartAttackSpecs.ObpWeaponSystems) as IEnumerable;
                        if (systems == null) continue;
                        foreach (var ws in systems)
                        {
                            var l = Get(ws, SmartAttackSpecs.WsLaunched) as IEnumerable;
                            if (l == null) continue;
                            foreach (var w in l)
                            {
                                if (w == null || !GameApi.IsAlive(w) || !_a.IsOfType(w, SmartAttackSpecs.TorpType) || BoolOf(Get(w, SmartAttackSpecs.ObjIsDestroyed))) continue;
                                double lat = 0, lon = 0, h;
                                _a.OwnPos(w, out lat, out lon, out h);
                                res.Add(new AswTorpObs { Id = _a.UidOf(w), LauncherUid = _a.UidOf(u), Lat = lat, Lon = lon });
                            }
                        }
                    }
                }
                catch { return null; }
                return res;
            }

            public int HitsOn(int trackId) { var l = _a.HeLedger; return l == null ? -1 : l.Count(trackId); }

            /// <summary>The ASW torpedo the aircraft would drop (the row AswWeaponIndex picks): the launch window of its parameters; null = unknown.</summary>
            public AswDropSpec DropSpec(int uid)
            {
                try
                {
                    var u = Unit(uid);
                    if (u == null) return null;
                    var rows = _a.WeaponRows(u);
                    int k = AswMath.AswWeaponIndex(rows);
                    if (k < 0) return null;
                    var spec = new AswDropSpec { Ammo = rows[k][0] };
                    double rng;
                    if (double.TryParse(rows[k][6], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out rng)) spec.MaxNm = rng;
                    var ap = _a.CallStatic(AswSpecs.ApGetShared, spec.Ammo);
                    var win = ap == null ? null : Get(ap, AswSpecs.ApLaunchAltitudes);
                    if (win != null)
                    {
                        var lo = D(Get(win, AswSpecs.Vec2X)); var hi = D(Get(win, AswSpecs.Vec2Y));
                        if (lo.HasValue && hi.HasValue && hi.Value > lo.Value) { spec.MinAltFt = lo.Value * FeetPerUnitAlt; spec.MaxAltFt = hi.Value * FeetPerUnitAlt; }
                    }
                    return spec;
                }
                catch { return null; }
            }
        }
    }
}
