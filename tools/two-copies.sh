#!/bin/zsh
# Two-copy test on one Mac: install the built plugin into game/host and
# game/guest, restart both (host first), wait until each is in the castle,
# then print the co-op lines of both logs and the game's exception count.
#
# Setup, once: game/host and game/guest are each a copy of RogueLegacy2.app
# with BepInEx 5.4.23 (macos_universal) next to it. The guest's
# BepInEx/config/rl2coop.cfg sets Debug.SaveFolder so it keeps its own save,
# and both set Debug.AutoStart (host / join) and Debug.AutoPlay = true.
# See TESTING.md.
cd "${0:A:h}/.." || exit 1
for d in host guest; do cp src/RL2Coop.Plugin/bin/Release/net472/RL2Coop*.dll game/$d/BepInEx/plugins/RL2Coop/; done
pkill -f "game/host/RogueLegacy2.app"; pkill -f "game/guest/RogueLegacy2.app"
for i in $(seq 1 30); do pgrep -f "game/(host|guest)/RogueLegacy2" >/dev/null || break; sleep 1; done
for d in host guest; do
  (cd game/$d && : > BepInEx/LogOutput.log && (./run_bepinex.sh > launch.log 2>&1 &))
  for i in $(seq 1 90); do grep -q "Scene: World" game/$d/BepInEx/LogOutput.log 2>/dev/null && break; sleep 2; done
  sleep 10
done
for d in host guest; do echo "--- $d"; grep "RL2 Co-op" game/$d/BepInEx/LogOutput.log | tail -12; done
# Both copies write the game's own log to the same place; the one started
# first has been rotated to Player-prev.log by the second.
L=~/Library/Logs/"Cellar Door Games/Rogue Legacy 2"
echo "--- exceptions in the game's own log (guest Player.log, host Player-prev.log)"
grep -c Exception "$L/Player.log" "$L/Player-prev.log"
