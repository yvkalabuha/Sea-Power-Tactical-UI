using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-234 (ADR-0096): source "intercept" of the "Orders:" field: the intercept runs. A suspended run (evasion) shows the pause. Reads state only.</summary>
    internal sealed partial class InterceptController : IUnitActionSource
    {
        string IUnitActionSource.Id { get { return "intercept"; } }

        void IUnitActionSource.CollectActions(double nowS, IList<UnitActionLabel> into)
        {
            foreach (var r in _runs.ToArray())
            {
                string st = r.Suspended ? "suspended" : r.Held ? "held" : r.State;
                var l = UnitActionMap.Intercept(r.Uid, st, r.TargetId, r.EtaS);
                if (l != null) into.Add(l);
            }
        }
    }
}
