using System;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-308 (ADR-0120): the one door between the game's course numbers (grid degrees of the beta's LCC terrain: ObjectBase.Heading, CourseCommand,
    /// UnityVelocityVector, SetCourse) and the mod's true headings. String-member part for Common; the UI mod fills the hook (GridHeadingGame.cs).
    /// VoiceBotBridge (frozen) has no hook body: the call is removed by the compiler, conv = 0, the behaviour is as before.
    /// </summary>
    internal sealed partial class GameApi
    {
        /// <summary>Implemented only by the UI mod. convDeg = grid yaw of true north at the point; left untouched = 0.</summary>
        partial void GridConvHook(double lat, double lon, ref double convDeg);

        /// <summary>Grid convergence at the point; 0 without LCC, without the game method, without the hook or on any error.</summary>
        internal double GridConv(double lat, double lon)
        {
            double c = 0;
            try { GridConvHook(lat, lon, ref c); } catch { c = 0; }
            return double.IsNaN(c) || double.IsInfinity(c) ? 0 : c;
        }

        /// <summary>Convergence at the unit's own position; 0 when it has none.</summary>
        internal double GridConvOf(object u)
        {
            double lat, lon, h;
            try { return OwnPos(u, out lat, out lon, out h) ? GridConv(lat, lon) : 0; } catch { return 0; }
        }

        /// <summary>ObjectBase.Heading as a true heading 0..360; null when unknown.</summary>
        internal double? GridHeadingTrueOf(object u)
        {
            var h = D(RxGet(u, "Heading"));
            return h.HasValue ? (double?)GridHeadingMath.TrueFromGrid(h.Value, GridConvOf(u)) : null;
        }

        /// <summary>CourseCommand.GetDemandedCourse as a true course 0..360; null when there is no command.</summary>
        internal double? GridDemandedCourseTrueOf(object u)
        {
            var cc = RxGet(u, "CourseCommand");
            if (cc == null) return null;
            var dc = D(Call(cc, "GetDemandedCourse", u));
            return dc.HasValue ? (double?)GridHeadingMath.TrueFromGrid(dc.Value, GridConvOf(u)) : null;
        }

        /// <summary>The number to hand to SetCourse(float) so that the unit steers the true course.</summary>
        internal float GridYawFromTrue(object u, double trueDeg)
        {
            return (float)GridHeadingMath.GridFromTrue(trueDeg, GridConvOf(u));
        }

        /// <summary>A grid-axes velocity member (x east-grid, z north-grid; raw Unity units) rotated into true axes at the point. False when there is no vector.</summary>
        internal bool GridVelTrueOf(object holder, string member, double lat, double lon, out double vx, out double vz)
        {
            vx = vz = 0;
            var vel = Get(holder, member);
            var x = D(Get(vel, "x")); var z = D(Get(vel, "z"));
            if (!x.HasValue || !z.HasValue) return false;
            GridHeadingMath.RotateGridToTrue(x.Value, z.Value, GridConv(lat, lon), out vx, out vz);
            return true;
        }
    }
}
