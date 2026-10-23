using System.Collections.Generic;
using RL2Coop.Net.Game;
using UnityEngine;

namespace RL2Coop.Plugin
{
    /// <summary>
    /// Lives on its own GameObject that survives scene loads. Runs the co-op
    /// controller once a frame, reads the hotkeys and draws the overlay: the
    /// co-op window, the player list with ping, and short notices.
    /// <para>
    /// Rogue Legacy 2 does not run Update on the BepInEx plugin object, so the
    /// plugin creates this object itself (and re-creates it from a game hook if
    /// something destroys it).
    /// </para>
    /// </summary>
    internal sealed class CoopDriver : MonoBehaviour
    {
        private const float NoticeSeconds = 6f;
        private const int MaxNotices = 4;

        internal CoopController Controller;
        internal CoopSettings Settings;

        private readonly List<string> _lines = new List<string>();
        private readonly List<(string text, float until)> _notices = new List<(string, float)>();
        private bool _windowOpen;
        private Rect _windowRect = new Rect(40, 40, 340, 0);
        private string _portText;
        private GUIStyle _shadowed;
        private float _row = 21f;

        internal static CoopDriver Create(CoopController controller, CoopSettings settings)
        {
            var go = new GameObject("RL2Coop");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            var d = go.AddComponent<CoopDriver>();
            d.Controller = controller;
            d.Settings = settings;
            d._portText = settings.Port.Value.ToString();
            controller.Notice += d.Push;
            controller.Ended += d.Push;
            return d;
        }

        private void OnDestroy()
        {
            if (Controller == null) return;
            Controller.Notice -= Push;
            Controller.Ended -= Push;
        }

        private void Push(string text)
        {
            _notices.Add((text, Time.unscaledTime + NoticeSeconds));
            if (_notices.Count > MaxNotices) _notices.RemoveAt(0);
        }

        private void Update()
        {
            // An unfocused window must keep sending and receiving, or the
            // other players see this hero freeze whenever it is alt-tabbed.
            Application.runInBackground = true;
            TryAutoStart();
            Controller.Tick();

            if (Input.GetKeyDown(Settings.MenuKey.Value)) _windowOpen = !_windowOpen;
            if (Controller.Session != null && !Controller.Session.LocalAlive)
            {
                if (Input.GetKeyDown(Settings.SpectatePrevKey.Value)) Controller.CycleSpectate(-1);
                if (Input.GetKeyDown(Settings.SpectateNextKey.Value)) Controller.CycleSpectate(+1);
            }
            _notices.RemoveAll(n => n.until < Time.unscaledTime);
        }

        private void OnApplicationQuit() => Controller.Leave();

        private bool _autoStarted;

        /// <summary>Debug.AutoStart: host or join once, a few seconds after launch, so test runs need no clicks.</summary>
        private void TryAutoStart()
        {
            if (_autoStarted || Time.realtimeSinceStartup < 5f) return;
            _autoStarted = true;
            var mode = Settings.AutoStart.Value;
            if (mode != "") CoopPlugin.Instance.Log.LogInfo($"AutoStart: {mode}");
            if (mode == "host") Controller.StartHost(Settings.ToOptions(), Settings.Port.Value);
            else if (mode == "join") Controller.Join(Settings.ToOptions(), Settings.HostAddress.Value.Trim(), Settings.Port.Value);
        }

        // ------------------------------------------------------------ overlay

        private void OnGUI()
        {
            if (_shadowed == null)
                _shadowed = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, richText = false };
            // Readable on a 1080p screen and on a Retina one alike.
            _shadowed.fontSize = Mathf.Max(15, Screen.height / 55);
            _row = _shadowed.fontSize * 1.4f;

            if (Settings.ShowPlayerList.Value && Controller.Session != null) DrawPlayerList();
            DrawSpectating();
            DrawNotices();
            if (_windowOpen)
                _windowRect = GUILayout.Window(0x52_4C_32, _windowRect, DrawWindow, "Co-op  (" + Settings.MenuKey.Value + ")");
        }

