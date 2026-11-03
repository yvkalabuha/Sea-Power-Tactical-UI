using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceBotBridge
{
    /// <summary>T-094: why a sonobuoy of a loaded save is (not) launched again by the mod.</summary>
    internal enum BuoyReviveVerdict { Revive, NotSave, NotSonobuoy, Launched, HasPlatform, Destroyed, NoTaskforce }

    /// <summary>Facts about one weapon from SceneCreator._weaponsToLaunch.</summary>
    internal struct BuoyFacts
    {
        public bool IsSave, IsBomb, IsSonobuoy, Launched, HasPlatform, Destroyed, HasTaskforce;
    }

    /// <summary>T-094: pure decision core of the buoy revive (no game, no Unity, no reflection).</summary>
    internal static class BuoyReviveMath
    {
        /// <summary>Order of the checks: NotSave, NotSonobuoy, Launched, HasPlatform, Destroyed, NoTaskforce.</summary>
        internal static BuoyReviveVerdict Decide(BuoyFacts f)
        {
            if (!f.IsSave) return BuoyReviveVerdict.NotSave;
            if (!(f.IsBomb && f.IsSonobuoy)) return BuoyReviveVerdict.NotSonobuoy;
            if (f.Launched) return BuoyReviveVerdict.Launched;
            if (f.HasPlatform) return BuoyReviveVerdict.HasPlatform;
            if (f.Destroyed) return BuoyReviveVerdict.Destroyed;
            if (!f.HasTaskforce) return BuoyReviveVerdict.NoTaskforce;
            return BuoyReviveVerdict.Revive;
        }

        /// <summary>The log line; null when there were no sonobuoys at all (nothing to say).</summary>
        internal static string Summary(int launched, int total, IDictionary<BuoyReviveVerdict, int> skipped, int failed)
        {
            if (total <= 0) return null;
            var sb = new StringBuilder();
            sb.Append("buoy revive: launched ").Append(launched).Append(" of ").Append(total).Append(" sonobuoys without a launch platform (skipped: ");
            bool any = false;
            foreach (BuoyReviveVerdict v in System.Enum.GetValues(typeof(BuoyReviveVerdict)))
            {
                int n;
                if (v == BuoyReviveVerdict.Revive || skipped == null || !skipped.TryGetValue(v, out n) || n <= 0) continue;
                if (any) sb.Append(", ");
                sb.Append(v.ToString().ToLowerInvariant()).Append('=').Append(n);
                any = true;
            }
            if (!any) sb.Append("none");
            sb.Append("; failed=").Append(failed).Append(')');
            return sb.ToString();
        }

        /// <summary>T-296: the text of ObjectSonobouyControlViewModel.SonobouyStatus for a buoy without a launch platform (the same replacements, ${ParentName} = "").</summary>
        internal static string FormatStatus(string template, float remainingLife, float transmitterRangeRaw, string deployStatus, string deployDepth)
        {
            string t = template ?? "";
            t = t.Replace("${ParentName}", "");
            double num = Math.Floor(remainingLife / 60f);
            string text = "> 1.5h";
            if (num < 90) text = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", num, remainingLife - num * 60f);
            t = t.Replace("${Timer}", ": " + text);
            // the game writes the range in the current culture; the mod keeps the invariant one (cosmetic difference on a comma culture)
            t = t.Replace("${TransmitterRange}", (transmitterRangeRaw * 0.036285132f).ToString("F1", CultureInfo.InvariantCulture));
            t = t.Replace("${DeployStatus}", deployStatus ?? "");
            t = t.Replace("${DeployDepth}", deployDepth ?? "");
            return t;
        }

        /// <summary>"${DeployDepth}": round(y * -220.47266) as in the game, "\u2013" without a mount object.</summary>
        internal static string DepthText(bool hasMount, float mountY)
        {
            return hasMount ? ((int)Math.Round(mountY * -220.47266f)).ToString(CultureInfo.InvariantCulture) : "\u2013";
        }

        private static BuoyFacts Orphan() { return new BuoyFacts { IsSave = true, IsBomb = true, IsSonobuoy = true, HasTaskforce = true }; }

        /// <summary>null = ok, else the first failure.</summary>
        internal static string SelfTest()
        {
            var f = Orphan();
            if (Decide(f) != BuoyReviveVerdict.Revive) return "orphan must revive";
            f = Orphan(); f.IsSave = false; if (Decide(f) != BuoyReviveVerdict.NotSave) return "not a save";
            f = Orphan(); f.IsSonobuoy = false; if (Decide(f) != BuoyReviveVerdict.NotSonobuoy) return "not a sonobuoy";
            f = Orphan(); f.IsBomb = false; if (Decide(f) != BuoyReviveVerdict.NotSonobuoy) return "not a bomb";
            f = Orphan(); f.Launched = true; if (Decide(f) != BuoyReviveVerdict.Launched) return "launched";
            f = Orphan(); f.HasPlatform = true; if (Decide(f) != BuoyReviveVerdict.HasPlatform) return "has platform";
            f = Orphan(); f.Destroyed = true; if (Decide(f) != BuoyReviveVerdict.Destroyed) return "destroyed";
            f = Orphan(); f.HasTaskforce = false; if (Decide(f) != BuoyReviveVerdict.NoTaskforce) return "no taskforce";
            // order with several flags
            f = new BuoyFacts { IsSave = false, IsBomb = false, Launched = true, HasPlatform = true, Destroyed = true };
            if (Decide(f) != BuoyReviveVerdict.NotSave) return "order: save first";
            f.IsSave = true; if (Decide(f) != BuoyReviveVerdict.NotSonobuoy) return "order: kind second";
            f.IsBomb = true; f.IsSonobuoy = true; if (Decide(f) != BuoyReviveVerdict.Launched) return "order: launched third";
            f.Launched = false; if (Decide(f) != BuoyReviveVerdict.HasPlatform) return "order: platform fourth";
            f.HasPlatform = false; if (Decide(f) != BuoyReviveVerdict.Destroyed) return "order: destroyed fifth";
            f.Destroyed = false; if (Decide(f) != BuoyReviveVerdict.NoTaskforce) return "order: taskforce last";
            if (Summary(0, 0, null, 0) != null) return "summary total=0 must be null";
            var sk = new Dictionary<BuoyReviveVerdict, int> { { BuoyReviveVerdict.Launched, 27 } };
            string s = Summary(15, 42, sk, 0);
            if (s != "buoy revive: launched 15 of 42 sonobuoys without a launch platform (skipped: launched=27; failed=0)") return "summary text: " + s;
            if (Summary(3, 3, null, 1) != "buoy revive: launched 3 of 3 sonobuoys without a launch platform (skipped: none; failed=1)") return "summary none";
            const string en = "Shutdown in ${Timer} Deploy Depth: ${DeployStatus} (${DeployDepth} ft)";
            string fs = FormatStatus(en, 3725f, 0f, "Below", "330");
            if (fs != "Shutdown in : 62:05 Deploy Depth: Below (330 ft)") return "status format: " + fs;
            if (FormatStatus("${Timer}", 5400f, 0f, "", "") != ": > 1.5h") return "status timer over 1.5h";
            if (FormatStatus("a${ParentName}b", 1f, 0f, "", "") != "ab") return "status parent name";
            if (FormatStatus("${TransmitterRange}", 0f, 551.2f, "", "") != "20.0") return "status range";
            if (FormatStatus(null, 1f, 0f, null, null) != "") return "status null template";
            if (DepthText(false, 0f) != "\u2013") return "depth without mount";
            if (DepthText(true, -1.5f) != "331") return "depth text";
            return null;
        }
    }
}
