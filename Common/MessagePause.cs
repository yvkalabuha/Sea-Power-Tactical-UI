using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceBotBridge
{
    internal sealed partial class GameApi
    {
        private object InputHandlerInstance()
        {
            var t = FindType("SeaPower.InputHandler");
            if (t == null) return null;
            try
            {
                var p = t.GetProperty("Instance", AllStat);
                return p == null ? null : p.GetValue(null, null);
            }
            catch { return null; }
        }

        public bool MessagePause()
        {
            var ih = InputHandlerInstance();
            var v = Get(ih, "IsInMessagePauseMode");
            return v is bool && (bool)v;
        }

        private void SetPauseFlag(bool paused)
        {
            var ih = InputHandlerInstance();
            if (ih != null && Get(ih, "IsInPauseMode") != null) RxSet(ih, "IsInPauseMode", paused);
        }
    }
}
