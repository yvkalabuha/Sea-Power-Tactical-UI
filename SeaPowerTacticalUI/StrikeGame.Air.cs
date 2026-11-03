using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 phase B (L1): the orders of the strike game layer: a route to the wait point, an orbit, the way home. The route goes through the same waypoint path as the retreat helper
    // (AgSteerImpl.Goto: the ASW game layer), the first call of an aircraft remembers its route for "Return control". Every method catches inside; null = ok, else an error code.
    internal sealed partial class GameApi
    {
        private sealed partial class StrikeGameImpl
        {
            private readonly HashSet<int> _remembered = new HashSet<int>();

            private string Placed(int uid, string err)
            {
                if (err == null) { try { _a.WpqNotePlaced(uid, "strike"); } catch (Exception) { } }
                return err;
            }

            private void Remember(int uid)
            {
                if (!_remembered.Add(uid)) return;
                try { _a.AgSteerGame().Remember(uid); } catch (Exception) { }
            }

            public string Goto(int uid, IList<GeoPt> pts, double altFt)
            {
                try
                {
                    if (pts == null || pts.Count == 0) return "bad_args";
                    Remember(uid);
                    return Placed(uid, _a.AgSteerGame().Goto(uid, pts, (int)Math.Max(0, altFt)));
                }
                catch (Exception e) { return "goto_failed:" + e.GetType().Name; }
            }

            public string Orbit(int uid, GeoPt center, double radiusNm)
            {
                try
                {
                    Remember(uid);
                    return Placed(uid, _a.AgSteerGame().Orbit(uid, center, radiusNm, true));
                }
                catch (Exception e) { return "orbit_failed:" + e.GetType().Name; }
            }

            /// <summary>Home, as the return of the CAP (the game's ReturnToBase order).</summary>
            public string Rtb(int uid)
            {
                try
                {
                    if (_a.FindOwnUnit(uid) == null) return "unit_not_found";
                    string ack = _a.ReturnToBaseCmd("0", new Dictionary<string, object> { { "uid", (double)uid } });
                    var d = Json.Parse(ack) as Dictionary<string, object>;
                    object ok, err;
                    if (d != null && d.TryGetValue("ok", out ok) && ok is bool && (bool)ok) return null;
                    return d != null && d.TryGetValue("error", out err) && err is string ? (string)err : "failed";
                }
                catch (Exception e) { return e.GetType().Name; }
            }
        }
    }
}
