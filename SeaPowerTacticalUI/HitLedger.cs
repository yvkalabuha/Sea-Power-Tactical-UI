using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge.Ui
{
    /// <summary>One own hit on a track, as the game's weapon report records it (ADR-0064 item 20). Plain data: no game objects.</summary>
    internal sealed class HeHit
    {
        public int TrackId;
        public string Clock, AmmoFile, Weapon, Shooter;
        public HeAmmoKind Kind;
        public bool Dud;
        public HeWarhead Warhead;
        // T-342: the id of the weapon object (the same hash as strike probe msl id=), the damage the CIWS did to the missile (-1 = not read), its share of the health (-1 = no denominator), the mission time, the order of adding
        public int WeaponId;
        public double CiwsDmg = -1, DmgFrac = -1, T = -1;
        public int Seq;
    }

    /// <summary>The hits of the player's weapons by track (ADR-0064 item 11). Session only; the module prunes it by track, side and mission.</summary>
    internal sealed class HitLedger
    {
        private readonly Dictionary<int, List<HeHit>> _by = new Dictionary<int, List<HeHit>>();
        private readonly List<HeHit> _order = new List<HeHit>();   // T-342: every hit in the order of adding (Since); Prune drops the hits of the pruned tracks
        private int _next = 1;

        public int Version { get; private set; }

        public void Add(HeHit h)
        {
            if (h == null) return;
            h.Seq = _next++;
            _order.Add(h);
            List<HeHit> l;
            if (!_by.TryGetValue(h.TrackId, out l)) _by[h.TrackId] = l = new List<HeHit>();
            l.Add(h);
            Version++;
        }

        public int Count(int trackId)
        {
            List<HeHit> l;
            return _by.TryGetValue(trackId, out l) ? l.Count : 0;
        }

        /// <summary>The newest first, at most max.</summary>
        public IList<HeHit> Recent(int trackId, int max)
        {
            List<HeHit> l;
            var res = new List<HeHit>();
            if (!_by.TryGetValue(trackId, out l)) return res;
            for (int i = l.Count - 1; i >= 0 && res.Count < max; i--) res.Add(l[i]);
            return res;
        }

        /// <summary>All hits of the track, the oldest first.</summary>
        public IList<HeHit> All(int trackId)
        {
            List<HeHit> l;
            return _by.TryGetValue(trackId, out l) ? new List<HeHit>(l) : new List<HeHit>();
        }

        /// <summary>Drops the tracks for which keep(id) is false; returns how many tracks went.</summary>
        public int Prune(Func<int, bool> keep)
        {
            var gone = _by.Keys.Where(k => !keep(k)).ToList();
            foreach (var k in gone) _by.Remove(k);
            if (gone.Count > 0) { _order.RemoveAll(x => gone.Contains(x.TrackId)); Version++; }
            return gone.Count;
        }

        public void Clear()
        {
            if (_by.Count == 0) return;
            _by.Clear(); _order.Clear();
            Version++;
        }

        public int Tracks { get { return _by.Count; } }

        /// <summary>T-342: the hits added since the cursor (the oldest first); the cursor moves past them. The numbers are never reused: a cursor stays valid after Prune and Clear (the hits of before the Clear are gone, the new ones are returned).</summary>
        public IList<HeHit> Since(ref int cursor)
        {
            var res = new List<HeHit>();
            foreach (var h in _order) if (h.Seq >= cursor) res.Add(h);
            cursor = _next;
            return res;
        }

        public static string SelfTest()
        {
            string err = null;
            Action<bool, string> chk = (c, m) => { if (!c && err == null) err = m; };
            var l = new HitLedger();
            chk(l.Count(1) == 0 && l.Recent(1, 20).Count == 0 && l.All(1).Count == 0 && l.Version == 0, "empty");
            for (int i = 0; i < 25; i++) l.Add(new HeHit { TrackId = 1, Weapon = "w" + i });
            l.Add(new HeHit { TrackId = 2, Weapon = "other" });
            chk(l.Count(1) == 25 && l.Count(2) == 1, "count counts all");
            var r = l.Recent(1, 20);
            chk(r.Count == 20 && r[0].Weapon == "w24" && r[19].Weapon == "w5", "recent: newest first, at most 20");
            chk(l.All(1)[0].Weapon == "w0" && l.All(1).Count == 25, "all: oldest first");
            int v = l.Version;
            chk(l.Prune(id => id == 1) == 1 && l.Count(2) == 0 && l.Count(1) == 25 && l.Version == v + 1, "prune");
            chk(l.Prune(id => true) == 0 && l.Version == v + 1, "prune nothing keeps the version");
            l.Clear();
            chk(l.Count(1) == 0 && l.Version == v + 2 && l.Tracks == 0, "clear");
            l.Clear();
            chk(l.Version == v + 2, "clear empty");
            l.Add(null);
            chk(l.Version == v + 2, "null ignored");
            // T-342: Since
            var s = new HitLedger(); int cur = 0;
            chk(s.Since(ref cur).Count == 0 && cur == 1, "since empty");
            s.Add(new HeHit { TrackId = 1, Weapon = "a", WeaponId = 11 }); s.Add(new HeHit { TrackId = 2, Weapon = "b" }); s.Add(new HeHit { TrackId = 1, Weapon = "c" });
            var got = s.Since(ref cur);
            chk(got.Count == 3 && got[0].Weapon == "a" && got[1].Weapon == "b" && got[2].Weapon == "c" && got[0].WeaponId == 11 && got[0].CiwsDmg == -1 && got[0].DmgFrac == -1 && got[0].T == -1, "since: in the order of adding, defaults");
            chk(s.Since(ref cur).Count == 0, "since: nothing twice");
            s.Add(new HeHit { TrackId = 3, Weapon = "d" });
            int c2 = 0; chk(s.Since(ref c2).Count == 4, "since: a second reader from 0");
            var d2 = s.Since(ref cur);
            chk(d2.Count == 1 && d2[0].Weapon == "d", "since: only the new one");
            s.Prune(id => id != 2);
            s.Add(new HeHit { TrackId = 1, Weapon = "e" });
            var d3 = s.Since(ref cur);
            chk(d3.Count == 1 && d3[0].Weapon == "e", "since after prune");
            int c3 = 0; chk(s.Since(ref c3).Count == 4, "prune drops the hits of the pruned track from the order");
            s.Clear();
            chk(s.Since(ref cur).Count == 0, "since after clear: empty");
            s.Add(new HeHit { TrackId = 9, Weapon = "f" });
            var d4 = s.Since(ref cur);
            chk(d4.Count == 1 && d4[0].Weapon == "f", "since after clear: the cursor stays valid");
            return err;
        }
    }
}