        private void DrawPlayerList()
        {
            Controller.DescribePlayers(_lines);
            var y = 10f;
            foreach (var line in _lines)
            {
                ShadowLabel(new Rect(Screen.width - _row * 24 - 10, y, _row * 24, _row), line, TextAnchor.UpperRight);
                y += _row;
            }
        }

        private void DrawSpectating()
        {
            var target = Controller.Spectator.Target;
            if (target == null || Controller.Session == null) return;
            var name = Controller.Session.Players.TryGetValue(target.Value, out var p) ? p.Name : "#" + target.Value;
            var text = $"You are watching {name}   {Settings.SpectatePrevKey.Value} / {Settings.SpectateNextKey.Value} to switch";
            ShadowLabel(new Rect(0, Screen.height * 0.12f, Screen.width, _row), text, TextAnchor.MiddleCenter);
        }

        private void DrawNotices()
        {
            var y = Screen.height * 0.78f;
            foreach (var (text, until) in _notices)
            {
                var alpha = Mathf.Clamp01((until - Time.unscaledTime) / 1.5f);
                var old = GUI.color;
                GUI.color = new Color(1, 1, 1, alpha);
                ShadowLabel(new Rect(0, y, Screen.width, _row), text, TextAnchor.MiddleCenter);
                GUI.color = old;
                y += _row;
            }
        }

        private void ShadowLabel(Rect r, string text, TextAnchor anchor)
        {
            _shadowed.alignment = anchor;
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, old.a * 0.85f);
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, _shadowed);
            GUI.color = old;
            GUI.Label(r, text, _shadowed);
        }

        private void DrawWindow(int id)
        {
            var c = Controller;
            GUILayout.Label(StatusLine());

            var offline = c.Mode == CoopMode.Offline;
            GUI.enabled = offline;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(70));
            Settings.PlayerName.Value = GUILayout.TextField(Settings.PlayerName.Value, 24);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Address", GUILayout.Width(70));
            Settings.HostAddress.Value = GUILayout.TextField(Settings.HostAddress.Value, 64);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(70));
            _portText = GUILayout.TextField(_portText, 5);
            if (int.TryParse(_portText, out var port) && port > 0 && port < 65536) Settings.Port.Value = port;
            GUILayout.EndHorizontal();
            Settings.GuestsCanLead.Value = GUILayout.Toggle(Settings.GuestsCanLead.Value, " Any player can lead through doors (host)");
            GUI.enabled = true;

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (offline)
            {
                if (GUILayout.Button("Host"))
                {
                    c.GuestsCanLead = Settings.GuestsCanLead.Value;
                    c.StartHost(Settings.ToOptions(), Settings.Port.Value);
                }
                if (GUILayout.Button("Join"))
                {
                    c.GuestsCanLead = Settings.GuestsCanLead.Value;
                    c.Join(Settings.ToOptions(), Settings.HostAddress.Value.Trim(), Settings.Port.Value);
                }
            }
            else if (GUILayout.Button(c.IsHost ? "Close session" : "Leave"))
            {
                c.Leave();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label(c.IsHost
                ? "Start or continue a run: everyone who joins plays in your castle."
                : "Join from the manor or the title screen; you will be pulled into the host's run.");
            GUI.DragWindow();
        }

        private string StatusLine()
        {
            switch (Controller.Mode)
            {
                case CoopMode.Hosting:
                    return $"Hosting on port {Controller.Host.Port}, {Controller.Session.Players.Count} player(s) joined";
                case CoopMode.Connecting:
                    return "Connecting…";
                case CoopMode.Joined:
                    return $"Joined as #{Controller.Session.LocalId}, ping {Controller.Guest.HostRttMs} ms";
                default:
                    return "Not in a session";
            }
        }
    }
}
