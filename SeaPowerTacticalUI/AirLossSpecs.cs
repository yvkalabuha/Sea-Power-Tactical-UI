using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-175 / ADR-0075 item 6 (ADR-0043): the members of the loss tracker (AirLossGame.cs). The landing mark is the one the voice mod uses (DeSpawn: _spawnTime = MaxValue);
    /// without it nothing is reported as a loss. IsDestroyed, the fuel and the ThreateningMissile come from the sets of the CAP and the evasion.</summary>
    internal static class AirLossSpecs
    {
        public static readonly MemberSpec ObjSpawnTime = MemberSpec.Field("air_loss", "SeaPower.ObjectBase", "_spawnTime", false);

        public static readonly SpecSet Set = SpecSet.Of("air_loss", typeof(AirLossSpecs));
    }
}
