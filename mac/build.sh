#!/bin/bash
# Builds mac/build/IPWidget.app (universal arm64 + x86_64, macOS 13+), ad-hoc signed.
set -euo pipefail
cd "$(dirname "$0")"

if ! xcrun --find swiftc >/dev/null 2>&1; then
    echo "swiftc not found. Install the command-line tools: xcode-select --install" >&2
    exit 1
fi

APP=build/IPWidget.app
rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" build/obj

for arch in arm64 x86_64; do
    xcrun swiftc -O -whole-module-optimization \
        -target "$arch-apple-macos13.0" \
        -sdk "$(xcrun --show-sdk-path --sdk macosx)" \
        -framework AppKit -framework SystemConfiguration -framework CoreWLAN -framework ServiceManagement \
        Sources/*.swift -o "build/obj/IPWidget-$arch"
done
lipo -create build/obj/IPWidget-arm64 build/obj/IPWidget-x86_64 -output "$APP/Contents/MacOS/IPWidget"
cp Info.plist "$APP/Contents/Info.plist"
rm -rf build/obj

codesign --force --sign - --timestamp=none "$APP"
echo "Built $(pwd)/$APP"
