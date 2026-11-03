using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-308 (ADR-0120): UI-mod part of the grid-to-true layer. Every course / speed vector of the game that the mod reads or writes goes through the
    /// Grid* members (this file and Common/GridHeading.cs); the guard test tests/test_sea_power_t308_grid_guard.py forbids the direct reads.
    /// Main thread only (the game's Utils method reads Burst shared statics).
    /// </summary>
    internal sealed partial class GameApi
    {
        private const double GridCellDeg = 0.05;
        private const int GridCacheMax = 4096;
        private readonly Dictionary<long, double> _gridConv = new Dictionary<long, double>();
        private object _gridMission;        // the mission the cache belongs to (identity); a new mission clears the cache
        private bool _gridLogged;
        private string _gridState = "n/a";  // on | off | n/a (the last probe)

        partial void GridConvHook(double lat, double lon, ref double convDeg)
        {
            if (double.IsNaN(lat) || double.IsNaN(lon) || double.IsInfinity(lat) || double.IsInfinity(lon)) return;
            if (Environment.CurrentManagedThreadId != _ownerThread) return;
            if (!Present(GridSpecs.YawToTrue) || !Present(GridSpecs.GeoCtor)) return;
            var mission = MissionRef();
            if (!ReferenceEquals(mission, _gridMission)) { _gridConv.Clear(); _gridMission = mission; _gridLogged = false; _gridState = "n/a"; }
            if (_gridConv.Count > GridCacheMax) _gridConv.Clear();
            long key = ((long)Math.Round(lat / GridCellDeg) << 32) ^ (long)(uint)(int)Math.Round(lon / GridCellDeg);
            double c;
            if (_gridConv.TryGetValue(key, out c)) { convDeg = c; return; }
            c = 0;
            try
            {
                double cl = Math.Round(lat / GridCellDeg) * GridCellDeg, co = Math.Round(lon / GridCellDeg) * GridCellDeg;
                var geo = New(GridSpecs.GeoCtor, cl, co, 0.0);
                float t0 = Convert.ToSingle(CallStatic(GridSpecs.YawToTrue, 0f, geo));
                c = GridHeadingMath.ConvFromProbe(t0);
                if (Math.Abs(c) > 1e-6) _gridState = "on"; else if (_gridState != "on") _gridState = "off";   // the game returns the argument unchanged when LCC is off: conv 0
            }
            catch { c = 0; _gridState = "n/a"; }
            _gridConv[key] = c;
            convDeg = c;
        }

        /// <summary>A heading of the object through the given spec, as a true heading; null when unknown.</summary>
        internal double? GridHeadingTrue(object u, MemberSpec headingSpec)
        {
            var h = D(RxGet(u, headingSpec));
            if (!h.HasValue) return null;
            double conv = GridConvOf(u);
            if (!_gridLogged && OwnObject(u)) GridLogOnce(u);   // the first own unit read of the mission writes the [Grid] line
            return GridHeadingMath.TrueFromGrid(h.Value, conv);
        }

        /// <summary>The unit's course command: the command object (for ReferenceEquals) and its demanded course as a true course (the same fallback as before).</summary>
        internal object GridCourseCmdTrue(object u, out double? trueDeg)
        {
            trueDeg = null;
            var cc = RxGet(u, EvasionSpecs.ObjCourseCommand);
            if (cc == null) return null;
            double? g = null;
            try { g = D(Call(cc, EvasionSpecs.CcGetDemandedCourse, u)); } catch { }
            trueDeg = g.HasValue ? (double?)GridHeadingMath.TrueFromGrid(g.Value, GridConvOf(u)) : DemandedCourse(u);
            return cc;
        }

        /// <summary>SetCourse(true course converted to grid) -> CourseCommand. Returns the command; MissingMemberException is not caught.</summary>
        internal object GridWriteCourseTrue(object u, double trueDeg)
        {
            var cmd = New(EvasionSpecs.SetCourseCtor, GridYawFromTrue(u, GridHeadingMath.Norm360(trueDeg)));
            RxSet(u, EvasionSpecs.ObjCourseCommand, cmd);
            return cmd;
        }

        /// <summary>A grid velocity vector of a track or an object (raw Unity units) in true axes at the point.</summary>
        internal bool GridVelTrue(object holder, MemberSpec vel, MemberSpec x, MemberSpec z, double lat, double lon, out double vx, out double vz)
        {
            return GridVelRead(Get(holder, vel), x, z, lat, lon, out vx, out vz);
        }

        /// <summary>The same for an OwnOnly velocity member: only the player's own object answers.</summary>
        internal bool GridVelTrueOwn(object holder, MemberSpec vel, MemberSpec x, MemberSpec z, double lat, double lon, out double vx, out double vz)
        {
            return GridVelRead(GetOwn(holder, vel), x, z, lat, lon, out vx, out vz);
        }

        private bool GridVelRead(object v, MemberSpec x, MemberSpec z, double lat, double lon, out double vx, out double vz)
        {
            vx = vz = 0;
            var gx = D(Get(v, x)); var gz = D(Get(v, z));
            if (!gx.HasValue || !gz.HasValue) return false;
            GridHeadingMath.RotateGridToTrue(gx.Value, gz.Value, GridConv(lat, lon), out vx, out vz);
            return true;
        }

        /// <summary>Once per mission: "[Grid] lcc=on|off|n/a conv@name=x.xx" (phase D reads the real size of the convergence from it).</summary>
        internal void GridLogOnce(object ownRef)
        {
            try
            {
                if (ownRef == null) return;
                double c = GridConvOf(ownRef);   // fills _gridMission / _gridState
                if (_gridLogged) return;
                _gridLogged = true;
                Log("[Grid] lcc=" + _gridState + " conv@" + NameOf(ownRef) + "=" + c.ToString("F2", CultureInfo.InvariantCulture));
            }
            catch { }
        }
    }
}
