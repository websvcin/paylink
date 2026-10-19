#!/usr/bin/env bash
# Builds the WebSvc.PayLink.* packages from packages/ into artifacts/packages,
# where nuget.config's "paylink-local" source picks them up.
# When the packages move to github.com/websvcin/paylink-packages and nuget.org, this script goes with them.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/packages"
mkdir -p "$out"

for project in "$root"/packages/src/*/*.csproj; do
  dotnet pack "$project" -c Release -o "$out" --nologo -v quiet
done

# Restore caches packages by version; drop cached copies so a rebuilt package with the same version is used.
rm -rf "${NUGET_PACKAGES:-$HOME/.nuget/packages}"/websvc.paylink.*
echo "Packages written to $out:"
ls -1 "$out"
