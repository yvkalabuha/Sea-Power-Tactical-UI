// T-211 (ADR-0088): the game part of the post-load gate: the sample of "own units are listed" and the deferred restore of the window controllers.
using System;
using System.Collections;
using System.Text;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        /// <summary>Set by AirTickFeature.Init; false = the controllers restore at once in the entry frame (the old path).</summary>
        internal bool PostLoadGateActive;

        /// <summary>T-217 (ADR-0089): the game pause flag; null when GameTime.IsPaused is absent or fails (the gate then judges by the standing game time).</summary>
        internal bool? PostLoadPaused()
        {
            try { return Present(CoreSpecs.GamePaused) ? (bool?)BoolOf(CallStatic(CoreSpecs.GamePaused)) : null; }
            catch { return null; }
        }

        /// <summary>
        /// T-217 (ADR-0089): members of the deferred CAP/ASW files expected in the game lists (uid &gt; 0, not a deck member, not a task uid; see DeferredMemberUids)
        /// and how many of them are listed now (UidOf of the taskforce aircraft + helicopters). Never throws: wanted = -1 on an error.
        /// </summary>
        internal void PostLoadMembers(out int wanted, out int found)
        {
            wanted = 0; found = 0;
            try
            {
                var want = new System.Collections.Generic.List<int>();
                if (Cap != null) want.AddRange(Cap.DeferredMemberUids());
                if (Asw != null) want.AddRange(Asw.DeferredMemberUids());
                if (want.Count == 0) return;
                var live = new System.Collections.Generic.HashSet<int>();
                var tf = GetStatic(CoreSpecs.PlayerTaskforce);
                if (tf != null)
                {
                    var lists = new[] { RangeRingSpecs.TfAircraft, RangeRingSpecs.TfHelicopters };
                    for (int i = 0; i < lists.Length; i++)
                    {
                        var list = Get(tf, lists[i]) as IEnumerable;
                        if (list != null) foreach (var u in list) if (u != null) live.Add(UidOf(u));
                    }
                }
                Ui.PostLoadGate.CountFound(want, live, out wanted, out found);
            }
            catch { wanted = -1; found = 0; }
        }

        /// <summary>
        /// Own units of the player's taskforce: vessels + submarines + aircraft + helicopters; sig = "v,s,a,h". Never throws: -1 on an error.
        /// </summary>
        internal int PostLoadSample(out string sig)
        {
            sig = "";
            try
            {
                if (!Frame.InMission) return 0;
                var tf = GetStatic(CoreSpecs.PlayerTaskforce);
                if (tf == null) return 0;
                var lists = new[] { CoreSpecs.TaskforceVessels, CoreSpecs.TaskforceSubmarines, RangeRingSpecs.TfAircraft, RangeRingSpecs.TfHelicopters };
                var sb = new StringBuilder();
                int total = 0;
                for (int i = 0; i < lists.Length; i++)
                {
                    int n = 0;
                    var list = Get(tf, lists[i]) as IEnumerable;
                    if (list != null) foreach (var u in list) if (u != null) n++;
                    total += n;
                    if (i > 0) sb.Append(',');
                    sb.Append(n);
                }
                sig = sb.ToString();
                return total;
            }
            catch { sig = "error"; return -1; }
        }

        /// <summary>The gate opened: ASW (with the SAR part) first, then CAP, the order of the mission subscribers.</summary>
        internal void ApplyDeferredRestore(double missionT)
        {
            try { if (Asw != null && _aswImpl != null) Asw.ApplyDeferred(missionT); }
            catch (Exception e) { try { Log("post-load asw restore failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
            try { if (Cap != null && _capImpl != null) Cap.ApplyDeferred(missionT); }
            catch (Exception e) { try { Log("post-load cap restore failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
            try { StrikeApplyDeferred(); }   // T-341
            catch (Exception e) { try { Log("post-load strike restore failed: " + e.GetType().Name + ": " + e.Message); } catch { } }
        }
    }
}
