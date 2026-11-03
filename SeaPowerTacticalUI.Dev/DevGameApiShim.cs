namespace VoiceBotBridge
{
    /// <summary>
    /// The dev DLL compiles the pure T-120 registry file GameMember.cs (MemberSpec, MemberResolver) for DevAirReadySpecs. SpecSet in that file names
    /// GameApi in a delegate signature only; this empty type satisfies it. The dev DLL has no GameApi and does not reference the UI assembly (ADR-0046).
    /// </summary>
    internal sealed partial class GameApi { }
}
