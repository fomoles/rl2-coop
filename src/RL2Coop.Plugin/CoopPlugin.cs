using BepInEx;
using RL2Coop.Net.Game;

namespace RL2Coop.Plugin
{
    /// <summary>
    /// BepInEx entry point. Reads the config, builds the controller around the
    /// game adapter and starts the driver. Nothing networked happens until the
    /// player presses Host or Join in the co-op window.
    /// </summary>
    [BepInPlugin(Id, "RL2 Co-op", "0.1.0")]
    public sealed class CoopPlugin : BaseUnityPlugin
    {
        public const string Id = "rl2coop";

        internal static CoopPlugin Instance { get; private set; }
        internal CoopSettings Settings { get; private set; }
        internal CoopController Controller { get; private set; }
#if RL2_GAME
        internal RL2.RL2World World { get; private set; }
#endif
        internal BepInEx.Logging.ManualLogSource Log => Logger;
        private CoopDriver _driver;

        private void Awake()
        {
            Instance = this;
            // A networked game must keep its loop running while unfocused.
            UnityEngine.Application.runInBackground = true;
            Settings = new CoopSettings(Config);
            var log = new BepInLog(Logger);
#if RL2_GAME
            var world = World = new RL2.RL2World(log);
            Controller = new CoopController(world, log);
            world.Attach(Controller);
            RL2.Hooks.Install(Id);
            EnsureDriver();
            Logger.LogInfo($"RL2 Co-op ready. Press {Settings.MenuKey.Value} in game to host or join.");
#else
            Logger.LogError("This build of RL2 Co-op was made without the game's assemblies and cannot play. Rebuild with -p:ManagedDir=<game>/Managed.");
#endif
        }

        /// <summary>Re-creates the driver if a scene change or the game destroyed it. Called from a per-frame game hook.</summary>
        internal void EnsureDriver()
        {
            if (Controller == null || _driver) return;
            _driver = CoopDriver.Create(Controller, Settings);
        }

        private void OnDestroy() => Controller?.Dispose();
    }
}
