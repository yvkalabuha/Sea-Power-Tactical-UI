using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-234 (ADR-0096): source "air_guard" of the "Orders:" field: the aircraft in the Retreat stage that the mod moves (they sit in a group). The cover ship and the
    /// distance to the threat come from the unit state; "orbiting" from the retreat steering when it is wired (ActionSteer). Reads state only.
    /// </summary>
    internal sealed partial class AirGuardController : IUnitActionSource
    {
        /// <summary>Set by UnitActionFeature (the steering lives in the ASW controller); null = no orbit state.</summary>
        internal AirRetreatSteer ActionSteer;

        string IUnitActionSource.Id { get { return "air_guard"; } }

        void IUnitActionSource.CollectActions(double nowS, IList<UnitActionLabel> into)
        {
            if (_p.Mode != "retreat") return;
            var capAlert = new HashSet<string>();   // T-236: the CAP alert group has no retreat of its own (AirGuardController.Report) - its aircraft keep the "Duty" label of the CAP source
            foreach (var d in _defs) if (d.Value.Kind == "cap_alert") capAlert.Add(d.Key);
            foreach (var kv in _units)
            {
                var us = kv.Value;
                if (us.Stage != AirStage.Retreat || !_uidGroup.ContainsKey(kv.Key)) continue;
                if (capAlert.Contains(_uidGroup[kv.Key])) continue;
                string mode = us.Cover == null ? null : us.Cover.Mode; bool orb = false;
                string sm;
                if (ActionSteer != null && ActionSteer.TryState(kv.Key, out sm, out orb)) mode = sm;
                into.Add(UnitActionMap.Retreat(kv.Key, mode, orb, us.Cover == null ? null : us.Cover.ShipName, us.Threat == null ? double.NaN : us.Threat.Nm));
            }
        }
    }
}
