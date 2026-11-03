using System;
using System.Linq;
using System.Reflection;

namespace VoiceBotBridge.ProjTest
{
    /// <summary>Runs `static string Run()` of every static class named *Cases in this assembly (null = ok). Prints "&lt;Class&gt; selftest ok" or FAILED.</summary>
    internal static class Program
    {
        private static int Main()
        {
            int bad = 0;
            var types = typeof(Program).Assembly.GetTypes()
                .Where(t => t.Name.EndsWith("Cases", StringComparison.Ordinal))
                .OrderBy(t => t.Name, StringComparer.Ordinal);
            foreach (var t in types)
            {
                MethodInfo m = t.GetMethod("Run", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
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
