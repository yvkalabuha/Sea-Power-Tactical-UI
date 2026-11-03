using System;
using System.Collections.Generic;

namespace VoiceBotBridge.Dev
{
    internal sealed class DevRequest
    {
        public object Id;
        public string Token;
        public string Cmd;
        public Dictionary<string, object> Args = new Dictionary<string, object>();
    }

    /// <summary>Wire format sptui-dev/1: one JSON object per line, hello on connect, one ack per request.</summary>
    internal static class DevProtocol
    {
        public static string Hello(string devVer, string uiVer, bool diag, bool link, bool fireOn)
        {
            var groups = new List<object> { "read", "control" };
            if (fireOn) groups.Add("fire");
            var d = new Dictionary<string, object>
            {
                { "hello", DevInfo.Protocol }, { "dev", devVer }, { "ui", uiVer },
                { "diag", diag }, { "link", link }, { "groups", groups },
            };
            return DevJson.Write(d);
        }

        public static string Ok(object id, object data, string fireLog)
        {
            var d = new Dictionary<string, object> { { "id", id }, { "ok", true }, { "data", data } };
            if (fireLog != null) d["fire_log"] = fireLog;
            return DevJson.Write(d);
        }

        public static string Err(object id, string code, string detail, string fireLog, object data = null)
        {
            var d = new Dictionary<string, object> { { "id", id }, { "ok", false }, { "error", code } };
            if (detail != null) d["detail"] = detail;
            if (data != null) d["data"] = data;
            if (fireLog != null) d["fire_log"] = fireLog;
            return DevJson.Write(d);
        }

        /// <summary>err: "bad_json" (not a JSON object) or "bad_request" (no cmd / wrong field types). The id is kept when readable.</summary>
        public static bool TryParse(string line, out DevRequest req, out string err)
        {
            req = new DevRequest();
            err = null;
            object parsed;
            try { parsed = Json.Parse(line ?? ""); }
            catch (Exception) { err = "bad_json"; return false; }
            var o = parsed as Dictionary<string, object>;
            if (o == null) { err = "bad_json"; return false; }
            object id;
            if (o.TryGetValue("id", out id) && (id == null || id is string || id is double)) req.Id = id;
            object t;
            if (o.TryGetValue("token", out t)) { req.Token = t as string; if (req.Token == null && t != null) { err = "bad_request"; return false; } }
            req.Cmd = o.ContainsKey("cmd") ? o["cmd"] as string : null;
            if (string.IsNullOrEmpty(req.Cmd)) { err = "bad_request"; return false; }
            object a;
            if (o.TryGetValue("args", out a) && a != null)
            {
                var ad = a as Dictionary<string, object>;
                if (ad == null) { err = "bad_request"; return false; }
                req.Args = ad;
            }
            return true;
        }
    }
}
