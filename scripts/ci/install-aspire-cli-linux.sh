#!/usr/bin/env bash
set -euo pipefail

# Pinned official bundle for the patched AppHost SDK. This script targets Linux x64 CI.
test "$(uname -s)-$(uname -m)" = "Linux-x86_64"
aspire_install_dir="${1:?Provide an absolute installation directory}"
case "$aspire_install_dir" in /*) ;; *) exit 2 ;; esac
aspire_archive="$(mktemp)"
trap 'rm -f "$aspire_archive"' EXIT
curl --fail --location --retry 3 --output "$aspire_archive" \
  https://github.com/microsoft/aspire/releases/download/v13.6.5/aspire-cli-linux-x64-13.6.5.tar.gz
printf '%s  %s\n' \
  c1ebfe903e96e0ac8c60de6897eb754328789b65bc859ab484618c62fc65826f3af376ff8b32943419975f2b8e5a00da20e3d11631dc2e07571fabd0e8262de8 \
  "$aspire_archive" | sha512sum --check
mkdir -p "$aspire_install_dir"
tar -xzf "$aspire_archive" -C "$aspire_install_dir"
if test -n "${GITHUB_PATH:-}"; then printf '%s\n' "$aspire_install_dir" >> "$GITHUB_PATH"; fi
"$aspire_install_dir/aspire" --version
