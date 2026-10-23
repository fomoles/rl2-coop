# Testing RL2 Co-op

What has been seen working, how it was tested, and what has not been tested.
Everything here was checked on **2026-10-23**. The setup was two copies of
Rogue Legacy 2 (Steam build, Unity 2020.3.47f1, BepInEx 5.4.23.5
macos_universal) running on one MacBook Pro (M1 Pro, macOS 26). They talked
over `127.0.0.1` and over the Mac's own network address. A Windows copy of
the game, in a virtual machine on the same Mac, joined a game hosted on the
Mac and fought in it (see "Windows"). **A real internet connection between
two computers has not been tried.** What was checked instead is under
"Internet-like connection" below.

## How the test is set up

Two copies of one game cannot share a save or a window, so the test has its
own setup. It lives in `game/` (git-ignored):

```
game/host/RogueLegacy2.app   + BepInEx    → Debug.AutoStart = host
game/guest/RogueLegacy2.app  + BepInEx    → Debug.AutoStart = join, Debug.SaveFolder = <own folder>
```

`tools/two-copies.sh` installs the freshly built plugin into both copies. It
restarts the host first and the guest second, waits until each one is in the
castle, then prints the co-op lines of both logs. Each copy writes
`BepInEx/LogOutput.log`, and every line quoted below comes from those logs.

### Test aids (the `[Debug]` section of the config)

Nobody sat at the keyboard during these tests, so the heroes were driven by
test aids that are **off by default**:

| Setting | What it does | Does it fake anything? |
|---|---|---|
| `AutoStart` | hosts or joins as soon as the game is up | no: same calls as the F8 window |
| `AutoPlay` | presses Continue on the title screen and skips the manor into the castle | no |
| `AutoWalk` | the hero walks right for two seconds, then left for two, on a loop | no: it feeds the movement input |
| `AutoFight` | the hero swings its weapon once a second; the host walks into the nearest room with enemies once every guest is in its room | **yes, one shortcut:** before each swing the hero is *placed* a step from the nearest enemy, because nothing does any pathfinding |
| `DieAfterSeconds` | the hero dies N seconds after entering the castle | uses the same death call a lethal hit makes |
| `SaveFolder` | the guest copy keeps its own save | no |

Network play itself (packets, rooms, enemy state, hits) goes through exactly
the same code whether or not these aids are on.

## Seen working

Each line below names the proof. Screenshots are in `proof/`. In every
screenshot the top-right corner shows the co-op player list with ping. For
every run listed, the game's own `Player.log` had **0 exceptions** in both
copies.

| What | Proof |
|---|---|
| Guest joins the host by IP (UDP); both see each other in the player list with ping | `proof/guest-1.jpg`; log `joined as #1; 1 other player(s); run in progress` |
| Same castle on both: the guest builds the host's castle from its seed (`7E8BD3CF`) | log `Next castle will be built from the host's seed 7E8BD3CF`; same room layout in every pair of screenshots |
| Rooms together: the host walks through a door and the guest is moved into the same room | log `Moving to room 3196653 at 95.4,7.0` / `Room 3196653: 2 enemies, moved by the host` |
| The guest sees the host's hero move, turn and animate | `proof/guest-sees-host-walking-1..4.jpg` |
| The host sees the guest's hero | `proof/host-sees-guest-1.jpg` |
| Both copies show the same enemies in the same places and poses | `proof/enemies-1-host.jpg` + `enemies-1-guest.jpg`, `enemies-2-*` |
| A guest's hit goes to the host, which applies it | `proof/logs/fight-*.log`: guest `Hit on enemy 2 for 30 sent to the host` → host `Guest hit enemy 2 for 30` |
| Kills are shared: an enemy the host's game says is dead dies on the guest too | same logs: guest `Host says enemy 2 died`, then `enemy 1` the same way; `proof/fight-guest-1..4.jpg`, 0.7 s apart, are the guest's view: two skeletons, the hero strikes the first, it is gone, the second takes a 30 hit, the room is empty |
| A dead guest watches the host: the camera follows the host, a banner says so, and both player lists mark the guest as dead | `proof/spectate-1-guest.jpg` (banner `You are watching Host`, list `(dead)`) + `proof/spectate-1-host.jpg` (list `#2 Guest 0 ms (dead)`); `proof/logs/spectate-*.log`: guest log `DieAfterSeconds: the hero dies now` → `You died. Watching your team`; host log `Guest died` |

