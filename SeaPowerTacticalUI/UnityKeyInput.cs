using System;
using UnityEngine;

namespace VoiceBotBridge.Ui
{
    /// <summary>IKeyInput on Unity's Input (T-118): key names are KeyCode names; Typing = a text field of the game has the focus.</summary>
    internal sealed class UnityKeyInput : IKeyInput
    {
        private readonly GameApi _api;

        public UnityKeyInput(GameApi api) { _api = api; }

        private static KeyCode Code(string name) { return (KeyCode)Enum.Parse(typeof(KeyCode), name, true); }

        public bool Held(string keyName) { return Input.GetKey(Code(keyName)); }
        public bool Down(string keyName) { return Input.GetKeyDown(Code(keyName)); }
        public bool Typing { get { return _api.TypingActive(); } }
    }
}
