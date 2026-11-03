using System;
using System.Linq;
using System.Reflection;

namespace VoiceBotBridge
{
    /// <summary>Runs every static string SelfTest() of the types named ContactInfo* / Ci* (null = ok).</summary>
    internal static class Program
    {
        private static int Main()
        {
            if (UiText.LoadForTests("en") <= 0) { Console.WriteLine("lang FAILED: no lang dir"); return 1; }
            int bad = 0;
            string lang = UiText.Has("SeaPowerTacticalUI", "ci.depth.nosol") ? null : "text FAILED: ci.* keys are missing in lang/";
            Console.WriteLine("text selftest " + (lang ?? "ok"));
            if (lang != null) bad++;
            var types = typeof(Program).Assembly.GetTypes()
                .Where(t => t.Name.StartsWith("ContactInfo", StringComparison.Ordinal) || t.Name.StartsWith("Ci", StringComparison.Ordinal))
                .OrderBy(t => t.Name, StringComparer.Ordinal);
            foreach (var t in types)
            {
                MethodInfo m = t.GetMethod("SelfTest", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (m == null || m.ReturnType != typeof(string)) continue;
                string err;
                try { err = (string)m.Invoke(null, null); }
                catch (Exception e) { err = "exception " + (e.InnerException ?? e); }
                Console.WriteLine(t.Name + " selftest " + (err == null ? "ok" : "FAILED: " + err));
                if (err != null) bad++;
            }
            return bad == 0 ? 0 : 1;
        }
    }
}
