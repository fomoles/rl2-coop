#if RL2_GAME
using HarmonyLib;
using RL_Windows;
using RL2Coop.Net.Game;

namespace RL2Coop.Plugin.RL2
{
    /// <summary>
    /// The Harmony patches that route the game's own decisions through the
    /// co-op controller: who may damage an enemy, what dying means while
    /// friends are alive, and a per-frame tick (RL2 does not call Update on
    /// the plugin object).
    /// </summary>
    internal static class Hooks
    {
        /// <summary>True while our own code ends the run, so the death patch lets the real death through.</summary>
        internal static bool AllowRealDeath;

        /// <summary>The local hero is down and spectating; other players are still fighting.</summary>
        internal static bool LocalDown;

        internal static void Install(string id) => new Harmony(id).PatchAll(typeof(Hooks).Assembly);

        private static CoopController Controller => RL2World.Current?.Controller;

        private static bool InSession => Controller != null && Controller.Mode != CoopMode.Offline;

        /// <summary>Backstop: re-create the driver if anything destroyed it. WindowManager.Update runs every frame in every scene.</summary>
        [HarmonyPatch(typeof(WindowManager), "Update")]
        private static class Tick
        {
            private static void Postfix()
            {
                CoopPlugin.Instance?.EnsureDriver();
                RL2World.Current?.Tick();
                AutoPlay.Tick();
            }
        }

        /// <summary>
        /// On a guest, an enemy's health belongs to the host. A guest's hit is
        /// sent to the host instead of being applied; the result comes back in
        /// the next enemy snapshot. Health changes the adapter makes itself
        /// (mirroring the host) pass through.
        /// </summary>
        [HarmonyPatch(typeof(EnemyController), nameof(EnemyController.SetHealth))]
        private static class GuestEnemyDamage
        {
            private static bool Prefix(EnemyController __instance, float value, bool additive)
            {
                var c = Controller;
                if (c == null || !c.IsGuest || RL2World.ApplyingHostState) return true;
                if (!additive || value >= 0) return true;
                if (!RL2World.Current.TryGetNetId(__instance, out var netId)) return true;
                CoopPlugin.Instance.Log.LogInfo($"Hit on enemy {netId} for {-value:0} sent to the host");
                return c.LocalHitsEnemy(netId, -value, false);
            }
        }

        /// <summary>On a guest, only the host kills enemies.</summary>
        [HarmonyPatch(typeof(EnemyController), nameof(EnemyController.KillCharacter))]
        private static class GuestEnemyDeath
        {
            private static bool Prefix(EnemyController __instance)
            {
                var c = Controller;
                if (c == null || !c.IsGuest || RL2World.ApplyingHostState) return true;
                return !RL2World.Current.TryGetNetId(__instance, out _);
            }
        }

        /// <summary>
        /// Dying while someone else is still alive does not end the run: the
        /// hero goes down and the camera follows a living player. When the
        /// last player falls, the controller ends the run and the real death
        /// (and the game's death screen) happens.
        /// </summary>
        [HarmonyPatch(typeof(PlayerController), nameof(PlayerController.KillCharacter))]
        private static class LocalDeath
        {
            private static bool Prefix(PlayerController __instance)
            {
                if (AllowRealDeath || !InSession) return true;
                if (LocalDown) return false;
                LocalDown = true;
                __instance.gameObject.SetActive(false);
                Controller.LocalDied();
                return false;
            }
        }

        /// <summary>
        /// A guest's castle is built from the host's seed. Every path into the
        /// castle (new heir, locked castle, continue) sets a master seed and then
        /// calls GenerateNewSeed("World"); replacing the master seed right
        /// before that call makes all of them build the host's castle.
        /// </summary>
        [HarmonyPatch(typeof(RNGSeedManager), nameof(RNGSeedManager.GenerateNewSeed))]
        private static class SharedSeed
        {
            private static void Prefix(string sceneName)
            {
                var seed = RL2World.PendingSeed;
                if (seed.HasValue && sceneName == "World" && RNGSeedManager.MasterSeedOverride != seed.Value)
                    RNGSeedManager.SetMasterSeedOverride(seed.Value);
            }
        }

        /// <summary>
        /// The game pauses itself when its window loses focus. A co-op run
        /// cannot pause for one player, so while in a session (or under
        /// Debug.AutoPlay) an alt-tab leaves the game running.
        /// </summary>
        [HarmonyPatch(typeof(GameManager), "OnApplicationFocus")]
        private static class NoPauseOnFocusLoss
        {
            private static bool Prefix(bool focus) =>
                focus || !(InSession || CoopPlugin.Instance?.Settings?.AutoPlay.Value == true);
        }

        /// <summary>Debug.AutoWalk: overrides the stick after the game has read it, through the game's own movement call.</summary>
        [HarmonyPatch(typeof(MoreMountains.CorgiEngine.CharacterHorizontalMovement_RL), "HandleHorizontalMovement")]
        private static class Walk
        {
            private static void Prefix(MoreMountains.CorgiEngine.CharacterHorizontalMovement_RL __instance)
            {
                var input = AutoPlay.WalkInput();
                if (input != 0f) __instance.SetHorizontalMove(input);
            }
        }

        /// <summary>Debug.SaveFolder: a second copy of the game on the same computer keeps its own save.</summary>
        [HarmonyPatch(typeof(SaveFileSystem), nameof(SaveFileSystem.PersistentDataPath), MethodType.Getter)]
        private static class SeparateSaves
        {
            private static void Postfix(ref string __result)
            {
                var folder = CoopPlugin.Instance?.Settings?.SaveFolder.Value;
                if (!string.IsNullOrWhiteSpace(folder)) __result = folder;
            }
        }
    }
}
#endif
