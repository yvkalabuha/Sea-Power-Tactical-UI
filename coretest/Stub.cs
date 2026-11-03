using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>The members of the real GameApi that UiCore touches; the real one needs the game's assemblies.</summary>
    internal sealed class GameApi
    {
        public sealed class Check { public string Capability, Member; public bool Required, Present; }
        public bool AllowAttack = true;
        public double NeutralSeekerLegNm = 10.0;
        public Func<double> Clock = () => 0;
        public Action<string> Log = delegate { };
        public readonly List<Check> Checks = new List<Check>();
        public readonly HashSet<string> Capabilities = new HashSet<string>();
        public Ui.ReleaseRegistry UiRelease;
        public void SelfCheck() { }
        // T-120: the registry part of the real GameApi (GameMemberAccess.cs); the stand has no game, so nothing is present
        internal IList<SpecSet> UiSpecs;
        internal RegistryReport Registry { get; private set; }
        internal void SetRegistry(RegistryReport r) { Registry = r; }
        // T-130: the mission part of the real GameApi (MissionGame.cs); fakes with fields
        internal object StubMission; internal bool StubBlocked; internal Action<string> Trace;
        internal int WireCalls, ReleaseCalls;
        internal object MissionRef() { if (Trace != null) Trace("mission"); return StubMission; }
        internal static string ReadLastSavePath() { return "/last.sav"; }
        internal string LoadedSavePath() { return "/last.sav"; }   // T-198
        internal SavStamp LoadedStamp(string savPath) { return null; }
        internal double? MissionTimeS() { return 1.0; }
        internal bool StubArmed; internal bool InputArmed() { return StubArmed; }
        internal void WorldPrime(double realTimeS) { }   // T-348: UiCore primes the world flag of AswMath (real one: RangeRingGame.Kin.cs)
        internal bool InputBlocked() { return StubBlocked; }
        internal string InputBlockOwners() { return "stub"; }
        internal void ReleaseInputBlocks() { ReleaseCalls++; StubBlocked = false; }
        internal void WireMission(Ui.MissionLifecycle m) { WireCalls++; if (Trace != null) Trace("wire"); }
        internal void UiEvent(string name, string dataJson) { }
        internal bool Present(MemberSpec s) { return false; }
        internal System.Reflection.MethodInfo PatchTarget(PatchSpec p) { return null; }
    }

    /// <summary>The link slot is the real UiLinkHost's business; the stand only needs the calls to exist.</summary>
    internal static class UiLinkHost
    {
        internal static void Register(GameApi api, Func<string> capsJson, Action ensureStarted = null) { }
        internal static void Unregister() { }
        internal static string CapsInner(GameApi api) { return ""; }
    }
}
