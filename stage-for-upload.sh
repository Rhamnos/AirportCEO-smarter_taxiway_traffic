#!/usr/bin/env bash
# Stage the mod where the in-game Modding menu's upload button reads from.
#
# The game uploads from AppData/Roaming/.../Airport CEO/Mods/<folder>/, not from
# this project. Run this, then launch the game and publish from the Modding menu.
#
# There is no public documentation for publishing a code-based mod -- the wiki and
# the Mod Loader's own docs cover subscribing only. The decompiled
# ModManager.UploadMod / NativeModContainer is the authoritative source.
#
# After the FIRST successful upload, Steam assigns a published file id. Patch it
# into package/ModData.json, because the game never writes it back and UploadMod
# forces PublishedFileId_t.Invalid when it is 0 -- so a second upload without it
# creates a DUPLICATE Workshop item instead of updating the existing one.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MODS="$HOME/.steam/debian-installation/steamapps/compatdata/673610/pfx/drive_c/users/steamuser/AppData/Roaming/Apoapsis Studios/Airport CEO/Mods/SmarterTaxiwayTraffic"
PLUGINS="$HOME/.steam/debian-installation/steamapps/common/Airport CEO/BepInEx/plugins"

if pgrep -x "Airport CEO.exe" >/dev/null 2>&1; then
  echo "Airport CEO is running -- close it first." >&2; exit 1
fi

if [ ! -f "$HERE/package/Logo512.png" ]; then
  echo "package/Logo512.png is missing. The upload needs a 512x512 icon." >&2; exit 1
fi

( cd "$HERE/src" && dotnet build -c Release -v quiet --nologo )
mkdir -p "$MODS/plugins"
cp "$HERE/src/bin/Release/AirportCEOAdvancedTaxiways.dll" "$HERE/package/plugins/"
cp "$HERE/package/plugins/AirportCEOAdvancedTaxiways.dll" "$MODS/plugins/"
cp "$HERE/package/ModData.json" "$HERE/package/Logo512.png" "$MODS/"

# Two folders sharing the plugin GUID means BepInEx silently skips one. While a
# staged copy exists, the loose BepInEx/plugins build has to go.
rm -f "$PLUGINS/AirportCEOAdvancedTaxiways.dll"

echo "staged to $MODS"
echo "removed the loose BepInEx/plugins copy -- two copies share a GUID and one gets skipped"
echo "now launch the game and publish from the Modding menu"
