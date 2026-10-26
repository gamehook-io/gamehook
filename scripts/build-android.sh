#!/usr/bin/env bash
# Builds the Android APK (Gamehook engine + local Web API) inside a throwaway container; nothing is installed on the host.
# Output: ./artifacts/android/*.apk (sideload) and *.aab (Google Play)
# Requires rootless podman. Env: CONFIGURATION (Release|Debug),
#      KEYSTORE_PASSWORD (default "gamehook-dev"; the generated keystore is for sideloading only).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
command -v podman >/dev/null || { echo "Need podman installed." >&2; exit 1; }

out="$root/artifacts/android"
mkdir -p "$out"

# Named volumes keep the ~2 GB of workloads/SDK/NuGet (and the signing key) between runs.
# The source mount is read-only; the container builds from a copy.
podman run --rm \
  -v "$root:/src:ro" \
  -v "$out:/out" \
  -v gamehook-android-dotnet:/usr/share/dotnet/metadata \
  -v gamehook-android-workloads:/usr/share/dotnet/sdk-manifests \
  -v gamehook-android-packs:/usr/share/dotnet/packs \
  -v gamehook-android-sdk:/android-sdk \
  -v gamehook-android-nuget:/root/.nuget \
  -v gamehook-android-keys:/keys \
  -e USER=root -e LOGNAME=root \
  -e CONFIGURATION="${CONFIGURATION:-Release}" \
  -e KEYSTORE_PASSWORD="${KEYSTORE_PASSWORD:-gamehook-dev}" \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -euo pipefail -c '
    if ! command -v java >/dev/null; then
      apt-get update -qq
      apt-get install -y -qq --no-install-recommends openjdk-21-jdk-headless unzip >/dev/null
    fi
    export JAVA_HOME="$(dirname "$(dirname "$(readlink -f "$(command -v java)")")")"

    dotnet workload install android --skip-sign-check

    # Only the projects the Android app depends on (not the desktop UI or tests).
    mkdir -p /build/src
    cp /src/Directory.Build.props /build/
    for project in Gamehook.Android Gamehook.Domain Gamehook.Infrastructure Gamehook.Presentation Gamehook.RestApi; do
      mkdir -p "/build/src/$project"
      tar -C "/src/src/$project" --exclude=bin --exclude=obj -cf - . | tar -C "/build/src/$project" -xf -
    done
    cd /build/src/Gamehook.Android

    if [[ ! -f /keys/gamehook.keystore ]]; then
      keytool -genkeypair -keystore /keys/gamehook.keystore -alias gamehook -keyalg RSA \
        -keysize 2048 -validity 10000 -dname "CN=Gamehook" \
        -storepass "$KEYSTORE_PASSWORD" -keypass "$KEYSTORE_PASSWORD"
    fi

    sdk_args=(-p:AndroidSdkDirectory=/android-sdk -p:JavaSdkDirectory="$JAVA_HOME")
    dotnet build -f net10.0-android -t:InstallAndroidDependencies "${sdk_args[@]}" \
      -p:AcceptAndroidSDKLicenses=True

    # Same signing for both: the APK sideloads (GitHub releases); the AAB is what Google Play takes.
    signing_args=(-p:AndroidKeyStore=true -p:AndroidSigningKeyStore=/keys/gamehook.keystore
      -p:AndroidSigningKeyAlias=gamehook
      -p:AndroidSigningKeyPass=env:KEYSTORE_PASSWORD -p:AndroidSigningStorePass=env:KEYSTORE_PASSWORD)
    for format in apk aab; do
      dotnet publish -c "$CONFIGURATION" -f net10.0-android "${sdk_args[@]}" \
        -p:AndroidPackageFormat=$format "${signing_args[@]}"
      find bin -path "*/publish/*" -name "*-Signed.$format" -exec cp {} /out/ \;
    done
    ls /out/*.apk /out/*.aab >/dev/null || { echo "Build finished but produced no signed APK/AAB." >&2; exit 1; }
    # Rootless podman maps container root to your user, so the APKs are already yours.
  '

echo "Build output in $out:"
ls -1 "$out"/*.apk "$out"/*.aab
