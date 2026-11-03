using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-341 phase B: the glue of the controller to the rest of the mod: the air tick, "Return control", the save of the groups next to the game save.
    internal sealed partial class GameApi
    {
        internal string StrikeSaveDir;
        private SaveSlotStore _strikeSlots;
        private string _strikeSlotsDir;
        private string _strikePendingJson;
        private bool _strikeRestoring;

        private SaveSlotStore StrikeSlots
        {
            get
            {
                if (_strikeSlots == null || _strikeSlotsDir != StrikeSaveDir) { _strikeSlots = new SaveSlotStore(StrikeSaveDir, Log); _strikeSlotsDir = StrikeSaveDir; }
                return _strikeSlots;
            }
        }

        /// <summary>One step (1 s of game time) of the strike controller, called by the air tick between BeginTick and EndTick of the deck broker (after the CAP).</summary>
        internal void AirTickStrike(double t)
        {
            var c = Strike;
            if (c == null) return;
            try { if (Frame.InMission) c.Tick(t); }
            catch (Exception e) { try { StrikeLog("[Strike] tick error: " + e.GetType().Name + ": " + e.Message); } catch { } }
        }

        /// <summary>"Return control" participant (ReleaseRegistry): the aircraft leaves its strike group, no orders are given.</summary>
        internal void StrikeRelease(int uid)
        {
            try { if (_strikeRestoring || Strike == null) return; Strike.Release(uid); }
            catch { }
        }

        /// <summary>From OnGameSaved (main thread): the groups go to the file next to the save; none = the slot is cleared.</summary>
        internal void StrikeOnGameSaved(string savPath, SavStamp stamp)
        {
            var c = Strike;
            if (c == null || string.IsNullOrEmpty(StrikeSaveDir) || string.IsNullOrEmpty(savPath)) return;
            try
            {
                string json = c.Export(stamp);
                if (json == null) { if (StrikeSlots.Delete(savPath)) Log("[Strike] strike save slot cleared " + SaveSlotStore.Key(savPath)); return; }
                if (StrikeSlots.Write(savPath, json, t => { string e; return StrikeSave.Parse(t, out e) == null ? (e ?? "bad file") : null; })) Log("[Strike] strike save written " + StrikeSlots.PathFor(savPath));
            }
            catch (Exception e) { try { Log("[Strike] save failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
        }

        /// <summary>Mission entry: reads the file of the loaded .sav when it is fresh; the groups are applied when the world has settled (ApplyDeferredRestore).</summary>
        internal void StrikeEnterMission(string savPathOrNull, double missionT, SavStamp loaded)
        {
            _strikePendingJson = null;
            if (Strike != null) Strike.Ready = true;
            if (Strike == null || savPathOrNull == null || string.IsNullOrEmpty(StrikeSaveDir)) return;
            try
            {
                StrikeSaveData d = null; string err = null, src;
                string text = StrikeSlots.Read(savPathOrNull, t => { d = StrikeSave.Parse(t, out err); return d == null ? (err ?? "bad file") : null; }, out src);
                if (text == null) { if (err != null) Log("[Strike] strike restore: bad file (" + err + ")"); return; }
                string why = SaveFreshness.Check(d.Sav, d.MissionT, loaded, missionT);
                if (why != null) { Log("[Strike] strike restore: file is stale (" + why + ")"); return; }
                if (d.Groups.Count == 0) return;
                _strikePendingJson = text;
                Strike.Ready = false;   // the ring and the window wait for the restore
                Log("[Strike] strike restore: file read n=" + d.Groups.Count + " from=" + src);
            }
            catch (Exception e) { Log("[Strike] strike restore failed: " + e.Message); }
        }

        /// <summary>The post-load gate opened: the groups of the loaded save come back (stale, loaded); the tick then sends the aircraft to the wait point.</summary>
        internal void StrikeApplyDeferred()
        {
            var c = Strike;
            string json = _strikePendingJson;
            if (c == null) return;
            c.Ready = true;
            if (json == null) return;
            _strikePendingJson = null;
            try { string err; c.Import(json, out err); if (err != null) Log("[Strike] strike restore: " + err); }
            catch (Exception e) { Log("[Strike] strike restore failed: " + e.GetType().Name + ": " + e.Message); }
        }
    }
}
