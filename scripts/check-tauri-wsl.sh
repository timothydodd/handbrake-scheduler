#!/usr/bin/env bash
# Type-check the src-tauri crate from WSL without GTK/webkit dev packages.
# `cargo check` never links, so the -sys build scripts only need pkg-config to
# *find* each library; we feed them empty stub .pc files from a temp dir.
# This catches Rust errors in src-tauri; it does not replace a Windows build.
set -euo pipefail
cd "$(dirname "$0")/.."
PC="${TMPDIR:-/tmp}/hbs-fakepc"
mkdir -p "$PC"
for m in glib-2.0 gobject-2.0 gio-2.0 gdk-3.0 gtk+-3.0 atk cairo cairo-gobject pango gdk-pixbuf-2.0 \
         libsoup-3.0 webkit2gtk-4.1 javascriptcoregtk-4.1 dbus-1 gdk-x11-3.0 \
         ayatana-appindicator3-0.1 appindicator3-0.1 xdo; do
  printf "Name: %s\nDescription: stub\nVersion: 99.0\nLibs:\nCflags:\n" "$m" > "$PC/$m.pc"
done
export PKG_CONFIG_PATH="$PC"
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$HOME/.cargo-target/handbrake-scheduler}"
cargo check -p handbrake-scheduler "$@"
