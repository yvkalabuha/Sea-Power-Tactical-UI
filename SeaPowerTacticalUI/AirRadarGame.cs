using System;
using System.Collections;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-241 (ADR-0099): the game side of the aircraft radar ledger. Main thread, through GameApi helpers and the descriptors of AirRadarSpecs.
    // Reads: the search radar lists of an own unit. Writes: RadarCmd (the same command as the "radar" button of the game and "Return control").
    internal sealed partial class GameApi
    {
        private AirRadarLedger _airRadar;

        /// <summary>One ledger per game API: the CAP and ASW controllers share it (a machine is in one window, but the memory is by uid).</summary>
        internal AirRadarLedger AirRadar { get { return _airRadar ?? (_airRadar = new AirRadarLedger(new AirRadarIoImpl(this))); } }

        /// <summary>Called at the end of CreateCapController / CreateAswController.</summary>
        internal void WireAirRadar()
        {
            try
            {
                var l = AirRadar;
                if (Cap != null) Cap.Radar = l;
                if (Asw != null) Asw.Radar = l;
            }
            catch { }
        }

        /// <summary>Mission left: the memory of the old mission is dropped without a game call (the objects may already be destroyed).</summary>
        internal void AirRadarMissionLeft() { if (_airRadar != null) _airRadar.Clear(); }

        /// <summary>Selfcheck: reading an unknown unit and switching it must answer, not throw. null = ok.</summary>
        internal string AirRadarSelfSmoke()
        {
            try
            {
                var io = new AirRadarIoImpl(this);
                var r = io.Read(-1, AirRadarKind.Air);
                if (r == null || r.Known) return "read of an unknown unit is Known";
                if (io.Set(-1, AirRadarKind.Air, true) == null) return "set of an unknown unit succeeded";
                var r2 = io.Read(-1, AirRadarKind.SearchSurface);
                if (r2 == null || r2.Known) return "read (surface) of an unknown unit is Known";
                return null;
            }
            catch (Exception e) { return "threw " + e.GetType().Name; }
        }

        private sealed class AirRadarIoImpl : IAirRadarIo
        {
            private readonly GameApi _a;
            public AirRadarIoImpl(GameApi a) { _a = a; }

            public double NowS
            {
                get
                {
                    try { double? t = _a.Frame != null ? _a.Frame.MissionTimeS : null; if (t.HasValue) return t.Value; } catch { }
                    return _a.Clock != null ? _a.Clock() : double.NaN;
                }
            }

            private static bool BoolOf(object o) { return o is bool && (bool)o; }

            /// <summary>Radars of a list that can be switched on: alive, not inoperable, turn-on allowed.</summary>
            private List<object> Operable(object list, bool surfaceAndAirOnly)
            {
                var res = new List<object>();
                var e = list as IEnumerable;
                if (e == null) return res;
                foreach (var rs in e)
                {
                    if (rs == null || !IsAlive(rs)) continue;
                    if (BoolOf(_a.RxGet(rs, AirRadarSpecs.RadarInoperable))) continue;
                    object allow = _a.Get(rs, AirRadarSpecs.SysAllowTurnOn);
                    if (allow is bool && !(bool)allow) continue;
                    if (surfaceAndAirOnly)
                    {
                        var rp = _a.Get(_a.Get(rs, AirRadarSpecs.RadarRadar), AirRadarSpecs.RadarRp);
                        var role = _a.Get(rp, AirRadarSpecs.RpRole);
                        if (role == null || role.ToString() != "SurfaceAndAir") continue;
                    }
                    res.Add(rs);
                }
                return res;
            }

            public AirRadarRead Read(int uid, string want)
            {
                var r = new AirRadarRead();
                try
                {
                    var u = _a.FindOwnUnit(uid);
                    if (u == null || !IsAlive(u)) return r;
                    var obp = _a.Get(u, AirRadarSpecs.ObjObp);
                    if (obp == null) return r;
                    r.Emcon = BoolOf(_a.RxGet(u, AirRadarSpecs.ObjEmcon));
                    List<object> chosen; string list;
                    if (want == "search_surface")
                    {
                        chosen = Operable(_a.Get(obp, AirRadarSpecs.ObpSurfaceList), false); list = "surface";
                        if (chosen.Count == 0) { chosen = Operable(_a.Get(obp, AirRadarSpecs.ObpAirList), true); list = "air"; }   // like EnableRadarsForSurfaceSearch of the game
                    }
                    else { chosen = Operable(_a.Get(obp, AirRadarSpecs.ObpAirList), false); list = "air"; }
                    r.Known = true; r.List = list; r.HasRadar = chosen.Count > 0;
                    foreach (var rs in chosen) if (BoolOf(_a.RxGet(rs, AirRadarSpecs.RadarIsOn))) { r.On = true; break; }
                }
                catch { r.Known = false; }
                return r;
            }

            public string Set(int uid, string list, bool on)
            {
                try
                {
                    if (!_a.Capabilities.Contains("set_radar")) return "unsupported";
                    var cmd = new Dictionary<string, object> { { "uid", (double)uid }, { "kind", list }, { "on", on } };
                    string res = _a.RadarCmd("0", cmd);
                    return res != null && res.Contains("\"ok\":true") ? null : "error";
                }
                catch (CapabilityException) { return "unsupported"; }
                catch (Exception) { return "error"; }
            }
        }
    }
}
