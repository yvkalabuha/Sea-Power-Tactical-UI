using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-234 (ADR-0096): source "cap" of the "Orders:" field: the airborne members of the patrol and alert groups. Deck and hangar rows are skipped. Reads state only.</summary>
    internal sealed partial class CapController : IUnitActionSource
    {
        string IUnitActionSource.Id { get { return "cap"; } }

        void IUnitActionSource.CollectActions(double nowS, IList<UnitActionLabel> into)
        {
            foreach (var g in _groups)
                foreach (var m in g.Members)
                {
                    if (!m.Airborne || m.Uid == 0) continue;
                    var l = UnitActionMap.Cap(m.Uid, g.Kind, m.Airborne, m.OnStation, m.AlertState, m.AlertRaid, m.Leaving, !string.IsNullOrEmpty(m.LentFrom), g.Name);
                    if (l != null) into.Add(l);
                }
        }
    }
}
