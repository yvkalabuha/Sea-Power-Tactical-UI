using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 phase B (L3): land under a point (the wait point and the launch points must be over the sea). The terrain heights of the game (TerrainSampler) through the cache of the intercept
    // module: no sampler yet = sea (asked again at the next call), never an exception.
    internal sealed partial class GameApi
    {
        private TerrainHeightCache _strikeHeights;

        internal bool StrikeIsLand(GeoPt g)
        {
            try
            {
                if (_strikeHeights == null)
                {
                    var sampler = TerrainSampler(false);
                    if (sampler == null) return false;
                    _strikeHeights = new TerrainHeightCache(sampler, null, m => Log("[Strike] " + m));
                }
                return _strikeHeights.HeightM(g) > 2.0;   // the sea floor is negative; open sea = -200 when there is no data
            }
            catch (Exception) { return false; }
        }

        private void StrikeLandLeave() { if (_strikeHeights != null) _strikeHeights.Clear(); _strikeHeights = null; }

        private sealed partial class StrikeGameImpl
        {
            public bool IsLand(GeoPt p) { return _a.StrikeIsLand(p); }
        }
    }
}
