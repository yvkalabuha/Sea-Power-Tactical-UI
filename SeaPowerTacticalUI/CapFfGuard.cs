// T-333 (ADR-0129 section 9) stream E2: orchestrator of the friendly-fire guard (no reflection: the game side is IFfGame; tested on a fake in captest "t333-ff").
using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal interface IFfGame
    {
        bool ReadShot(object task, out FfShot shot);          // false = not ours / unreadable -> allow
        IList<FfFriend> Friends(double now);                  // cached 0.5 s
        IList<FfAlt> Alts(int shooterUid);                    // AAMs on board with seeker / range / left
        bool CanSwap(object task);                            // no guiding alignment
        string Swap(object task, string ammo);                // null = ok (sets _ammoId, _ammunition, _shotsToFire)
        double Now { get; }
    }

    internal sealed class CapFfGuard
    {
        private readonly IFfGame _g;
        private readonly Func<int, bool> _isCapAir;
        private readonly Func<bool> _on;
        private readonly Action<string> _log;
        private readonly Action<string, string> _evt;
        public FfBook Book { get; private set; }

        public CapFfGuard(IFfGame g, Func<int, bool> isCapAir, Func<bool> on, Action<string> log, Action<string, string> evt)
        {
            _g = g; _isCapAir = isCapAir; _on = on; _log = log; _evt = evt; Book = new FfBook();
        }

        private static string F(double v) { return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture); }

        /// <summary>The ExtraEngageVeto delegate: true = do not add this engage task. Never throws; anything unclear means "fire".</summary>
        public bool Veto(object engageTask)
        {
            try
            {
                if (_g == null || _isCapAir == null || (_on != null && !_on())) return false;
                FfShot s;
                if (!_g.ReadShot(engageTask, out s) || s == null || !s.Auto) return false;
                if (!_isCapAir(s.Shooter)) return false;
                s.CapAir = true;
                double now = _g.Now;
                IList<FfFriend> friends = _g.Friends(now);
                bool swapOn = CapFfMath.SwapEnabled && _g.CanSwap(engageTask);
                IList<FfAlt> alts = swapOn ? _g.Alts(s.Shooter) : null;
                FfDecision d = Book.Decide(s, friends, alts, swapOn, now);
                if (d.Op == "swap")
                {
                    string err = _g.Swap(engageTask, d.SwapTo);
                    if (err == null)
                    {
                        LogFf("swap", s, d, "from=" + s.Ammo + " to=" + d.SwapTo);
                        return false;
                    }
                    if (_log != null) _log("[Cap] ff swap failed uid=" + s.Shooter + " why=" + err);
                    d = Book.Decide(s, friends, null, false, now);   // the same pair, no swap: hold / timeout as usual
                }
                if (d.Log)
                {
                    if (d.Op == "hold") LogFf("hold", s, d, null);
                    else if (d.Op == "allow" && d.Why != null) LogFf("release", s, d, null);
                }
                return d.Op == "hold";
            }
            catch (Exception) { return false; }
        }

        private void LogFf(string op, FfShot s, FfDecision d, string extra)
        {
            try
            {
                string why = d.Why == null ? "" : " why=" + d.Why;
                if (_log != null)
                    _log("[Cap] ff " + op + " uid=" + s.Shooter + " target=" + s.TargetKey + (d.Friend >= 0 ? " friend=" + d.Friend + " fr_nm=" + F(d.FrNm) + " cone_deg=" + F(d.ConeDeg) : "")
                        + " ammo=" + (s.Ammo ?? "-") + " seeker=" + (s.Seeker ?? "-") + why + (op == "hold" ? "" : " held_s=" + F(d.HeldS)) + (extra == null ? "" : " " + extra));
                if (_evt != null)
                    _evt("cap_ff", "{\"op\":" + Json.Str(op) + ",\"uid\":" + s.Shooter + ",\"target\":" + s.TargetKey + ",\"friend\":" + d.Friend + ",\"fr_nm\":" + Json.Num(Math.Round(d.FrNm, 2))
                        + ",\"cone_deg\":" + Json.Num(Math.Round(d.ConeDeg, 1)) + ",\"held_s\":" + Json.Num(Math.Round(d.HeldS, 1)) + ",\"why\":" + (d.Why == null ? "null" : Json.Str(d.Why))
                        + ",\"ammo\":" + Json.Str(s.Ammo ?? "") + ",\"seeker\":" + Json.Str(s.Seeker ?? "") + "}");
            }
            catch (Exception) { }
        }
    }
}
