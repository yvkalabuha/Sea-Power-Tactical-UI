using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-234 (ADR-0096): source "smart_attack" of the "Orders:" field: the running smart attacks (a finished one gives no label). Reads state only.</summary>
    internal sealed partial class SmartAttackController : IUnitActionSource
    {
        string IUnitActionSource.Id { get { return "smart_attack"; } }

        void IUnitActionSource.CollectActions(double nowS, IList<UnitActionLabel> into)
        {
            foreach (var a in _active.ToArray())
            {
                var l = UnitActionMap.SmartAttack(a.Uid, a.State, a.TrackId);
                if (l != null) into.Add(l);
            }
        }
    }
}
