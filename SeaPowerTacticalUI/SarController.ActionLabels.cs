using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-234 (ADR-0096): source "sar_ship" of the "Orders:" field: the ships of the SAR order (helicopters are labelled by AswController). Reads state only.</summary>
    internal sealed partial class SarController : IUnitActionSource
    {
        string IUnitActionSource.Id { get { return "sar_ship"; } }

        void IUnitActionSource.CollectActions(double nowS, IList<UnitActionLabel> into)
        {
            foreach (var s in _ships.ToArray())
            {
                var l = UnitActionMap.SarShip(s.Uid, s.State, s.RaftId);
                if (l != null) into.Add(l);
            }
        }
    }
}
