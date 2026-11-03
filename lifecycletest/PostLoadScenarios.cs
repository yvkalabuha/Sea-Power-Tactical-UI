using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>T-211 (ADR-0088): a load with empty game lists - the groups wait for the post-load gate and are restored after it, nobody is dropped.</summary>
    internal static class PostLoadScenarios
    {
        public static string PostLoad()
        {
            using (var r = new LifeRig { GateOn = true })
            {
                var a = LifeWorld.MakeA(1000, null);
                r.Enter(a, 0);
                r.Populate("asw"); r.Populate("cap");
                if (r.Gate.Armed) return "a new mission must not arm the gate";
                r.Run(3);
                r.SetMarker("/saves/a.sav");
                r.Run(3);
                double saveT = r.Time;
                r.Destroy();
                // a load of the save: the lists of the game are empty in the first frames
                r.HideLists = true;
                var a2 = LifeWorld.MakeA(saveT, "/saves/a.sav");
                r.Enter(a2, 40);
                if (!r.Gate.Armed) return "a load must arm the gate";
                if (!r.Asw.RestoreWaiting || !r.Cap.RestoreWaiting) return "controllers must wait: asw=" + r.Asw.RestoreWaiting + " cap=" + r.Cap.RestoreWaiting;
                double real = 40;
                for (int i = 0; i < 5; i++) { real += 0.25; if (r.GateStep(true, 0, "", r.Time, real)) return "opened on the load screen"; }
                for (int i = 0; i < 3; i++) { r.Run(1); real += 0.25; if (r.GateStep(false, 0, "", r.Time, real)) return "opened on empty lists"; }
                r.Run(8);
                if (r.Asw.View().Groups.Count != 0 || r.Cap.View().Groups.Count != 0) return "groups appeared on empty lists";
                if (r.CapG.LogHas("member out")) return "member out while the lists were empty";
                r.Unhide(a2);
                int opened = 0;
                string[] sigs = { "3,0,2,1", "3,0,4,2", "3,0,6,2", "3,0,6,2", "3,0,6,2", "3,0,6,2" };
                foreach (string sig in sigs)
                {
                    r.Run(1); real += 0.25;
                    if (r.GateStep(false, 11, sig, r.Time, real)) opened++;
                }
                if (opened != 1) return "the gate must open exactly once, opened " + opened;
                if (string.Join(",", r.Applied) != "asw,cap") return "apply order: " + string.Join(",", r.Applied);
                if (r.Asw.RestoreWaiting || r.Cap.RestoreWaiting) return "still waiting after the apply";
                if (r.Asw.View().Groups.Count != 1 || r.Cap.View().Groups.Count != 1) return "groups after the apply: asw=" + r.Asw.View().Groups.Count + " cap=" + r.Cap.View().Groups.Count;
                r.Run(10);
                if (r.Asw.View().Groups.Count != 1 || r.Cap.View().Groups.Count != 1) return "groups after 10 s: " + r.Snapshot();
                if (r.CapG.LogHas("member out")) return "member out after the apply";
            }
            return null;
        }

        /// <summary>A new mission (no save): the gate is not armed and the restore is not deferred.</summary>
        public static string PostLoadNew()
        {
            using (var r = new LifeRig { GateOn = true })
            {
                r.Enter(LifeWorld.MakeA(1000, null), 0);
                if (r.Gate.Armed || r.Asw.RestoreWaiting || r.Cap.RestoreWaiting) return "a new mission must not wait";
            }
            return null;
        }
    }
}
