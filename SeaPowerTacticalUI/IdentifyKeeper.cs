using System;
using System.IO;
using System.Text;

namespace VoiceBotBridge
{
    // T-111: persistence of the identify statuses next to the game save (same scheme as the ASW file). No game types: testable in idtest.
    internal sealed class IdentifyKeeper
    {
        private readonly IdentifyStore _store;
        private readonly string _dir;
        private readonly Action<string> _log;
        private string _lastSave;

        public IdentifyKeeper(IdentifyStore store, string saveDir, Action<string> log)
        {
            _store = store; _dir = saveDir; _log = log ?? (s => { });
        }

        public string LastSave { get { return _lastSave; } }

        /// <summary>T-198: true while the Harmony hook on the game save works; the mark observer then writes nothing.</summary>
        public bool SaveHookActive;

        private static IdentifySaveData Stamped(IdentifySaveData d, SavStamp stamp) { d.Sav = stamp; return d; }

        private SaveSlotStore _slots;
        private SaveSlotStore Slots { get { return _slots ?? (_slots = new SaveSlotStore(_dir, _log)); } }

        /// <summary>T-130: the mission was left: the statuses of its tracks go (a new load restores them from the file).</summary>
        public void OnMissionLeft()
        {
            _store.Clear();
            _lastSave = null;
        }

        /// <summary>A mission was entered (new ObjectsManager): clear, then restore from the .sav it came from (when the file is fresh).</summary>
        public void OnMissionChanged(string savPathOrNull, double missionTimeS) { OnMissionChanged(savPathOrNull, missionTimeS, null); }

        /// <summary>T-198 (ADR-0086): <paramref name="loaded"/> = header stamp of the loaded .sav, null = unknown (the T-122 rule: file time within 5 s of the mission).</summary>
        public void OnMissionChanged(string savPathOrNull, double missionTimeS, SavStamp loaded)
        {
            _store.Clear();
            _lastSave = savPathOrNull;
            if (savPathOrNull == null || _dir == null) return;
            try
            {
                IdentifySaveData d = null; string err = null, src;
                string text = Slots.Read(savPathOrNull, t => { d = IdentifySave.Parse(t, out err); return d == null ? (err ?? "bad file") : null; }, out src);
                if (text == null) { if (err != null) _log("identify restore: bad file (" + err + ")"); return; }
                string why = SaveFreshness.Check(d.Sav, d.MissionTimeS, loaded, missionTimeS);
                if (why != null) _log("identify restore: file is stale (" + why + ")");
                else
                {
                    double dt = SaveFreshness.Shift(d.MissionTimeS, missionTimeS);   // the game does not restore the mission clock
                    foreach (var it in d.Items) it.AtS += dt;
                    IdentifySave.ToStore(d, _store); _log("identify restored " + d.Items.Count + " statuses");
                }
            }
            catch (Exception e) { _log("identify restore failed: " + e.Message); }
        }

        /// <summary>The game wrote a new save (last_save marker changed): store the statuses next to it.</summary>
        public void OnSaveMarker(string sav, double missionTimeS, SavStamp stamp = null, bool force = false)
        {
            if (sav == null) return;
            if (!force && (sav == _lastSave || SaveHookActive)) return;   // T-198: with the hook on, only the hook writes (a marker that lags must not overwrite the stamped file)
            _lastSave = sav;
            if (_dir == null) return;
            try
            {
                if (_store.All.Count == 0)
                {
                    if (Slots.Delete(sav)) _log("identify save slot cleared " + SaveSlotStore.Key(sav));   // T-122 / R8: nothing to keep for this slot
                    return;
                }
                if (Slots.Write(sav, IdentifySave.ToJson(Stamped(IdentifySave.FromStore(_store, missionTimeS, sav), stamp)), jtxt => { string perr; return IdentifySave.Parse(jtxt, out perr) == null ? (perr ?? "bad file") : null; })) _log("identify saved " + Slots.PathFor(sav));
            }
            catch (Exception e) { _log("identify save failed: " + e.Message); }
        }
    }
}

namespace VoiceBotBridge
{
    /// <summary>
    /// T-111: per-call scope of the game's identify request. Begin (prefix) opens it, Alert (RaiseAlert postfix) collects the texts the game
    /// showed to the player, End (postfix) classifies and closes it. Thread-static, never throws. Only alerts feed the result.
    /// </summary>
    internal static class IdentifyCapture
    {
        private sealed class Scope { public int TrackId; public System.Collections.Generic.List<string> Alerts = new System.Collections.Generic.List<string>(); }

        [System.ThreadStatic] private static Scope _scope;

        public static bool Open { get { return _scope != null; } }

        public static void Begin(int trackId) { _scope = new Scope { TrackId = trackId }; }

        public static void Alert(string text)
        {
            var s = _scope;
            if (s != null && text != null && s.Alerts.Count < 8) s.Alerts.Add(text);
        }

        public static void Abort() { _scope = null; }

        /// <summary>Harmony finalizer body: closes a scope left open by an exception in the original; returns the same exception.</summary>
        public static System.Exception AfterRequest(System.Exception e) { _scope = null; return e; }

        /// <summary>Closes the scope. False = no scope was open.</summary>
        public static bool End(bool resultNotNull, IdentifyTemplates t, out int trackId, out IdentifyResult result)
        {
            var s = _scope;
            _scope = null;
            trackId = -1; result = IdentifyResult.None;
            if (s == null) return false;
            trackId = s.TrackId;
            result = IdentifyClassify.Classify(resultNotNull, s.Alerts, t);
            return true;
        }
    }
}
