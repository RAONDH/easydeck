#!/bin/zsh
# EasyDeck 빌드: ./build.sh  →  build/EasyDeck.app
# 설치까지: ./build.sh install  →  /Applications/EasyDeck.app 로 복사 후 실행
set -e
cd "$(dirname "$0")"

APP=build/EasyDeck.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

swiftc -O -swift-version 5 -target arm64-apple-macos13.0 \
  -o "$APP/Contents/MacOS/EasyDeck-arm64" Sources/*.swift
if swiftc -O -swift-version 5 -target x86_64-apple-macos13.0 \
     -o "$APP/Contents/MacOS/EasyDeck-x86_64" Sources/*.swift 2>/dev/null; then
  lipo -create -output "$APP/Contents/MacOS/EasyDeck" \
    "$APP/Contents/MacOS/EasyDeck-arm64" "$APP/Contents/MacOS/EasyDeck-x86_64"
  rm "$APP/Contents/MacOS/EasyDeck-arm64" "$APP/Contents/MacOS/EasyDeck-x86_64"
else
  mv "$APP/Contents/MacOS/EasyDeck-arm64" "$APP/Contents/MacOS/EasyDeck"
fi

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>EasyDeck</string>
  <key>CFBundleDisplayName</key><string>EasyDeck</string>
  <key>CFBundleIdentifier</key><string>org.easypastor.easydeck</string>
  <key>CFBundleExecutable</key><string>EasyDeck</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppleEventsUsageDescription</key><string>음량·다크 모드 조절과 애플스크립트 동작을 실행하기 위해 필요합니다.</string>
</dict>
</plist>
PLIST

# 앱 아이콘 생성 (SF 심볼 기반)
if [ -f Tools/make_icon.swift ]; then
  ICONSET=build/AppIcon.iconset
  rm -rf "$ICONSET"; mkdir -p "$ICONSET"
  swift Tools/make_icon.swift "$ICONSET" >/dev/null 2>&1 && \
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns" || true
fi

codesign --force --deep --sign - "$APP"
echo "✅ 빌드 완료: $APP"

if [ "$1" = "install" ]; then
  pkill -x EasyDeck 2>/dev/null || true
  rm -rf /Applications/EasyDeck.app
  cp -R "$APP" /Applications/
  open /Applications/EasyDeck.app
  echo "✅ /Applications/EasyDeck.app 설치 및 실행"
fi
