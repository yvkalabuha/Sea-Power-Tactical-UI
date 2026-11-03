using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-363: which ledger hits the strike counter keeps. A hit is kept when its track was ever seen as a surface ship. The earlier filter asked "is the track in the current ship list" and the list drops
    /// destroyed contacts, so the hit that sinks a ship (and every hit of the same tick) was thrown away and the cursor moved past it (ledger_gap of the 0.30.40/41 series: `strike kill ... hits_before=0`
    /// while the game log shows the hits). Pure: no game types.
    /// </summary>
    internal sealed class StrikeHitFilter
    {
        private readonly HashSet<int> _seen = new HashSet<int>();

        public int Count { get { return _seen.Count; } }

        /// <summary>A surface track seen in the table, alive or already destroyed.</summary>
        public void NoteSurface(int trackId) { _seen.Add(trackId); }

        public bool Keep(bool missile, int trackId) { return missile && _seen.Contains(trackId); }

        public void Clear() { _seen.Clear(); }

        public static string SelfTest()
        {
            string err = null;
            System.Action<bool, string> chk = (c, m) => { if (!c && err == null) err = m; };
            var f = new StrikeHitFilter();
            chk(!f.Keep(true, 7002), "unknown track dropped");
            f.NoteSurface(7002); f.NoteSurface(7002);
            chk(f.Count == 1 && f.Keep(true, 7002), "seen surface track kept");
            chk(!f.Keep(false, 7002), "gun/torpedo hit dropped");
            chk(!f.Keep(true, 1004), "air track (never noted) dropped");
            // the ship is destroyed: it leaves the ship list but was noted while alive, its hits stay
            chk(f.Keep(true, 7002), "destroyed ship still kept");
            f.Clear();
            chk(!f.Keep(true, 7002) && f.Count == 0, "clear");
            return err;
        }
    }
}
