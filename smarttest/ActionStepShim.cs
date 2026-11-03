using System.Collections.Generic;

namespace VoiceBotBridge
{
    // Stand-only copy of Common/Actions.cs `ActionStep` (that file drags in GameApi). ActionJournal.cs needs only this type.
    internal sealed class ActionStep
    {
        public string Cmd, When;
        public Dictionary<string, object> Args = new Dictionary<string, object>();
        public bool Optional, Unit = true;
    }
}
