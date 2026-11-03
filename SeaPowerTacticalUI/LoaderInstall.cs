using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace VoiceBotBridge.Ui
{
    internal enum LoaderInstallVerdict { Disabled, NotWorkshopEntry, NoPackaged, UpToDate, Install }

    internal struct LoaderInstallResult
    {
        public LoaderInstallVerdict Verdict;
        public string Line;     // null = say nothing
        public bool Warning;
    }

    /// <summary>
    /// T-314 (ADR-0121): when the mod starts from its Workshop folder (entry anchorchain/loader) it copies the packaged
    /// loader/SeaPowerTacticalUI.Loader.dll into the plugins folder (a newer copy replaces an older one). BCL only, never throws.
    /// </summary>
    internal static class LoaderInstall
    {
        public const string FileName = "SeaPowerTacticalUI.Loader.dll";
        public const string SubDir = "loader";

        /// <summary>!enabled -> Disabled; entry not "anchorchain"/"loader" -> NotWorkshopEntry; packaged null -> NoPackaged;
        /// installed != null &amp;&amp; installed >= packaged -> UpToDate; else Install.</summary>
        public static LoaderInstallVerdict Decide(string entry, bool enabled, Version packaged, Version installed)
        {
            if (!enabled) return LoaderInstallVerdict.Disabled;
            if (entry != "anchorchain" && entry != "loader") return LoaderInstallVerdict.NotWorkshopEntry;
            if (packaged == null) return LoaderInstallVerdict.NoPackaged;
            if (installed != null && installed >= packaged) return LoaderInstallVerdict.UpToDate;
            return LoaderInstallVerdict.Install;
        }

        public static LoaderInstallResult Run(string entry, bool enabled, string modDir, string pluginsDir, Func<string, Version> readVersion)
        {
            var res = new LoaderInstallResult { Verdict = LoaderInstallVerdict.Disabled };
            string tmp = null;
            try
            {
                if (!enabled || (entry != "anchorchain" && entry != "loader")) { res.Verdict = Decide(entry, enabled, null, null); return res; }
                string src = Path.Combine(Path.Combine(modDir ?? "", SubDir), FileName);
                string dst = Path.Combine(pluginsDir ?? "", FileName);
                Version packaged = File.Exists(src) ? readVersion(src) : null;
                Version installed = File.Exists(dst) ? readVersion(dst) : null;
                res.Verdict = Decide(entry, enabled, packaged, installed);
                if (res.Verdict != LoaderInstallVerdict.Install) return res;
                tmp = dst + ".tmp";
                File.Copy(src, tmp, true);
                if (File.Exists(dst))
                {
                    try { File.Replace(tmp, dst, null); }
                    catch (PlatformNotSupportedException) { File.Delete(dst); File.Move(tmp, dst); }
                }
                else File.Move(tmp, dst);
                res.Line = "loader installed " + packaged + " -> " + dst;
                return res;
            }
            catch (Exception ex)
            {
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
                res.Line = "loader install failed: " + ex.Message;
                res.Warning = true;
                return res;
            }
        }

        /// <summary>AssemblyName.GetAssemblyName(path).Version (no load); null if missing or not an assembly.</summary>
        public static Version ReadVersion(string path)
        {
            try { return AssemblyName.GetAssemblyName(path).Version; }
            catch { return null; }
        }

        public static string SelfTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "loaderinstall-" + Guid.NewGuid().ToString("N"));
            try
            {
                string mod = Path.Combine(root, "mod"), plugins = Path.Combine(root, "plugins");
                Directory.CreateDirectory(Path.Combine(mod, SubDir));
                Directory.CreateDirectory(plugins);
                string src = Path.Combine(Path.Combine(mod, SubDir), FileName), dst = Path.Combine(plugins, FileName);
                File.WriteAllText(src, "new");
                var ver = new Dictionary<string, Version>();
                Func<string, Version> read = p => { Version v; return ver.TryGetValue(p, out v) ? v : null; };
                ver[src] = new Version(1, 0, 0, 0);

                var r = LoaderInstall.Run("anchorchain", true, mod, plugins, read);
                if (r.Verdict != LoaderInstallVerdict.Install || r.Warning) return "install: fresh -> " + r.Verdict + " " + r.Line;
                if (!File.Exists(dst) || File.ReadAllText(dst) != "new") return "install: file not copied";
                if (File.Exists(dst + ".tmp")) return "install: tmp left behind";
                if (r.Line != "loader installed 1.0.0.0 -> " + dst) return "install: line = " + r.Line;

                File.WriteAllText(dst, "old");
                ver[dst] = new Version(0, 9, 0, 0);
                r = Run("loader", true, mod, plugins, read);
                if (r.Verdict != LoaderInstallVerdict.Install || File.ReadAllText(dst) != "new" || File.Exists(dst + ".tmp")) return "install: older copy must be replaced";

                foreach (var v in new[] { new Version(1, 0, 0, 0), new Version(2, 0, 0, 0) })
                {
                    File.WriteAllText(dst, "keep");
                    ver[dst] = v;
                    r = Run("anchorchain", true, mod, plugins, read);
                    if (r.Verdict != LoaderInstallVerdict.UpToDate || r.Line != null || File.ReadAllText(dst) != "keep") return "install: equal/newer must stay, " + v;
                }

                r = Run("anchorchain", false, mod, plugins, read);
                if (r.Verdict != LoaderInstallVerdict.Disabled || r.Line != null) return "install: disabled";
                r = Run("bepinex", true, mod, plugins, read);
                if (r.Verdict != LoaderInstallVerdict.NotWorkshopEntry || r.Line != null) return "install: bepinex entry";
                r = Run("anchorchain", true, Path.Combine(root, "nomod"), plugins, read);
                if (r.Verdict != LoaderInstallVerdict.NoPackaged || r.Line != null) return "install: no packaged loader";
                r = Run("anchorchain", true, mod, Path.Combine(root, "noplugins"), read);
                if (!r.Warning || r.Line == null || !r.Line.StartsWith("loader install failed:")) return "install: missing plugins dir must warn, got " + r.Line;

                if (ReadVersion(typeof(LoaderInstall).Assembly.Location) == null) return "ReadVersion: own assembly";
                string txt = Path.Combine(root, "x.txt");
                File.WriteAllText(txt, "not an assembly");
                if (ReadVersion(txt) != null || ReadVersion(Path.Combine(root, "missing.dll")) != null) return "ReadVersion: text/missing must be null";
                return null;
            }
            catch (Exception ex) { return "LoaderInstall: " + ex; }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
