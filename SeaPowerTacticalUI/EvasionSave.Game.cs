using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    // T-256 (ADR-0086 pattern): the player's route before an own torpedo evasion survives a save/load of the game.
    // Writes from the save hook, reads on mission entry, binds the records to the ships lazily (uid, then name).
    // The file goes through SaveSlotStore only. Common/ and the voice mod are not involved: ControlMemory.Save.cs is a UI-only partial.
    internal sealed partial class GameApi
    {
        internal string EvasionSaveDir;

        private SaveSlotStore _evSlots;
        private string _evSlotsDir;
        private SaveSlotStore EvSlots
        {
            get
            {
                if (_evSlots == null || _evSlotsDir != EvasionSaveDir) { _evSlots = new SaveSlotStore(EvasionSaveDir, Log); _evSlotsDir = EvasionSaveDir; }
                return _evSlots;
            }
        }

        private List<EvMemRecord> _evPending;                  // records of the loaded file that wait for their ships
        private double _evPendingSince = double.NaN, _evBindT = double.NaN;
        private readonly Dictionary<object, double> _evResume = new Dictionary<object, double>(new RefEq());   // restored ship -> mission time of the "evasion did not resume" end

        private readonly Dictionary<object, int> _evBoundUid = new Dictionary<object, int>(new RefEq());   // T-274: restored ship -> its uid at the bind (a uid that moves later is logged)

        /// <summary>From OnGameSaved (main thread): ships with a running own evasion go to the file; none = the slot is cleared.</summary>
        internal void EvasionOnGameSaved(string savPath, SavStamp stamp, double missionT)
        {
            if (string.IsNullOrEmpty(EvasionSaveDir) || string.IsNullOrEmpty(savPath)) return;
            var d = new EvSaveData { MissionT = missionT, Sav = stamp };
            foreach (var u in new List<object>(_evRuns.Keys))
            {
                if (!IsAlive(u) || BoolOf(Get(u, ContactInfoSpecs.ObjIsDestroyed))) continue;   // T-263: a sinking ship is not remembered
                if (ShipImmobileWhy(u) != null)   // T-266: a ship that cannot move is not remembered either
                {
                    Log("[Evasion] evasion memory skip uid=" + UidOf(u) + " reason=immobile why=" + ShipImmobileWhy(u));
                    continue;
                }
                var r = _ctlMem.ExportRoute(u, UidOf(u), NameOf(u));
                if (r != null) d.Units.Add(r);
            }
            if (d.Units.Count == 0)
            {
                if (EvSlots.Delete(savPath)) Log("[Evasion] evasion memory slot cleared " + SaveSlotStore.Key(savPath));
                return;
            }
            string json = EvasionSave.ToJson(d);
            if (EvSlots.Write(savPath, json, t => { string e; return EvasionSave.Parse(t, out e) == null ? (e ?? "bad file") : null; }))
                Log("[Evasion] evasion memory saved n=" + d.Units.Count + " " + EvSlots.PathFor(savPath));
        }

        /// <summary>Mission entry: reads the file of the loaded .sav when it is fresh; the ships are bound later (EvasionResumeTick).</summary>
        internal void EvasionEnterMission(string savPathOrNull, double missionT, SavStamp loaded)
        {
            _evPending = null; _evPendingSince = double.NaN; _evBindT = double.NaN; _evResume.Clear(); _evBoundUid.Clear();
            if (savPathOrNull == null || string.IsNullOrEmpty(EvasionSaveDir)) return;
            try
            {
                EvSaveData d = null; string err = null, src;
                string text = EvSlots.Read(savPathOrNull, t => { d = EvasionSave.Parse(t, out err); return d == null ? (err ?? "bad file") : null; }, out src);
                if (text == null) { if (err != null) Log("[Evasion] evasion memory restore: bad file (" + err + ")"); return; }
                string why = SaveFreshness.Check(d.Sav, d.MissionT, loaded, missionT);
                if (why != null) { Log("[Evasion] evasion memory restore: file is stale (" + why + ")"); return; }
                if (d.Units.Count == 0) return;
                _evPending = new List<EvMemRecord>(d.Units);
                Log("[Evasion] evasion memory loaded n=" + d.Units.Count + " from=" + src);
            }
            catch (Exception e) { Log("[Evasion] evasion memory restore failed: " + e.Message); }
        }

        private void EvasionSaveLeave()
        {
            _evPending = null; _evPendingSince = double.NaN; _evBindT = double.NaN; _evResume.Clear(); _evBoundUid.Clear();
        }

        /// <summary>From AutoEvadeHook (own ships, own mode, before EvasionTick). Never throws.</summary>
        private void EvasionResumeTick(object u, double realNow)
        {
            try
            {
                if (_evPending == null && _evResume.Count == 0) return;
                double? mt = Frame.MissionTimeS;
                if (!mt.HasValue) return;
                double now = mt.Value;
                // T-274: the ships are bound after the load screen (their uid may still be temporary while it is shown)
                if (_evPending != null && !LoadScreenActive() && (double.IsNaN(_evBindT) || Math.Abs(now - _evBindT) > 1e-6))
                {
                    _evBindT = now;
                    EvasionBindPending(now);
                }
                double due;
                if (!_evResume.TryGetValue(u, out due)) return;
                int bound;
                if (_evBoundUid.TryGetValue(u, out bound))
                {
                    int cur = UidOf(u);
                    if (cur != bound) { _evBoundUid.Remove(u); Log("[Evasion] evasion memory uid moved uid=" + cur + " was=" + bound); }
                }
                if (_evRuns.ContainsKey(u)) { _evResume.Remove(u); _evBoundUid.Remove(u); return; }   // the evasion resumed: its end returns the ship to the player's route
                if (now < due) return;
                _evResume.Remove(u); _evBoundUid.Remove(u);
                string route = "na";
                try
                {
                    var wps = Waypoints(u);
                    route = ControlMemory.DecideRoute(_ctlMem.Peek(u), wps != null && wps.Count > 0 ? wps[0] : null, (a, b) => WorldDistanceNm(a[0], a[1], b[0], b[1]));
                }
                catch (Exception) { }
                Log("[Evasion] evasion resume end uid=" + UidOf(u) + " reason=no_restart route=" + route);
                EvasionFinish(u, now, null, "resume_timeout");
            }
            catch (Exception e) { try { Log("[Evasion] resume tick failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
        }

        private void EvasionBindPending(double now)
        {
            if (double.IsNaN(_evPendingSince)) _evPendingSince = now;
            var objs = new List<object>(); var uids = new List<int>(); var names = new List<string>();
            foreach (var o in OwnUnits())
            {
                if (UnitKindOfObj(o) != "ship" || !IsAlive(o)) continue;
                objs.Add(o); uids.Add(UidOf(o)); names.Add(NameOf(o));
            }
            List<string> by;
            var pairs = EvasionSave.Bind(_evPending, uids, names, out by);
            if (pairs.Count > 0) TacticsEnsureMission();   // before the first import: a later mission reset would wipe it
            var inv = CultureInfo.InvariantCulture;
            var taken = new HashSet<int>();
            for (int i = 0; i < pairs.Count; i++)
            {
                var rec = _evPending[pairs[i][0]];
                var unit = objs[pairs[i][1]];
                taken.Add(pairs[i][0]);
                _ctlMem.ImportRoute(unit, rec);
                _evResume[unit] = now + EvasionSave.ResumeGraceS;
                _evBoundUid[unit] = uids[pairs[i][1]];
                Log("[Evasion] evasion memory restored uid=" + uids[pairs[i][1]] + " from=" + rec.Uid + " by=" + by[i] + " wp=" + rec.Route.Count +
                    " course=" + (rec.CourseDeg.HasValue ? rec.CourseDeg.Value.ToString("0", inv) : "na"));
            }
            var rest = new List<EvMemRecord>();
            for (int i = 0; i < _evPending.Count; i++) if (!taken.Contains(i)) rest.Add(_evPending[i]);
            _evPending = rest.Count == 0 ? null : rest;
            if (_evPending != null && now - _evPendingSince >= EvasionSave.BindTimeoutS)
            {
                Log("[Evasion] evasion memory dropped n=" + _evPending.Count);
                _evPending = null;
            }
        }
    }
}