In the fight run, both copies' `Player.log` had 0 exceptions and neither log
had a `dropped bad` packet line. The host's window could not be captured
during that fight (it sat on another desktop), so the host's side of it is
proof by log only.

### Internet-like connection

Both copies were run with `SimulateLatencyMs = 150` and
`SimulateLossPercent = 5`: every packet is held back 150 ms and one in twenty
is thrown away, in both directions. That is worse than a typical connection
between two countries. The guest joined over the Mac's network address
(`10.243.96.73`, not `127.0.0.1`) and, in a second run, by host name
(`localhost`).

| What | Proof |
|---|---|
| Join, same castle, rooms together and shared kills over 150 ms lag and 5 % loss | `proof/lag150-loss5-{host,guest}.log`: host `Guest joined from 10.243.96.73`; guest `Room 3183949: 2 enemies, moved by the host`, hits on enemies 1 and 2, `Host says enemy N died` |
| Joining by host name, same lag and loss | `proof/join-by-name-lag150-loss5-*.log`: guest `looking up localhost` → `Joined as player 2` |
| The game keeps running while a host name is looked up | unit test `Joining_by_host_name_does_not_freeze_the_game`: the lookup is made to take 1.5 s, Join returns in under 0.3 s |

Both runs had 0 exceptions in `Player.log`. Before these runs the simulation
settings did nothing in the released build (they were compiled only into
debug builds); that is fixed.

### Windows

