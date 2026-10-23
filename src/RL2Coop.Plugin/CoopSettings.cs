using BepInEx.Configuration;
using RL2Coop.Net.Protocol;
using RL2Coop.Net.Session;
using UnityEngine;

namespace RL2Coop.Plugin
{
    /// <summary>Everything a player can set, stored in BepInEx/config/rl2coop.cfg.</summary>
    internal sealed class CoopSettings
    {
        public readonly ConfigEntry<string> PlayerName;
        public readonly ConfigEntry<string> HostAddress;
        public readonly ConfigEntry<int> Port;
        public readonly ConfigEntry<bool> GuestsCanLead;

        public readonly ConfigEntry<KeyCode> MenuKey;
        public readonly ConfigEntry<KeyCode> SpectatePrevKey;
        public readonly ConfigEntry<KeyCode> SpectateNextKey;
        public readonly ConfigEntry<bool> ShowPlayerList;

        public readonly ConfigEntry<float> InterpolationDelayMs;
        public readonly ConfigEntry<int> DisconnectTimeoutMs;
        public readonly ConfigEntry<int> SimulateLatencyMs;
        public readonly ConfigEntry<int> SimulateLossPercent;
        public readonly ConfigEntry<string> SaveFolder;
        public readonly ConfigEntry<string> AutoStart;
        public readonly ConfigEntry<bool> AutoPlay;
        public readonly ConfigEntry<bool> AutoWalk;
        public readonly ConfigEntry<bool> AutoFight;
        public readonly ConfigEntry<int> DieAfterSeconds;

        public CoopSettings(ConfigFile cfg)
        {
            PlayerName = cfg.Bind("Session", "PlayerName", "Player",
                "Name the other players see above your hero.");
            HostAddress = cfg.Bind("Session", "HostAddress", "127.0.0.1",
                "IP address (or host name) to join. The host gives you theirs.");
            Port = cfg.Bind("Session", "Port", ProtocolInfo.DefaultPort,
                "UDP port. The host must let it through the firewall / forward it on the router.");
            GuestsCanLead = cfg.Bind("Session", "GuestsCanLead", false,
                "Host only. When on, any player walking through a door takes everyone along; when off, only the host does.");

            MenuKey = cfg.Bind("Keys", "Menu", KeyCode.F8, "Open or close the co-op window.");
            SpectatePrevKey = cfg.Bind("Keys", "SpectatePrevious", KeyCode.LeftBracket, "While dead: watch the previous player.");
            SpectateNextKey = cfg.Bind("Keys", "SpectateNext", KeyCode.RightBracket, "While dead: watch the next player.");
            ShowPlayerList = cfg.Bind("Overlay", "ShowPlayerList", true, "Show who is in the session and their ping in a corner.");

            InterpolationDelayMs = cfg.Bind("Network", "InterpolationDelayMs", 100f,
                new ConfigDescription("How far in the past other players are drawn. Higher hides more lag, lower feels more immediate.",
                    new AcceptableValueRange<float>(30f, 400f)));
            DisconnectTimeoutMs = cfg.Bind("Network", "DisconnectTimeoutMs", 8000,
                new ConfigDescription("Silence after which a player counts as lost.", new AcceptableValueRange<int>(2000, 60000)));
            SimulateLatencyMs = cfg.Bind("Debug", "SimulateLatencyMs", 0,
                new ConfigDescription("Testing aid: add this much delay to every packet.", new AcceptableValueRange<int>(0, 1000)));
            SimulateLossPercent = cfg.Bind("Debug", "SimulateLossPercent", 0,
                new ConfigDescription("Testing aid: drop this share of packets.", new AcceptableValueRange<int>(0, 50)));
            SaveFolder = cfg.Bind("Debug", "SaveFolder", "",
                "Testing aid: keep this copy's saves in this folder instead of the game's own, so two copies on one computer do not share a save. Empty = the game's folder.");
            AutoStart = cfg.Bind("Debug", "AutoStart", "",
                new ConfigDescription("Testing aid: 'host' or 'join' starts the session as soon as the game is up, without the co-op window.",
                    new AcceptableValueList<string>("", "host", "join")));
            AutoPlay = cfg.Bind("Debug", "AutoPlay", false,
                "Testing aid: go from the title screen into the castle with no input (Continue, skip the tutorial, leave the manor).");
            AutoWalk = cfg.Bind("Debug", "AutoWalk", false,
                "Testing aid: in the castle the hero walks left and right on its own, so another copy of the game has movement to mirror.");
            AutoFight = cfg.Bind("Debug", "AutoFight", false,
                "Testing aid: in the castle the hero swings its weapon every second, and the host goes into the nearest room with enemies.");
            DieAfterSeconds = cfg.Bind("Debug", "DieAfterSeconds", 0,
                new ConfigDescription("Testing aid: the hero dies this many seconds after entering the castle, to try spectating. 0 = off.",
                    new AcceptableValueRange<int>(0, 3600)));
        }

        public CoopOptions ToOptions()
        {
            var o = new CoopOptions
            {
                PlayerName = string.IsNullOrWhiteSpace(PlayerName.Value) ? "Player" : PlayerName.Value.Trim(),
                DisconnectTimeoutMs = DisconnectTimeoutMs.Value,
                SimulateLatencyMs = SimulateLatencyMs.Value,
                SimulateLossPercent = SimulateLossPercent.Value,
            };
            o.Interpolation.Delay = InterpolationDelayMs.Value / 1000.0;
            return o;
        }
    }
}
