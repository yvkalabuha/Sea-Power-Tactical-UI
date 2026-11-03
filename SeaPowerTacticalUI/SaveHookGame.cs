// T-198 (ADR-0086): the game part of the save hook: the stamp of a game save, the path of the file that was loaded, and the fan-out to the state owners.
using System;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        private bool _saveHookActive;
        private bool _loadedFromGlobals;

        /// <summary>True while the Harmony postfix on SaveGame is installed (set by HarmonyFeature). The marker observers then only follow the marker.</summary>
        internal bool SaveHookActive
        {
            get { return _saveHookActive; }
            set { _saveHookActive = value; PushHookFlag(value); }
        }

        private void PushHookFlag(bool on)
        {
            try { if (Asw != null) Asw.SaveHookActive = on; } catch { }
            try { if (SmartAttack != null) SmartAttack.SaveHookActive = on; } catch { }
            try { if (_idKeeper != null) _idKeeper.SaveHookActive = on; } catch { }
        }

        /// <summary>
        /// The .sav file the running mission came from: Globals.currentMissionFilePath when it is a .sav (fromGlobals = true), null for a new mission;
        /// when the field cannot be read, the last_save marker as before T-198 (fromGlobals = false: such a path is the last SAVED file, not a loaded one).
        /// </summary>
        internal string LoadedSavePath(out bool fromGlobals)
        {
            fromGlobals = false;
            try
            {
                if (Present(SaveHookSpecs.CurrentMission))
                {
                    fromGlobals = true;
                    string p = GetStatic(SaveHookSpecs.CurrentMission) as string;
                    return !string.IsNullOrEmpty(p) && p.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) ? p : null;
                }
            }
            catch { fromGlobals = false; }
            return ReadLastSavePath();
        }

        /// <summary>The mission lifecycle reads the path first and then the stamp: the answer of the last LoadedSavePath decides whether the header may be read.</summary>
        internal string LoadedSavePath()
        {
            bool fg;
            string p = LoadedSavePath(out fg);
            _loadedFromGlobals = fg;
            return p;
        }

        /// <summary>Header stamp of the loaded .sav; null when the path is not a loaded file or the header cannot be read.</summary>
        internal SavStamp LoadedStamp(string savPath)
        {
            return _loadedFromGlobals && savPath != null ? SavStamp.ReadSav(savPath) : null;
        }

        /// <summary>T-217: startedUtc is the time of the prefix (the game writes [File] CreationTime at the start of the call). The postfix of SaveLoadManager.SaveGame (main thread, last_save is already written): the mod's state files of that path are written now.</summary>
        internal void OnGameSaved(string path, DateTime? startedUtc = null)
        {
            if (string.IsNullOrEmpty(path) || !Frame.InMission) return;
            path = path.Trim();
            SavStamp stamp = null;
            try
            {
                var el = D(GetStatic(SaveHookSpecs.GameElapsed));
                if (el.HasValue && !double.IsNaN(el.Value)) stamp = SavStamp.Now(el.Value, SavStamp.HookCreatedUtc(startedUtc, DateTime.UtcNow), "hook");
            }
            catch { }
            double t = 0;
            try { t = Frame.MissionTimeS ?? 0; } catch { }
            _saveHookActive = true;
            try
            {
                if (Cap != null && _capImpl != null)
                {
                    _capLastSave = path; _capLastSaveSeen = true; _capPendingClear = null;
                    if (Cap.View().Groups.Count > 0 || Cap.RestoreWaiting) Cap.PersistNow(path, stamp);   // T-211 else Cap.ClearSlot(path);
                }
            }
            catch (Exception e) { HookFail("cap", e); }
            try { if (Asw != null && _aswImpl != null) { Asw.SaveHookActive = true; Asw.OnGameSaved(path, stamp); } }
            catch (Exception e) { HookFail("asw", e); }
            try
            {
                if (IdentifyActive && _idKeeper != null) { _idKeeper.SaveHookActive = true; _idSavePath = path; _idKeeper.OnSaveMarker(path, t, stamp, true); }
            }
            catch (Exception e) { HookFail("identify", e); }
            try
            {
                if (SmartAttack != null && _smartImpl != null) { SmartAttack.SaveHookActive = true; _smartSavePath = path; SmartAttack.OnSaveMarker(path, t, stamp, true); }
            }
            catch (Exception e) { HookFail("smart_attack", e); }
            try { StrikeOnGameSaved(path, stamp); }   // T-341
            catch (Exception e) { HookFail("strike", e); }
            try { EvasionOnGameSaved(path, stamp, t); }   // T-256
            catch (Exception e) { HookFail("evasion", e); }
        }

        private void HookFail(string who, Exception e)
        {
            try { Log("save hook " + who + " failed: " + e.GetType().Name + ": " + e.Message); } catch { }
        }
    }
}
