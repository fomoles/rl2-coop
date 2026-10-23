#!/bin/zsh
# Builds the release zips into out/:
#   RL2Coop-<ver>.zip               the plugin folder only (goes into BepInEx/plugins/)
#   RL2Coop-<ver>-windows-full.zip  BepInEx 5 for Windows + the plugin, unzip next to the game's .exe
# The BepInEx zip is downloaded once into game/dl-bepinex-win/ and checked against its known hash.
set -e
cd "${0:A:h}/.."
VER=0.1.0
BEPINEX=BepInEx_win_x64_5.4.23.5.zip
BEPINEX_SHA=82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4
BIN=src/RL2Coop.Plugin/bin/Release/net472

[[ -f $BIN/RL2Coop.dll && -f $BIN/RL2Coop.Net.dll ]] || { echo "build the plugin in Release first"; exit 1; }
mkdir -p game/dl-bepinex-win out
if [[ ! -f game/dl-bepinex-win/$BEPINEX ]]; then
  curl -sSLf -o game/dl-bepinex-win/$BEPINEX "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/$BEPINEX"
fi
echo "$BEPINEX_SHA  game/dl-bepinex-win/$BEPINEX" | shasum -a 256 -c -

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT

# Plugin only
mkdir -p $stage/plugin/RL2Coop
cp $BIN/RL2Coop.dll $BIN/RL2Coop.Net.dll $stage/plugin/RL2Coop/
cp third_party/LiteNetLib/LICENSE.txt $stage/plugin/RL2Coop/LiteNetLib-LICENSE.txt
rm -f out/RL2Coop-$VER.zip
(cd $stage/plugin && zip -qr -X "$OLDPWD/out/RL2Coop-$VER.zip" RL2Coop)

# Windows, everything in one
mkdir -p $stage/win
(cd $stage/win && unzip -q "$OLDPWD/game/dl-bepinex-win/$BEPINEX")
mkdir -p $stage/win/BepInEx/plugins
cp -R $stage/plugin/RL2Coop $stage/win/BepInEx/plugins/
cp tools/windows-guide-uk.md "$stage/win/INSTALL-UK.txt"
rm -f out/RL2Coop-$VER-windows-full.zip
(cd $stage/win && zip -qr -X "$OLDPWD/out/RL2Coop-$VER-windows-full.zip" .)

ls -la out/
unzip -l out/RL2Coop-$VER-windows-full.zip | tail -n +4 | awk '{print $4}' | grep -v '^$' | grep -v '/$'
