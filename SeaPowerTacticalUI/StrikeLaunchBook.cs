using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 / ADR-0132 section 6: the launches the mod itself expects (uid, target, ammo, a window around the planned time); the veto lets only these pass. Pure.
    internal sealed class StrikeLaunchBook
    {
        private sealed class Entry { public int Uid, TargetId; public string Ammo; public double T, WinS; }
        private readonly List<Entry> _e = new List<Entry>();

        public int Count { get { return _e.Count; } }

        public void Expect(int uid, int targetId, string ammo, double t, double winS = 60)
        {
            _e.RemoveAll(x => x.Uid == uid && x.TargetId == targetId && x.Ammo == ammo);
            _e.Add(new Entry { Uid = uid, TargetId = targetId, Ammo = ammo, T = t, WinS = winS });
        }

        /// <summary>A launch of this shooter at this target with this ammo is expected at the time (+-WinS). targetId -1 matches any target; ammo null matches any.</summary>
        public bool Expected(int uid, int targetId, string ammo, double now)
        {
            foreach (var x in _e)
            {
                if (x.Uid != uid || Math.Abs(now - x.T) > x.WinS) continue;
                if (targetId >= 0 && x.TargetId >= 0 && x.TargetId != targetId) continue;
                if (ammo != null && x.Ammo != null && x.Ammo != ammo) continue;
                return true;
            }
            return false;
        }

        public void Clear(int uid = -1) { if (uid < 0) _e.Clear(); else _e.RemoveAll(x => x.Uid == uid); }

        /// <summary>The veto rule: only an automatic attack of a watched shooter with an anti-ship missile that the mod did not plan.</summary>
        public static bool Veto(bool auto, bool watched, bool isAsm, bool expected) { return auto && watched && isAsm && !expected; }

        public static string SelfTest()
        {
            var b = new StrikeLaunchBook();
            b.Expect(7, 100, "usn_agm-84c", 1000);
            if (b.Count != 1 || !b.Expected(7, 100, "usn_agm-84c", 1000) || !b.Expected(7, 100, "usn_agm-84c", 1060) || !b.Expected(7, 100, "usn_agm-84c", 940)) return "window";
            if (b.Expected(7, 100, "usn_agm-84c", 1061) || b.Expected(7, 100, "usn_agm-84c", 939)) return "outside the window";
            if (b.Expected(8, 100, "usn_agm-84c", 1000) || b.Expected(7, 101, "usn_agm-84c", 1000) || b.Expected(7, 100, "fr_am-39", 1000)) return "other uid / target / ammo";
            if (!b.Expected(7, -1, null, 1000)) return "wildcards";
            b.Expect(7, 100, "usn_agm-84c", 1500);   // same key: replaced
            if (b.Count != 1 || b.Expected(7, 100, "usn_agm-84c", 1000)) return "replace";
            b.Expect(8, 100, "usn_agm-84c", 1500);
            b.Clear(7);
            if (b.Count != 1 || b.Expected(7, 100, "usn_agm-84c", 1500) || !b.Expected(8, 100, "usn_agm-84c", 1500)) return "clear uid";
            b.Clear();
            if (b.Count != 0) return "clear all";
            for (int m = 0; m < 16; m++)
            {
                bool auto = (m & 1) != 0, watched = (m & 2) != 0, asm = (m & 4) != 0, exp = (m & 8) != 0;
                if (Veto(auto, watched, asm, exp) != (auto && watched && asm && !exp)) return "veto table " + m;
            }
            return null;
        }
    }
}
