#if RL2_GAME
using HarmonyLib;
using RL_Windows;
using SceneManagement_RL;
using UnityEngine;

namespace RL2Coop.Plugin.RL2
{
    /// <summary>
    /// Debug.AutoPlay: walks a fresh copy of the game from the title screen
    /// into the castle with no input, so two copies can be tested unattended.
    /// It presses Continue/New Game on the main menu and, in the tutorial or
    /// the manor, does what the pause menu's "Skip Tutorial" does: loads the
    /// castle.
    /// </summary>
    internal static class AutoPlay
    {
        private static string _scene;
        private static string _actedIn;
        private static float _enteredAt;

        internal static void Tick()
        {
            Fight();
            Die();
            if (CoopPlugin.Instance?.Settings?.AutoPlay.Value != true) return;
            if (SceneLoader_RL.IsLoading || SceneLoader_RL.IsRunningTransitionWithLogic) return;

            var scene = SceneLoader_RL.CurrentScene;
            if (scene != _scene)
            {
                _scene = scene;
                _actedIn = null;
                _enteredAt = Time.realtimeSinceStartup;
            }
            if (scene == _actedIn || Time.realtimeSinceStartup - _enteredAt < 3f) return;

            if (scene == SceneLoadingUtility.GetSceneName(SceneID.MainMenu))
            {
                var menu = WindowManager.GetWindowController(WindowID.MainMenu) as MainMenuWindowController;
                if (menu == null || Traverse.Create(menu).Field<bool>("m_isLoading").Value) return;
                Act(scene, "pressing Continue on the main menu");
                menu.LoadGame();
            }
            else if (scene == SceneLoadingUtility.GetSceneName(SceneID.Tutorial) || scene == SceneLoadingUtility.GetSceneName(SceneID.Town))
            {
                Act(scene, "going straight into the castle");
                WindowManager.CloseAllOpenWindows();
                SceneLoader_RL.LoadScene(SceneID.World, TransitionID.FadeToBlackWithLoading);
            }
        }

        /// <summary>
        /// Debug.AutoWalk: the stick position the hero walks with this frame,
        /// 0 when off. Right for two seconds, left for two, starting a few
        /// seconds after the castle is entered.
        /// </summary>
        internal static float WalkInput()
        {
            if (CoopPlugin.Instance?.Settings?.AutoWalk.Value != true || !RL2World.InBuiltCastle) return 0f;
            var t = Time.realtimeSinceStartup - _enteredAt - 5f;
            if (t < 0f) return 0f;
            return (int)(t / 2f) % 2 == 0 ? 1f : -1f;
        }

        private static float _nextSwing;
        private static string _soughtFightIn;

        /// <summary>
        /// Debug.AutoFight: a swing a second in the castle, through the same
        /// call the attack button makes, a step from the nearest living enemy
        /// (no pathfinding: the hero is put there); the host also walks into a room with
        /// enemies once, when every guest stands in its room, so all copies
        /// see that room's enemies from the start.
        /// </summary>
        private static void Fight()
        {
            var plugin = CoopPlugin.Instance;
            if (plugin?.Settings?.AutoFight.Value != true || !RL2World.InBuiltCastle || Hooks.LocalDown) return;
            if (SceneLoader_RL.IsLoading || SceneLoader_RL.IsRunningTransitionWithLogic) return;
            if (_scene != SceneLoader_RL.CurrentScene) return; // Tick has not noted the new scene yet
            var since = Time.realtimeSinceStartup - _enteredAt;
            if (plugin.Controller.IsHost && _soughtFightIn != _scene && since > 8f
                && plugin.Controller.OthersInRoom(plugin.Controller.Session.RoomId))
            {
                _soughtFightIn = _scene;
                if (!plugin.World.GoToNearestFight()) plugin.Log.LogInfo("AutoFight: no room with enemies found");
            }
            if (since < 5f || Time.realtimeSinceStartup < _nextSwing) return;
            _nextSwing = Time.realtimeSinceStartup + 1f;
            plugin.World.StepToNearestEnemy();
            var player = PlayerManager.GetPlayerController();
            if (player != null && player.CastAbility != null) player.CastAbility.StartAbility(CastAbilityType.Weapon);
        }

        private static string _diedIn;

        /// <summary>
        /// Debug.DieAfterSeconds: the hero dies that long after entering the
        /// castle, once per castle, through the same call a lethal hit makes.
        /// </summary>
        private static void Die()
        {
            var plugin = CoopPlugin.Instance;
            var after = plugin?.Settings?.DieAfterSeconds.Value ?? 0;
            if (after <= 0 || !RL2World.InBuiltCastle || _scene != SceneLoader_RL.CurrentScene || _diedIn == _scene) return;
            if (Time.realtimeSinceStartup - _enteredAt < after) return;
            var player = PlayerManager.GetPlayerController();
            if (player == null || player.IsDead) return;
            _diedIn = _scene;
            plugin.Log.LogInfo("DieAfterSeconds: the hero dies now");
            player.KillCharacter(player.gameObject, true);
        }

        private static void Act(string scene, string what)
        {
            _actedIn = scene;
            CoopPlugin.Instance.Log.LogInfo($"AutoPlay: {what}");
        }
    }
}
#endif
