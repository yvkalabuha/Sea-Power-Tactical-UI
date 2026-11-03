using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Dev
{
    internal interface IDiagPort { string Query(string json); }
    internal interface ILinkPort { string Call(string line); }

    /// <summary>Lazy lookup of the UI mod slots in the AppDomain (plain BCL types cross the slot; no assembly reference to the UI mod).</summary>
    internal sealed class DevSlots
    {
        private sealed class FuncDiag : IDiagPort
        {
            public Func<string, string> F;
            public string Query(string json) { return F(json); }
        }

        private sealed class FuncLink : ILinkPort
        {
            public Func<string, string> F;
            public string Call(string line) { return F(line); }
        }

        public IDiagPort Diag;
        public ILinkPort Link;
        /// <summary>null | "ui_mod_absent" | "unsupported", per slot.</summary>
        public string DiagError, LinkError;
        public string UiVersion;

        public static DevSlots Resolve()
        {
            var r = new DevSlots();
            var diag = AppDomain.CurrentDomain.GetData(DevInfo.DiagSlotKey) as Dictionary<string, object>;
            if (diag == null) r.DiagError = "ui_mod_absent";
            else
            {
                var q = diag.ContainsKey("query") ? diag["query"] as Func<string, string> : null;
                if (!(diag.ContainsKey("diag") && diag["diag"] is int && (int)diag["diag"] == 1) || q == null) r.DiagError = "unsupported";
                else { r.Diag = new FuncDiag { F = q }; r.UiVersion = diag.ContainsKey("version") ? diag["version"] as string : null; }
            }
            var link = AppDomain.CurrentDomain.GetData(DevInfo.LinkSlotKey) as Dictionary<string, object>;
            if (link == null) r.LinkError = "ui_mod_absent";
            else
            {
                var c = link.ContainsKey("call") ? link["call"] as Func<string, string> : null;
                if (!(link.ContainsKey("link") && link["link"] is int && (int)link["link"] == 1) || c == null) r.LinkError = "unsupported";
                else
                {
                    r.Link = new FuncLink { F = c };
                    if (r.UiVersion == null) r.UiVersion = link.ContainsKey("version") ? link["version"] as string : null;
                }
            }
            return r;
        }
    }
}
