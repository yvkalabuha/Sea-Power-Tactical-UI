using System.Text;

namespace VoiceBotBridge
{
    /// <summary>
    /// Border between Common and the voice mod (ADR-0033, ADR-0044 s.7). The members below are implemented only by the voice mod
    /// (VoiceBotBridge/VoiceOnly/*.cs); in the interface mod the compiler removes the calls, so its behaviour is unchanged.
    /// </summary>
    internal sealed partial class GameApi
    {
        private static bool BoolOf(object o) { return o is bool && (bool)o; }

        partial void SelfCheckMissionData();                                   // capabilities mission_data / mission_areas / debrief_preview
        partial void AppendDamage(StringBuilder sb, object u);                 // ",\"damage\":{...}" for a unit (UnitState)
        partial void LastInboundTorpedo(object u, ref bool found, ref double brgDeg, ref double rangeNm, ref int? track); // torpedo the game announced against u
        partial void RestoreSummaryRu(string roe, string route, string speed, string emcon, string radar, ref string summary); // "summary_ru" of the restore_control ack
    }
}
