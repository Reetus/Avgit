#!/bin/sh
# Builds a Velopack release of Avgit for Linux: a self-updating .AppImage plus
# the release feed (releases.<channel>.json, full/delta .nupkg) for publishing.
#
#   branding/linux/build-appimage.sh [--version X.Y.Z] [--rid RID] [--output DIR]
#
# --version  defaults to <Version> in Avgit.App/Avgit.App.csproj
# --rid      defaults to this machine's architecture (linux-x64 or linux-arm64)
# --output   defaults to artifacts/releases/<rid>; keep earlier releases there
#            so vpk can build delta packages against them
#
# The app is published with the NativeAot profile (needs clang and zlib1g-dev),
# laid out as an AppDir with Avgit's own desktop entry and icon set, then packed
# with the vpk version pinned in dotnet-tools.json.

set -eu

script_dir=$(cd "$(dirname "$0")" && pwd)
repo_root=$(cd "$script_dir/../.." && pwd)
version=
rid=
output_dir=

while [ $# -gt 0 ]; do
    case "$1" in
        --version|--rid|--output)
            [ $# -ge 2 ] || { echo "$1 needs a value" >&2; exit 2; }
            case "$1" in
                --version) version=$2 ;;
                --rid) rid=$2 ;;
                --output) output_dir=$2 ;;
            esac
            shift ;;
        -h|--help)
            sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ -z "$rid" ]; then
    case "$(uname -m)" in
        x86_64|amd64) rid=linux-x64 ;;
        aarch64|arm64) rid=linux-arm64 ;;
        *) echo "Unsupported architecture $(uname -m); pass --rid." >&2; exit 1 ;;
    esac
fi

cd "$repo_root"

if [ -z "$version" ]; then
    version=$(dotnet msbuild Avgit.App/Avgit.App.csproj -getProperty:Version)
fi
output_dir=${output_dir:-"$repo_root/artifacts/releases/$rid"}

echo "Publishing Avgit $version for $rid"
dotnet publish Avgit.App -c Release -r "$rid" -p:PublishProfile=NativeAot -p:Version="$version"
publish_dir="$repo_root/artifacts/publish/aot/$rid"

# vpk uses a pack directory ending in .AppDir as-is, so the desktop entry and
# icons are ours rather than its generated minimal ones.
app_dir="$repo_root/artifacts/appimage/$rid/Avgit.AppDir"
rm -rf "$app_dir"
mkdir -p "$app_dir/usr/bin" "$app_dir/usr/share/applications"
# The NativeAOT binary loads libSkiaSharp/libHarfBuzzSharp from its own directory.
cp -R "$publish_dir"/. "$app_dir/usr/bin"/
chmod 755 "$app_dir/usr/bin/Avgit"

for size_dir in "$script_dir"/hicolor/*/; do
    size=$(basename "$size_dir")
    mkdir -p "$app_dir/usr/share/icons/hicolor/$size/apps"
    cp "$size_dir/apps/avgit.png" "$app_dir/usr/share/icons/hicolor/$size/apps/avgit.png"
done
# AppImage spec: the Icon= image and .DirIcon sit in the AppDir root.
cp "$script_dir/hicolor/256x256/apps/avgit.png" "$app_dir/avgit.png"
cp "$script_dir/hicolor/256x256/apps/avgit.png" "$app_dir/.DirIcon"

# Desktop integration tools (AppImageLauncher, Gear Lever, appimaged) rewrite
# Exec to the AppImage's own path when they install the entry.
sed -e 's/^Exec=.*/Exec=Avgit %F/' "$script_dir/avgit.desktop" > "$app_dir/avgit.desktop"
printf 'X-AppImage-Version=%s\n' "$version" >> "$app_dir/avgit.desktop"
cp "$app_dir/avgit.desktop" "$app_dir/usr/share/applications/avgit.desktop"

cat > "$app_dir/AppRun" <<'EOF'
#!/bin/sh
HERE=$(dirname "$(readlink -f "$0")")
exec "$HERE/usr/bin/Avgit" "$@"
EOF
chmod 755 "$app_dir/AppRun"

dotnet tool restore
dotnet vpk pack \
    --packId Avgit \
    --packVersion "$version" \
    --packTitle Avgit \
    --packDir "$app_dir" \
    --mainExe Avgit \
    --runtime "$rid" \
    --outputDir "$output_dir"

echo "Release written to $output_dir"
