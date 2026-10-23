# Rogue Legacy 2 members the adapter touches

The adapter (`src/RL2Coop.Plugin/RL2/`) was first drafted from open-source RL2
mods (RL2.ModLoader, RL2Archipelago, RogueLegacy2Mods) before the game was on
the build machine. Every member below has since been checked against the
decompiled `Assembly-CSharp.dll` of the Steam build (Unity 2020.3.47f1, Mono)
and compiles against it; **used** means it is also exercised in the two-copy
test described in `TESTING.md`.

To look one up: `ilspycmd -t <Full.Type.Name> -r <Managed> <Managed>/Assembly-CSharp.dll`
(CorgiEngine types need their namespace, e.g. `MoreMountains.CorgiEngine.CharacterHorizontalMovement_RL`).

## Events and the frame

| Member | Status | Used for |
|---|---|---|
| `WindowManager.Update` (namespace `RL_Windows`) — Harmony postfix | used | the per-frame tick; a BepInEx plugin's own `Update` never runs in RL2 |
| `GameManager.OnApplicationFocus` — Harmony prefix | used | the game pauses when it loses focus; skipped while a session is open |
| `Messenger<GameMessenger, GameEvent>.AddListener` | used | room, enemy and world events |
| `GameEvent.PlayerEnterRoom` + `RoomViaDoorEventArgs.Room` | used | room changes |
| `GameEvent.EnemySummoned` + `EnemySummonedEventArgs.SummonedEnemy` | compiles | numbering summoned enemies |
| `GameEvent.EnemyDeath` + `EnemyDeathEventArgs.Victim` | compiles | host reports kills |
| `GameEvent.WorldCreationComplete` | used | host announces a run |
| `SaveFileSystem.PersistentDataPath` getter — Harmony postfix | used | `Debug.SaveFolder`: a second copy on one machine keeps its own saves |

## Run, castle and rooms

| Member | Status | Used for |
|---|---|---|
| `RNGSeedManager.GetCurrentSeed("World")`, `GetSeedAsHex` | used | the seed the castle was built from |
| `RNGSeedManager.GenerateNewSeed` — Harmony prefix | used | a guest builds the host's castle instead of a fresh one |
| `SceneLoader_RL.LoadScene(SceneID.World, TransitionID.FadeToBlackWithLoading)`, `.CurrentScene`, `.IsLoading` | used | rebuilding the castle; AutoPlay skipping the tutorial/manor |
| `MainMenuWindowController.LoadGame` | used | AutoPlay pressing Continue |
| `BaseRoom.BiomeType`, `BiomeType.HubTown` | used | the manor stays private |
| `BaseRoom.PlacePlayerInRoom(Vector3 local)` | used | following the host into a room (the call the game's own tunnels make; it fires `PlayerEnterRoom`) |
| `BaseRoom.PlacePlayerInRoom(Door)`, `BaseRoom.Doors` | used | `Debug.AutoFight`: the host walks in through a door |
| `BaseRoom.SpawnControllerManager.EnemySpawnControllers` → `ShouldSpawn`, `EnemyInstance` | used | enemy ids in spawn order; instances exist only after the room is entered |

## Heroes

| Member | Status | Used for |
|---|---|---|
| `PlayerManager.GetPlayerController()`, `PlayerManager.IsInstantiated` | used | local hero |
| `PlayerController.Animator` | used | puppet animation; the animator sits on the hero's **root** object, so a copy of it is a copy of the whole hero |
| `PlayerController.IsFacingRight` | used | facing flag |
| `PlayerController.CurrentHealth`, `.ActualMaxHealth`, `.IsDead`, `.KillCharacter` | used / compiles | hero state, death |
| `PlayerController.CastAbility.StartAbility(CastAbilityType.Weapon)` | used | `Debug.AutoFight` swings, the same call the attack button makes |
| `CharacterHorizontalMovement_RL.HandleHorizontalMovement` (prefix) + `SetHorizontalMove(float)` | used | `Debug.AutoWalk`: walking with no keyboard (a game in the background gets no keys) |
| `SaveManager.PlayerSaveData.CurrentCharacter.ClassType` | compiles | loadout |

## Enemies and camera

| Member | Status | Used for |
|---|---|---|
| `EnemyController.Animator`, `.IsBoss`, `.IsFacingRight`, `.IsDead` | compiles | enemy state on the host |
| `EnemyController.SetHealth`, `.KillCharacter` — Harmony prefixes | compiles | on a guest, enemies take health and death only from the host |
| `CameraController.CinemachineBrain.ActiveVirtualCamera.Follow` | compiles | spectating points the room's camera at a puppet |

"Compiles" members are checked against the decompiled game but were not reached
in the unattended test; see `TESTING.md` for which behaviours that leaves unproven.
