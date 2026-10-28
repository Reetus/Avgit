#!/bin/sh
# Installs (or removes) Avgit for the current user on Linux: the published app,
# an `avgit` command, the menu/launcher entry and the icons.
#
#   branding/linux/install.sh [--prefix DIR] [PUBLISH_DIR | Avgit.AppImage]
#   branding/linux/install.sh --uninstall [--prefix DIR]
#
# PUBLISH_DIR defaults to the NativeAOT (then single-file) publish output for
# this machine's architecture, e.g. artifacts/publish/aot/linux-x64. Publish
# first with:  dotnet publish Avgit.App -r linux-x64 -p:PublishProfile=NativeAot
# An .AppImage from build-appimage.sh can be installed instead; it stays
# self-updating because PREFIX/lib/avgit is writable by the user.
#
# Files go under PREFIX (default ~/.local, no root needed):
#   PREFIX/lib/avgit/                        the app (publish folder or Avgit.AppImage)
#   PREFIX/bin/avgit                         symlink to the binary
#   PREFIX/share/applications/avgit.desktop  launcher entry
#   PREFIX/share/icons/hicolor/NxN/apps/avgit.png

set -eu

script_dir=$(cd "$(dirname "$0")" && pwd)
repo_root=$(cd "$script_dir/../.." && pwd)
prefix="${HOME}/.local"
mode=install
publish_dir=

while [ $# -gt 0 ]; do
    case "$1" in
        --uninstall) mode=uninstall ;;
        --prefix)
            [ $# -ge 2 ] || { echo "--prefix needs a directory" >&2; exit 2; }
            prefix=$2
            shift ;;
        -h|--help)
            sed -n '2,19p' "$0" | sed 's/^# \{0,1\}//'
            exit 0 ;;
        -*) echo "Unknown option: $1" >&2; exit 2 ;;
        *) publish_dir=$1 ;;
    esac
    shift
done

case "$prefix" in
    /*) ;;
    *) prefix="$(pwd)/$prefix" ;;
esac

app_dir="$prefix/lib/avgit"
bin_link="$prefix/bin/avgit"
desktop_file="$prefix/share/applications/avgit.desktop"
icon_root="$prefix/share/icons/hicolor"

refresh_caches() {
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database -q "$prefix/share/applications" 2>/dev/null || true
    fi
    if command -v gtk-update-icon-cache >/dev/null 2>&1 && [ -d "$icon_root" ]; then
        # -t: a user icon directory normally has no index.theme of its own.
        gtk-update-icon-cache -q -t -f "$icon_root" 2>/dev/null || true
    fi
}

if [ "$mode" = uninstall ]; then
    # Only remove what this script installs.
    if [ -L "$bin_link" ]; then
        case "$(readlink "$bin_link")" in
            "$app_dir/Avgit"|"$app_dir/Avgit.AppImage") rm -f "$bin_link" ;;
        esac
    fi
    rm -f "$desktop_file"
    for icon in "$icon_root"/*/apps/avgit.png; do
        [ -e "$icon" ] && rm -f "$icon"
    done
    if [ -f "$app_dir/Avgit" ] || [ -f "$app_dir/Avgit.AppImage" ]; then
        rm -rf "$app_dir"
    fi
    refresh_caches
    echo "Removed Avgit from $prefix"
    exit 0
fi

if [ -z "$publish_dir" ]; then
    case "$(uname -m)" in
        x86_64|amd64) rid=linux-x64 ;;
        aarch64|arm64) rid=linux-arm64 ;;
        *) echo "Unsupported architecture $(uname -m); pass PUBLISH_DIR explicitly." >&2; exit 1 ;;
    esac
    for candidate in "$repo_root/artifacts/publish/aot/$rid" "$repo_root/artifacts/publish/singlefile/$rid"; do
        if [ -x "$candidate/Avgit" ]; then
            publish_dir=$candidate
            break
        fi
    done
    if [ -z "$publish_dir" ]; then
        echo "No published build found for $rid. Publish first:" >&2
        echo "  dotnet publish Avgit.App -r $rid -p:PublishProfile=NativeAot" >&2
        exit 1
    fi
fi

if [ -f "$publish_dir" ]; then
    case "$publish_dir" in
        *.AppImage) ;;
        *) echo "$publish_dir is not a publish folder or an .AppImage." >&2; exit 1 ;;
    esac
    # Velopack updates replace the AppImage in place, under this fixed name.
    app_exe="$app_dir/Avgit.AppImage"
elif [ -x "$publish_dir/Avgit" ]; then
    app_exe="$app_dir/Avgit"
else
    echo "$publish_dir does not contain an executable Avgit binary." >&2
    exit 1
fi

rm -rf "$app_dir"
mkdir -p "$app_dir"
if [ -f "$publish_dir" ]; then
    cp "$publish_dir" "$app_exe"
else
    # The NativeAOT binary loads libSkiaSharp/libHarfBuzzSharp from its own
    # directory, so the whole publish folder is installed together.
    cp -R "$publish_dir"/. "$app_dir"/
fi
chmod 755 "$app_exe"

mkdir -p "$prefix/bin"
ln -sfn "$app_exe" "$bin_link"

for size_dir in "$script_dir"/hicolor/*/; do
    size=$(basename "$size_dir")
    mkdir -p "$icon_root/$size/apps"
    cp "$size_dir/apps/avgit.png" "$icon_root/$size/apps/avgit.png"
done

# Desktop sessions often do not have ~/.local/bin on PATH, so the launcher
# runs the installed binary by absolute path (quoted per the Desktop Entry spec).
mkdir -p "$prefix/share/applications"
# Inside a quoted argument \ " ` $ need a backslash, and the value's own string
# escaping doubles every backslash, so each becomes two backslashes plus the char.
exec_path=$(printf '%s' "$app_exe" | sed 's/[\\"`$]/\\\\&/g')
while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in
        Exec=*) printf 'Exec="%s" %%F\n' "$exec_path" ;;
        *) printf '%s\n' "$line" ;;
    esac
done < "$script_dir/avgit.desktop" > "$desktop_file"
chmod 644 "$desktop_file"

refresh_caches

echo "Installed Avgit to $app_dir"
echo "  command:  $bin_link"
echo "  launcher: $desktop_file"
case ":$PATH:" in
    *":$prefix/bin:"*) ;;
    *) echo "Note: $prefix/bin is not on PATH; the app menu entry works regardless." ;;
esac
