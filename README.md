# RL2 Co-op — online co-op for Rogue Legacy 2

An unofficial, free [BepInEx](https://github.com/BepInEx/BepInEx) mod that lets
2–4 friends run the same Rogue Legacy 2 castle together over the internet
(built for up to four; so far tested with two).

- **Same castle.** The host's run seed goes to every guest, and each copy of the
  game builds the identical castle.
- **Together through it.** The host leads: when the host goes through a door,
  everyone follows into that room. Guests can be allowed to lead too
  (`GuestsCanLead`).
- **See each other.** Every other player is drawn as their actual heir. Their
  position, facing, animation and attacks are smoothed over the network.
- **One set of enemies.** The host's game decides enemy health and deaths.
  A guest's hits are sent to the host, and every copy shows the result.
- **Death isn't the end of the evening.** A dead player watches a living one
  (`[` / `]` to switch), and the run ends when everyone is down.
- **Direct connection.** No accounts and no servers in between: the host opens
  a UDP port and friends connect to its address.

It ships none of the game's files. Every player needs their own copy of
Rogue Legacy 2.

*Unofficial and not affiliated with Cellar Door Games.*

## Status

Early. Played on macOS (Steam build, Unity 2020.3) with two copies of the
game on one machine, including over a simulated bad connection (150 ms lag,
5 % packet loss). On Windows (a virtual machine on the same Mac) the game
joined a Mac host, followed it between rooms and fought alongside it. See [TESTING.md](TESTING.md) for exactly what has been seen
working and what has not been tried yet.

## Install

1. Install **BepInEx 5.4.23** into the Rogue Legacy 2 folder:
   - **macOS:** use the `BepInEx_macos_universal` zip. Make `run_bepinex.sh`
     executable and set the game's Steam launch options to
     `"<game folder>/run_bepinex.sh" %command%`.
   - **Windows:** use the `BepInEx_win_x64` zip, or the release's
     `windows-full` zip, which already has BepInEx and the mod inside: unzip
     it next to `Rogue Legacy 2.exe` and skip steps 2 and 3.
2. Start the game once, so BepInEx creates its folders, then quit.
3. Copy the `RL2Coop` folder from the release into `BepInEx/plugins/`.
4. Start the game. The log should say `RL2 Co-op ready. Press F8 in game to host or join.`

## Playing with a friend

1. **The host** presses **F8** → *Host*. The default port is UDP **26950**.
   Forward it on the router, or use a virtual LAN (Tailscale, ZeroTier, Radmin
   VPN) and skip port forwarding.
2. **Each friend** presses **F8**, types the host's address (an IP or a host name) → *Join*.
3. The host goes into the castle. Guests enter from their manor and land in the
   host's castle, in the host's room.

The corner of the screen lists the players and their ping. A player in a
different room is marked *(other room)*.

## Settings

Edit `BepInEx/config/rl2coop.cfg` (it is created on first start):

| Section | Setting | Default | What it does |
|---|---|---|---|
| Session | `PlayerName` | Player | Name others see |
| Session | `HostAddress` / `Port` | 127.0.0.1 / 26950 | Where *Join* connects; the port *Host* opens |
| Session | `GuestsCanLead` | false | Guests' room changes move everyone too |
| Keys | `Menu`, `SpectatePrevious`, `SpectateNext` | F8, `[`, `]` | |
| Overlay | `ShowPlayerList` | true | Player list with ping |
| Network | `InterpolationDelayMs` | 100 | How far behind real time others are drawn; raise it on a bad connection |
| Network | `DisconnectTimeoutMs` | 8000 | When a silent player counts as gone |
| Debug | `SimulateLatencyMs`, `SimulateLossPercent` | 0 | Fake a bad connection to test with |
| Debug | `SaveFolder`, `AutoStart`, `AutoPlay`, `AutoWalk`, `AutoFight` | off | Test aids for running two copies unattended; see TESTING.md |

## How it works

| Part | Where | What it does |
|---|---|---|
| `RL2Coop.Net` | `src/RL2Coop.Net` | Game-free netcode: protocol, host/guest sessions over LiteNetLib, clock sync, snapshot interpolation, enemy mirror, spectating. Has 54 unit and loopback tests. |
| `RL2Coop` (plugin) | `src/RL2Coop.Plugin` | The BepInEx plugin: Harmony hooks into the game, puppets, overlay, F8 window. |
| Game adapter | `src/RL2Coop.Plugin/RL2` | The only code that names game types. Every game member it touches is listed in [docs/RL2-API.md](docs/RL2-API.md). |

- **Same castle.** A guest's next `GenerateNewSeed` is replaced with the host's
  seed, so its own castle builder produces the same rooms.
- **Players.** Each copy sends its hero's state 30 times a second over an
  unreliable channel. Others are drawn 100 ms in the past, between two received
  states, so movement stays smooth through jitter and lost packets.
- **Puppets.** A remote player is an inactive copy of the local hero with every
  script removed except the animator. It looks right but can't be hit and
  doesn't hit anything.
- **Enemies.** The host sends where its enemies are, 20 times a second. On a
  guest, each enemy's own brain and physics are switched off, so it moves only
  where the host's snapshots put it. Its health and death also come only from
  the host: a guest's hit is sent to the host instead of being applied.

## Building

```bash
dotnet build src/RL2Coop.Plugin -c Release -p:ManagedDir="<game>/RogueLegacy2.app/Contents/Resources/Data/Managed"
dotnet test tests/RL2Coop.Net.Tests
```

`ManagedDir` points at the game's own assemblies, which are referenced but
never copied into the repository. The `RL2Coop.Net` library and its tests build
without the game.

## Credits and notes

- [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (MIT) for UDP networking,
  vendored in `third_party/`.
- [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX).
- The open-source RL2 mods RL2.ModLoader, RL2Archipelago and RogueLegacy2Mods
  were the first map of the game's code.
- AI tools were used while building this mod.
- Rogue Legacy 2 belongs to Cellar Door Games. Please buy it, and if this mod
  breaks something, report it here, not to them.

## License

MIT, see [LICENSE](LICENSE). LiteNetLib keeps its own MIT license in `third_party/LiteNetLib`.
