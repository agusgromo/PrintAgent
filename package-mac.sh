#!/bin/sh
set -eu

cd "$(dirname "$0")"
if [ "$(uname -s)" != Darwin ]; then echo "Run this script on macOS." >&2; exit 1; fi

for runtime in osx-arm64 osx-x64; do
    dotnet publish PrintAgent.csproj -c Release -r "$runtime"

    app="artifacts/$runtime/PrintAgent Setup.app"
    resources="$app/Contents/Resources"
    mkdir -p "artifacts/$runtime"
    if [ -d "$app" ]; then rm -rf "$app"; fi
    osacompile -o "$app" \
        -e 'on run' \
        -e 'set installer to (POSIX path of (path to me)) & "Contents/Resources/install-mac.sh"' \
        -e 'do shell script (quoted form of installer)' \
        -e 'end run'
    mkdir -p "$resources"
    cp "bin/Release/net10.0/$runtime/publish/PrintAgent" "$resources/PrintAgent"
    cp "bin/Release/net10.0/$runtime/publish/appsettings.json" "$resources/appsettings.json"
    cp install-mac.sh "$resources/install-mac.sh"
    chmod +x "$resources/install-mac.sh" "$resources/PrintAgent"

    codesign --force --sign - "$resources/PrintAgent"
    codesign --force --deep --sign - "$app"
    hdiutil create -volname "PrintAgent Setup" -srcfolder "$app" -ov "artifacts/PrintAgent-Setup-Mac-$runtime.dmg"
    echo "Created artifacts/PrintAgent-Setup-Mac-$runtime.dmg"
done
