// T-130 (R7): the game part of the mission lifecycle: the mission identity, the one reader of the last_save marker, the input blocks,
// and the subscribers of MissionLifecycle in one place so the order is visible (and checked by tests/test_sea_power_mission_lifecycle.py).
using System;
using System.IO;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        /// <summary>The ObjectsManager of the running mission, null outside a mission. MissionLifecycle holds it weakly.</summary>
        internal object MissionRef() { return Frame.ObjectsManager; }

        /// <summary>The one reader of persistentDataPath/last_save: the second line is the path of the last .sav (the first is a fallback). null = none.</summary>
        // T-227: stat each call (cheap), read only when mtime or length changed (once per save), not 240-420 file opens a minute.
        private static readonly Ui.StatGatedText LastSave = new Ui.StatGatedText();
        internal static long LastSaveReads { get { return LastSave.Reads; } }

        internal static string ReadLastSavePath()
        {
            try
            {
                string f = Path.Combine(UnityEngine.Application.persistentDataPath, "last_save");
                var lines = LastSave.Lines(f);
                if (lines == null) return null;
                return lines.Length >= 2 ? lines[1].Trim() : (lines.Length == 1 ? lines[0].Trim() : null);
            }
            catch { return null; }
        }

        // ---------- input blocks ----------

        /// <summary>Any of the mod's blocks of the game mouse handling is on: windows (UiMouseBlock), the radial ring, the target pick.</summary>
        internal bool InputBlocked() { return UiMouseBlock.Any || RadialBlocking || SmartPickBlocking; }

        /// <summary>A draw mode is armed by the user: the block is intended, the watchdog stays quiet.</summary>
        internal bool InputArmed() { return UiMouseBlock.AnyArmed; }

        internal string InputBlockOwners()
        {
            string s = UiMouseBlock.OwnersText;
            if (RadialBlocking) s += (s.Length > 0 ? "," : "") + "radial";
            if (SmartPickBlocking) s += (s.Length > 0 ? "," : "") + "smart_pick";
            return s;
        }

        /// <summary>Drops every block. The ring and the pick rewrite their flag every frame, so a wrongly released one comes back at once.</summary>
        internal void ReleaseInputBlocks()
        {
            try { UiMouseBlock.ReleaseAll(); UiMouseBlock.ResetArmed(); } catch { }
            try { UiEscGuard.ReleaseAll(); } catch { }   // T-199
            try { SetRadialBlock(false); } catch { }
            SmartPickBlocking = false;
        }

        // ---------- subscribers ----------

        /// <summary>
        /// The order is the contract. Entered ascending: world(0, T-356: the world of the mission before any restore) -> air_tick(10, subscribed by AirTickFeature) -> sar(20) -> asw(30) -> cap(40) -> identify(50) -> smart_attack(60).
        /// Left descending: input(90) -> doctrine(70) -> ... -> sar(20) -> air_tick(10). SAR is cleared before the ASW file (which also holds the SAR part) is restored.
        /// Left only clears fields: the objects of the left mission may already be destroyed.
        /// </summary>
        internal void WireMission(Ui.MissionLifecycle m)
        {
            m.MarkerOf = ReadLastSavePath;   // T-198 (ADR-0086): the marker is the base of the observers; the path to restore from is the loaded file
            m.StampOf = LoadedStamp;
            m.Subscribe(0, "world", e => WorldEnter(), why => WorldLeave());   // T-356 (ADR-0135): the world of the mission is decided before sar 20 / asw 30 / cap 40 / strike 42 restore their orders
            m.Subscribe(20, "sar", e =>
            {
                if (Sar == null || _sarImpl == null) return;
                _sarImpl.ResetMission(); _sarClock.Reset(); Sar.OnMissionChanged();
            }, why =>
            {
                if (Sar == null || _sarImpl == null) return;
                Sar.OnMissionLeft(); _sarImpl.ResetMission(); _sarClock.Reset();
            });
            m.Subscribe(30, "asw", e =>
            {
                if (Asw == null || _aswImpl == null) return;
                _aswImpl.ResetMission(); Asw.SaveHookActive = SaveHookActive; Asw.DeferRestore = PostLoadGateActive && e.SavePath != null; Asw.OnMissionChanged(e.SavePath, e.MissionTimeS, e.Stamp);
            }, why =>
            {
                AswLeaveMission();
                if (Asw == null || _aswImpl == null) return;
                Asw.OnMissionLeft(); _aswImpl.ResetMission();
            });
            m.Subscribe(40, "cap", e =>
            {
                if (Cap == null || _capImpl == null) return;
                _capImpl.ResetMission(); Cap.DeferRestore = PostLoadGateActive && e.SavePath != null; Cap.OnMissionChanged(e.SavePath, e.MissionTimeS, e.Stamp);
                _capLastSave = e.MarkerPath; _capLastSaveSeen = true; _capPendingClear = null;
            }, why =>
            {
                AirRadarMissionLeft();   // T-241: the radar memory of the left mission (cap and asw run before this step, their holders are gone)
                if (Cap == null || _capImpl == null) return;
                Cap.OnMissionLeft(); _capImpl.ResetMission();
                _capLastSave = null; _capLastSaveSeen = false; _capPendingClear = null;
            });
            m.Subscribe(50, "identify", e =>
            {
                if (!IdentifyActive) return;   // PumpIdentify does the entry work when the patch comes up later
                if (_idKeeper == null) _idKeeper = NewIdentifyKeeper();
                _idSavePath = e.MarkerPath;
                _idKeeper.SaveHookActive = SaveHookActive;
                _idKeeper.OnMissionChanged(e.SavePath, e.MissionTimeS, e.Stamp);
            }, why =>
            {
                if (_idKeeper != null) _idKeeper.OnMissionLeft();
                _idSavePath = null;
            });
            m.Subscribe(60, "smart_attack", e =>
            {
                if (SmartAttack == null || _smartImpl == null) return;
                _smartSavePath = e.MarkerPath;
                _smartImpl.ResetMission();
                SmartAttack.SaveHookActive = SaveHookActive;
                SmartAttack.OnMissionChanged(e.SavePath, e.MissionTimeS, e.Stamp);
            }, why =>
            {
                if (SmartAttack != null) SmartAttack.OnMissionLeft();
                if (_smartImpl != null) _smartImpl.ResetMission();
                _smartSavePath = null;
            });
            m.Subscribe(65, "contact_info", e => CiEnterMission(), why => CiLeaveMission());
            m.Subscribe(66, "evasion", e => EvasionEnterMission(e.SavePath, e.MissionTimeS, e.Stamp), why => EvasionLeaveMission());
            m.Subscribe(70, "doctrine", null, why => MissionLeftDoctrine());
            m.Subscribe(90, "input", null, why => ReleaseInputBlocks());
        }
    }
}
