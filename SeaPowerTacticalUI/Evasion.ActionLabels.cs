using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-234 (ADR-0096): source "evasion" of the "Orders:" field: the ships in the mod's torpedo evasion (_evRuns). Reads state only; the game is not touched.</summary>
    internal sealed partial class GameApi
    {
        private EvasionActionSource _uaEvasion;

        /// <summary>The source for UnitActionFeature.</summary>
        internal IUnitActionSource UaEvasionSource { get { return _uaEvasion ?? (_uaEvasion = new EvasionActionSource(this)); } }

        private sealed class EvasionActionSource : IUnitActionSource
        {
            private readonly GameApi _a;
            public EvasionActionSource(GameApi a) { _a = a; }
            public string Id { get { return "evasion"; } }

            public void CollectActions(double nowS, IList<UnitActionLabel> into)
            {
                var done = new HashSet<int>();
                foreach (var kv in _a._evRuns)
                {
                    int uid = _a.UidOf(kv.Key);
                    if (uid == 0) continue;
                    done.Add(uid);
                    var run = kv.Value;
                    bool turn = _a._atwTurn.PhaseOf(uid) != "idle";   // T-230: turning onto the salvo
                    into.Add(UnitActionMap.Evasion(uid, run.S.PrimaryId, run.Holding, run.Slowed, turn));
                }
                // a ship turning onto the salvo without an evasion run is still shown (the turn is its own manoeuvre)
                foreach (int uid in _a._atwTurn.ActiveUids())
                    if (uid != 0 && done.Add(uid)) into.Add(UnitActionMap.Evasion(uid, 0, false, false, true));
            }
        }
    }
}
