#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
npm ci
npm run build
npx cap sync ios
node scripts/normalize-ios.js
version=$(node -p 'JSON.parse(require("fs").readFileSync("package.json", "utf8")).version')
xcodebuild -project ios/App/App.xcodeproj -scheme App -configuration Release -sdk iphoneos -derivedDataPath ios/App/DerivedData CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO build
mkdir -p ios/App/Unsigned/Payload
cp -R ios/App/DerivedData/Build/Products/Release-iphoneos/App.app ios/App/Unsigned/Payload/
cd ios/App/Unsigned
zip -qr "AlarmHub-$version-unsigned.ipa" Payload
shasum -a 256 "AlarmHub-$version-unsigned.ipa" > "AlarmHub-$version-unsigned.ipa.sha256"
printf 'Unsigned IPA: %s/AlarmHub-%s-unsigned.ipa\n' "$PWD" "$version"
