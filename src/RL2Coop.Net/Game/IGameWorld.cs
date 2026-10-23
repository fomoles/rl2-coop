using System.Collections.Generic;
using RL2Coop.Net.Protocol;
using RL2Coop.Net.Session;

namespace RL2Coop.Net.Game
{
    /// <summary>
    /// Everything the co-op layer needs from the game, and nothing else. The
    /// Rogue Legacy 2 plugin implements this on top of the game's own classes;
    /// the tests implement it with a fake world. Keeping the boundary this
    /// narrow is what lets every rule of the co-op session (who leads, where a
    /// hit is decided, when the run ends) be tested without the game.
    ///
    /// All calls arrive on the game's main thread, from <see cref="CoopController.Tick"/>
    /// or from the controller's Local* methods.
    /// </summary>
    public interface IGameWorld
    {
        // ---- the local hero ----

        /// <summary>Position, velocity, animation and health of the local hero. False while there is no hero (menus, loading).</summary>
        bool TryReadLocalPlayer(out PlayerStateMsg state);

        /// <summary>Class and look of the current heir, as an opaque blob only the adapter understands.</summary>
        void ReadLocalLoadout(out int classId, out byte[] appearance);

        // ---- the run, driven by the host ----

        /// <summary>Guest only: generate the castle from the host's seed and drop the hero in at its start.</summary>
        void StartRun(int seed);

        /// <summary>
        /// Move the hero to the room the session is in: a guest following the
        /// host, or the host following a guest when guests may lead.
        /// <paramref name="x"/> and <paramref name="y"/> are where the leader entered; NaN means the room's
        /// default entrance (late join).
        /// </summary>
        void GoToRoom(int roomId, float x, float y);

        /// <summary>The run is over for everyone (all heroes dead, the host left, or victory).</summary>
        void EndRun(RunEndReason reason);

        // ---- other players' heroes ----

        /// <summary>Create the stand-in for another player, or refresh its look after a loadout change.</summary>
        void ShowPuppet(RemotePlayer player);

        /// <summary>Place the stand-in at an already interpolated state. Called every frame while it is in our room.</summary>
        void MovePuppet(byte playerId, in PlayerStateMsg state);

        /// <summary>One-shot moves (an attack swing, a spell, a dash) the stand-in should play.</summary>
        void PlayPuppetAction(byte playerId, in PlayerActionMsg action);

        /// <summary>Remove the stand-in (player left, or is in another room).</summary>
        void HidePuppet(byte playerId);

        /// <summary>Point the camera at a living player's stand-in; null returns it to the local hero.</summary>
        void Spectate(byte? playerId);

        // ---- enemies ----

        /// <summary>
        /// Host only: the enemies of the current room. NetId must mean the same
        /// enemy on every machine; the adapter uses spawn order within the room,
        /// which the shared seed makes identical.
        /// </summary>
        void ReadEnemies(List<EnemyState> into);

        /// <summary>Guest only: put an enemy (whose own AI the adapter has switched off) at the host's interpolated state.</summary>
        void MoveEnemy(in EnemyState state);

        /// <summary>Guest only: the host says this enemy died; play its death even if no snapshot showed it.</summary>
        void KillEnemy(ushort netId);

        /// <summary>Host only: a guest's attack connected. Apply it to the real enemy as if the host's hero had hit it.</summary>
        void ApplyHit(in HitRequestMsg hit);
    }
}
