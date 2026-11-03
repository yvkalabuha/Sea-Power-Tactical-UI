using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            UiText.LoadForTests("ru");
            if (args.Length > 0 && args[0] == "wiring")
            {
                var l = new List<string>();
                foreach (var w in LifeRig.Wiring) l.Add(w.Id + ":" + w.Order);
                Console.WriteLine(string.Join(",", l.ToArray()));
                return 0;
            }
            var all = new List<KeyValuePair<string, Func<string>>>
            {
                new KeyValuePair<string, Func<string>>("order", LifecycleScenarios.Order),
                new KeyValuePair<string, Func<string>>("switch", LifecycleScenarios.Switch),
                new KeyValuePair<string, Func<string>>("restore", LifecycleScenarios.Restore),
                new KeyValuePair<string, Func<string>>("wrong_order", LifecycleScenarios.WrongOrder),
                new KeyValuePair<string, Func<string>>("menu", LifecycleScenarios.Menu),
                new KeyValuePair<string, Func<string>>("weak", LifecycleScenarios.Weak),
                new KeyValuePair<string, Func<string>>("throwing", LifecycleScenarios.Throwing),
                new KeyValuePair<string, Func<string>>("files", LifecycleScenarios.Files),
                new KeyValuePair<string, Func<string>>("post_load", PostLoadScenarios.PostLoad),   // T-211
                new KeyValuePair<string, Func<string>>("post_load_new", PostLoadScenarios.PostLoadNew),
                new KeyValuePair<string, Func<string>>("counters", RepeatScenarios.Counters),   // T-212
                new KeyValuePair<string, Func<string>>("repeat", RepeatScenarios.Repeat),
            };
            // known leaks run only by name: their FAILED line must not hide the green scenarios (pytest marks them xfail)
            var named = new List<KeyValuePair<string, Func<string>>>
            {
                new KeyValuePair<string, Func<string>>("stale_marker", LifecycleScenarios.StaleMarker),
            };
            if (args.Length > 0) all.AddRange(named);
            int bad = 0;
            foreach (var kv in all)
            {
                if (args.Length > 0 && args[0] != kv.Key) continue;
                string err;
                try { err = kv.Value(); } catch (Exception e) { err = e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace; }
                Console.WriteLine("lifecycle " + kv.Key + " selftest " + (err == null ? "ok" : "FAILED: " + err));
                if (err != null) bad++;
            }
            return bad == 0 ? 0 : 1;
        }
    }
}
