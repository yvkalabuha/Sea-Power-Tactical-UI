using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-234 (ADR-0096): source "sub_evasion" of the "Orders:" field: submarines in a torpedo evasion. Reads state only.</summary>
    internal sealed partial class SubEvasionController : IUnitActionSource
    {
        string IUnitActionSource.Id { get { return "sub_evasion"; } }

        void IUnitActionSource.CollectActions(double nowS, IList<UnitActionLabel> into)
        {
            foreach (var kv in _runs) into.Add(UnitActionMap.SubEvasion(kv.Key, kv.Value.Mode));
        }
    }
}
