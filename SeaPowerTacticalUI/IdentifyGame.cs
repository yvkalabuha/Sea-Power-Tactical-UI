using System;
using System.IO;
using System.Reflection;

namespace VoiceBotBridge
{
    // T-111: the game side of the identify statuses (reflection only). The data comes from the alerts the game shows to the player
    // (see IdentifyPatches); this file keeps the store, restores / saves it next to the game save, and builds the reference alert texts.
    internal sealed partial class GameApi
    {
        internal readonly IdentifyStore Identify = new IdentifyStore();

        /// <summary>BepInEx/config/SeaPowerTacticalUI/identify, set by the plugin.</summary>
        internal string IdentifySaveDir;

        /// <summary>Set once the Harmony patch of the identify request is in (capability "identify").</summary>
        internal bool IdentifyActive;

        private IdentifyKeeper _idKeeper;
        private string _idSavePath;
        private double _idCheckT;

        /// <summary>Per frame, main thread: a new last_save marker -> write the file (the mission change comes from MissionLifecycle, T-130).</summary>
        internal void PumpIdentify()
        {
            if (!IdentifyActive || !Frame.InMission) return;
            try
            {
                double t = Frame.MissionTimeS ?? 0;
                if (_idKeeper == null)
                {
                    // the patch became active after the mission was entered: the entry work has not run for it yet
                    _idKeeper = NewIdentifyKeeper();
                    _idKeeper.SaveHookActive = SaveHookActive;
                    _idSavePath = ReadLastSavePath();
                    _idKeeper.OnMissionChanged(LoadedSavePath(), t, LoadedStamp(LoadedSavePath()));   // T-198: the loaded file
                    return;
                }
                double w = Clock();
                if (w - _idCheckT < 1.0) return;   // the last_save file is read at most once per second
                _idCheckT = w;
                string sp = ReadLastSavePath();
                if (sp != null && sp != _idSavePath) { _idSavePath = sp; _idKeeper.OnSaveMarker(sp, t); }
            }
            catch (Exception e) { try { Log("identify pump failed: " + e.Message); } catch { } }
        }

        private IdentifyKeeper NewIdentifyKeeper() { return new IdentifyKeeper(Identify, IdentifySaveDir, m => { try { Log(m); } catch { } }); }

        /// <summary>A request of the player produced a result for this track (called from the Harmony postfix).</summary>
        internal void OnIdentifyRequest(int trackId, IdentifyResult r)
        {
            Identify.Record(trackId, r, Frame.MissionTimeS ?? 0);
        }

        /// <summary>Track id of a Vehicle as the Overview sees it, -1 when unknown.</summary>
        internal int IdentifyTrackId(object vehicle)
        {
            var id = D(Get(vehicle, CoreSpecs.VehId));
            return id.HasValue ? (int)id.Value : -1;
        }

        /// <summary>Reference alert texts: the game's own FormatCommText over getText("Contact", key) for this unit.</summary>
        internal IdentifyTemplates IdentifyTemplatesFor(object unit, string requestKey)
        {
            var lrh = Singleton(CoreSpecs.LanguageHandler);
            var lookup = GameTextLookup(lrh);
            if (lookup == null) return null;
            if (!Present(IdentifySpecs.FormatComm)) return null;
            Func<string, string> f = key =>
            {
                string raw = lookup("Contact", key);
                if (raw == null) return null;
                return CallStatic(IdentifySpecs.FormatComm, unit, raw) as string;
            };
            return new IdentifyTemplates { NoLos = f("NoLOS"), Ignore = f("IgnoreComm"), Refuse = f("RefuseRequest"), Request = f(requestKey) };
        }
    }
}