**Started on Windows and joined the Mac.** Windows 11 (ARM, running the x64
game through Windows' own emulation) ran in a UTM virtual machine on the same
Mac, with its own network address (`192.168.64.2`; the Mac is
`192.168.64.1`). The Windows copy of the game with
`RL2Coop-0.1.0-windows-full.zip` unpacked into it was set to join
automatically. The Mac copy hosted. What happened, in `proof/windows-vm/`:

- BepInEx for Windows started and loaded the mod (`System platform: Bits64,
  Windows`, `Loading [RL2 Co-op 0.1.0]`).
- The mod connected to the Mac over the virtual network, joined as player 2
  and got the host's castle seed: `joined as #1; 1 other player(s); run in
  progress`, then `Next castle will be built from the host's seed 7E8BD3CF`
  (`guest-BepInEx-LogOutput.log`).
- The Mac saw it arrive: `#1 Windows joined from 192.168.64.2:52158`
  (`host-mac-coop-lines.log`).
- The mod's player list on Windows showed `#2 Windows (you)` and
  `#1 Host (host) 11 ms` (`guest-joined-overlay.jpg`).

**The first try stopped there.** The Windows game needs the Steam app running
on the same computer, and the virtual machine had no Steam yet. The game
showed "Steam initialization failed" (`guest-steam-init-failed.jpg`) and quit
before reaching the castle. This is the game's own check, not the mod's.

**Played, once Steam was installed.** The owner installed Steam in the
virtual machine and signed in. Then the Windows copy went the whole way, in
`proof/windows-vm/play-*`:

- It loaded the castle from the host's seed and showed the host:
  `Showing Host (#1)` on Windows, `Showing Windows (#2)` on the Mac.
- It followed the host between rooms: `Moving to room 3183949 at 7.5,-1.0`,
  then `Room 3183949: 2 enemies, moved by the host`.
- It fought: `Hit on enemy 2 for 41 sent to the host`, `Host says enemy 2
  died`, the same for enemy 1. The Mac logged `Guest hit enemy 2 for 41` and
  `Guest hit enemy 1 for 30`. Both skeletons died
  (`play-windows-guest-skeletons.jpg`, `play-windows-guest-after-fight.jpg`).
- It left cleanly: closing the Windows game made the Mac log `#1 Windows
  left (RemoteConnectionClose)`.

Logs: `play-guest-windows.log` (Windows) and `play-host-mac.log` (Mac). The
mod's player list showed `#1 Host (host) 23 ms` on Windows and `#2 Windows
56 ms` on the Mac (`play-mac-host-view.jpg`). In both screenshots after the
fight only one knight can be seen. Both heroes were placed at the same spot
when they entered the room, so the second is most likely drawn underneath;
the screenshots do not prove it either way.

**Found on Windows and fixed: a ghost player.** In an earlier try the Windows
game was killed and started again. It came back as `#2`, while the Mac kept
the old `#1`, which no longer existed, in its player list for good
(`ghost-before-fix-host.log`: `#1 Windows joined`, then `#2 Windows joined`;
only #2 ever left). The host now drops any player it has heard nothing
from for longer than the disconnect timeout plus 2 seconds, even when the
network library never reports the disconnect. A unit test kills a guest
without a goodbye and checks that the host lets it go.

**Checked before that, from the Mac.** These checks were made against the
Windows copy of the game (Steam depot 1253921, downloaded with the owner's
Steam account):

- **Same engine.** The Windows build uses the same Unity 2020.3.47f1 and Mono
  runtime as the Mac build, so BepInEx 5 for Windows loads plugins the same way.
- **Same game code.** `tools/apidump` lists every type, method and field of the
  game's code on each platform; `proof/windows-vs-mac-api.diff` is the
  difference. It has only Windows-only additions (window and monitor helpers,
  focus checks, the Steam library name, the webcam API). Nothing the mod uses
  is missing or different. The one listed type the mod touches,
  `MainMenuWindowController`, keeps its `LoadGame` method on both.
- **Builds against Windows.** The plugin compiles with 0 errors against the
  Windows game's DLLs.
- **No Mac-only code in the mod.** It has no file paths, no native calls and
  no platform checks. The network library (LiteNetLib) supports Windows.

`out/RL2Coop-0.1.0-windows-full.zip` (made by `tools/package.sh`) is BepInEx
for Windows plus the plugin, unzipped next to the game's `.exe`, with an
install guide in Ukrainian (`INSTALL-UK.txt`).

## Not seen working yet

- **Bosses.** The test aids never got the heroes to a boss room. Bosses
  use the same enemy code path, but nobody has watched one.
- **3 or 4 players.** Only two copies fit on this machine. The network code
  is tested for more players by unit tests (below), not in the game.
- **Two different computers** over a real network, NAT and port forwarding.
  A bad connection was simulated (above), and a Windows virtual machine
  joined over a virtual network (above), but no real internet connection.
- **Two Windows computers together**, or Windows hosting. On Windows only
  the guest side was played, against a Mac host.
- **Long runs**, such as a whole castle, biome changes, or the end of a run on
  the host's side.
- **Hand-played sessions.** All the fighting above was driven by the
  test aids.

## Unit tests

```
dotnet test tests/RL2Coop.Net.Tests
```

54 tests cover the network core, which does not need the game: a session over
a real UDP loopback, interpolation, clock sync, enemy mirroring, spectating,
joining by host name, dropping a guest that vanished without a goodbye, and
the controller's rules. **Result on 2026-10-23: 54 passed.** An earlier run had one failure: a host name that does not exist took
about 8 seconds to be rejected, and the game froze meanwhile. The lookup now
runs in the background, so the game keeps running and the message appears
when the answer comes.

## Known issues

- **The host's death notice was not seen in the last fight run.** The host
  sends a reliable "enemy died" message when the game reports an enemy death,
  and an earlier run logged it (`Enemy 2 died; telling the guests`). In the
  last run the host never logged it, so the guest learned of both deaths from
  the dead flag in the host's regular enemy updates instead, which arrive many
  times a second. The result on screen is the same; the cause is not found yet.
- **While spectating, the host's hero was not visible in either window** in
  the screenshots. The camera, the banner and the player lists were right, and
  no co-op code hides the host's own hero. Not investigated yet.
- A guest's kills are counted by the host's game (the host's save gets the
  kill statistics). On the guest, the kill is credited to the guest's own hero.
- The test aid `AutoFight` teleports the hero next to an enemy. That is a
  testing shortcut, not something the mod does in normal play.
