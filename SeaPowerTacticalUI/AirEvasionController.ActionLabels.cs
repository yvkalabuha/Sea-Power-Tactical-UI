using System.Collections.Generic;

namespace VoiceBotBridge.Ui
{
    /// <summary>T-234 (ADR-0096): source "air_evasion" of the "Orders:" field: aircraft in a missile evasion. Reads state only.</summary>
    internal sealed partial class AirEvasionController : IUnitActionSource
    {
        string IUnitActionSource.Id { get { return "air_evasion"; } }

        void IUnitActionSource.CollectActions(double nowS, IList<UnitActionLabel> into)
        {
            foreach (var kv in _runs) into.Add(UnitActionMap.AirEvasion(kv.Key, kv.Value.Mode, kv.Value.ChaffDone));
        }
    }
}
