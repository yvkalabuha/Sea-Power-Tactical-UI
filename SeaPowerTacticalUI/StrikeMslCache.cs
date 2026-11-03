using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-353: the missiles of a strike by their carrier. The game lists the missiles through the weapon systems of the carrier; when the carrier is shot down the list is gone, but the missiles keep flying
    /// and hit (r3 of T-341: "AS-4 of ... hits" after the death of the Tu-22M2). The adapter notes every missile it sees and keeps following the object after the carrier is lost.
    /// Pure: no game types (the handle is the game object, or the record of the fake world).
    /// </summary>
    internal sealed class StrikeMslCache
    {
        private sealed class Entry { public int Uid, Id; public object Handle; public double Lat = double.NaN, Lon = double.NaN; }

        private readonly Dictionary<int, Entry> _byId = new Dictionary<int, Entry>();

        public int Count { get { return _byId.Count; } }

        /// <summary>A missile seen in the launched list of the carrier; the handle is the game object (or the fake's record). The same id is never duplicated.</summary>
        public void Note(int uid, int id, object handle)
        {
            Entry e;
            if (_byId.TryGetValue(id, out e)) { e.Uid = uid; e.Handle = handle; return; }
            _byId[id] = new Entry { Uid = uid, Id = id, Handle = handle };
        }

        /// <summary>The last position read of the missile (a position the game refuses later for an orphan missile is replaced by this one).</summary>
        public void SetPos(int id, double lat, double lon)
        {
            Entry e;
            if (_byId.TryGetValue(id, out e)) { e.Lat = lat; e.Lon = lon; }
        }

        public bool LastPos(int id, out double lat, out double lon)
        {
            Entry e;
            lat = lon = 0;
            if (!_byId.TryGetValue(id, out e) || double.IsNaN(e.Lat)) return false;
            lat = e.Lat; lon = e.Lon;
            return true;
        }

        /// <summary>All missiles noted for the carrier that are still alive; the dead ones are dropped. Works when the carrier itself is gone.</summary>
        public IList<KeyValuePair<int, object>> Live(int uid, Func<object, bool> alive)
        {
            var res = new List<KeyValuePair<int, object>>();
            foreach (var e in _byId.Values.Where(x => x.Uid == uid).OrderBy(x => x.Id).ToList())
            {
                bool ok;
                try { ok = e.Handle != null && alive(e.Handle); } catch (Exception) { ok = false; }
                if (!ok) { _byId.Remove(e.Id); continue; }
                res.Add(new KeyValuePair<int, object>(e.Id, e.Handle));
            }
            return res;
        }

        /// <summary>Drops the dead missiles of every carrier (called once per InFlight; bounds the memory without an interface change).</summary>
        public void Prune(Func<object, bool> alive)
        {
            foreach (var e in _byId.Values.ToList())
            {
                bool ok;
                try { ok = e.Handle != null && alive(e.Handle); } catch (Exception) { ok = false; }
                if (!ok) _byId.Remove(e.Id);
            }
        }

        public void Clear() { _byId.Clear(); }

        public static string SelfTest()
        {
            var c = new StrikeMslCache();
            var dead = new HashSet<object>();
            Func<object, bool> alive = h => !dead.Contains(h);
            var m = new object[] { new object(), new object(), new object(), new object(), new object() };
            for (int i = 0; i < 3; i++) c.Note(112, 113 + i, m[i]);
            if (c.Live(112, alive).Count != 3) return "three noted";
            c.Note(112, 113, m[0]);
            if (c.Count != 3 || c.Live(112, alive).Count != 3) return "a repeated note does not duplicate";
            c.Note(116, 117, m[3]); c.Note(116, 118, m[4]);
            if (c.Live(112, alive).Count != 3 || c.Live(116, alive).Count != 2) return "carriers are not mixed";
            if (c.Live(999, alive).Count != 0) return "unknown carrier";
            // the carrier is gone: no more Note, the missiles are still followed
            if (c.Live(112, alive).Count != 3) return "the missiles outlive the carrier";
            dead.Add(m[1]);
            var l = c.Live(112, alive);
            if (l.Count != 2 || l.Any(kv => kv.Key == 114)) return "a dead missile is dropped";
            if (c.Count != 4) return "the dead one is removed from the cache " + c.Count;
            c.SetPos(113, 40.5, 10.25);
            double la, lo;
            if (!c.LastPos(113, out la, out lo) || la != 40.5 || lo != 10.25) return "last position";
            if (c.LastPos(115, out la, out lo)) return "no position noted";
            dead.Add(m[0]); dead.Add(m[3]);
            c.Prune(alive);
            if (c.Count != 2 || c.Live(112, alive).Count != 1 || c.Live(116, alive).Count != 1) return "prune of every carrier " + c.Count;
            c.Clear();
            if (c.Count != 0) return "clear";
            return null;
        }
    }
}
