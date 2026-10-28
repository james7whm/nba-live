#!/bin/bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root/macos"
architecture="${1:-$(uname -m)}"
case "$architecture" in arm64|x86_64) ;; *) echo 'Choose arm64 or x86_64' >&2; exit 2;; esac
if [[ "$(uname -s)" != Darwin ]]; then echo 'A Mac with Xcode is required to build the app bundle.' >&2; exit 2; fi
version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' Info.plist)"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo 'Invalid version' >&2; exit 2; }
output="$root/artifacts/macos-swift-preview-$architecture"
archive="$root/artifacts/NbaLive-$version-swift-preview-macos12-$architecture.zip"
if [[ -e "$output" || -e "$archive" ]]; then echo 'Preview output already exists; use a separate directory or preserve it first.' >&2; exit 2; fi

export MACOSX_DEPLOYMENT_TARGET=12.0
swift test -c release
swift build -c release --arch "$architecture"
binary_dir="$(swift build -c release --arch "$architecture" --show-bin-path)"
mkdir -p "$output/NBA Live.app/Contents/MacOS" "$output/NBA Live.app/Contents/Resources/Teams" "$output/NBA Live.app/Contents/Frameworks"
app="$output/NBA Live.app"
cp "$binary_dir/NBALiveMac" "$app/Contents/MacOS/NBALiveMac"
cp Info.plist "$app/Contents/Info.plist"
cp Resources/AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
cp Resources/MenuBar.png "$app/Contents/Resources/MenuBar.png"
cp "$root"/src/NbaLive/Assets/Teams/*.png "$app/Contents/Resources/Teams/"
cp "$root/LICENSE" "$app/Contents/Resources/LICENSE"
cp "$root/THIRD-PARTY-NOTICES.md" "$app/Contents/Resources/THIRD-PARTY-NOTICES.md"

xcrun lipo -verify_arch "$architecture" "$app/Contents/MacOS/NBALiveMac"
xcrun vtool -show-build "$app/Contents/MacOS/NBALiveMac" | grep -E 'minos[[:space:]]+12\.0([[:space:]]|$)'
xcrun swift-stdlib-tool --copy --scan-executable "$app/Contents/MacOS/NBALiveMac" --platform macosx --destination "$app/Contents/Frameworks"
python3 audit_macho.py "$app" "$architecture"
if ! otool -l "$app/Contents/MacOS/NBALiveMac" | grep -Fq '@executable_path/../Frameworks'; then
    install_name_tool -add_rpath '@executable_path/../Frameworks' "$app/Contents/MacOS/NBALiveMac"
fi
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
ditto -c -k --sequesterRsrc --keepParent "$app" "$archive"
(cd "$(dirname "$archive")" && shasum -a 256 "$(basename "$archive")" > "$(basename "$archive").sha256")
echo "Created $archive"
