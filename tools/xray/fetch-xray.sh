#!/usr/bin/env bash
# Скачать ядро Xray-core закреплённой версии и проверить sha256 (закреплён ЗДЕСЬ, а не только из .dgst релиза).
# Версия — та же, что в Android-сборке (AndroidLibXrayLite на Xray-core v26.7.28), см. HANDOFF.md §5.
# Использование: tools/xray/fetch-xray.sh [linux-64|windows-64]...   (по умолчанию оба)
# Результат: third_party/xray/<платформа>/{xray|xray.exe, geoip.dat, geosite.dat, LICENSE} (в git не попадает).
set -euo pipefail
VERSION="v26.7.28"
declare -A SHA256=(
  [linux-64]="8195d909f1109b8f3d99eefe401a3c451d7bf4af71f24d3815420f77e5dd2a40"
  [windows-64]="c7172078fca4711bcd92a4774dcd1822544579c58816197575c47533317fd8d1"
)
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PLATFORMS=("$@"); [ ${#PLATFORMS[@]} -eq 0 ] && PLATFORMS=(linux-64 windows-64)
for P in "${PLATFORMS[@]}"; do
  EXP="${SHA256[$P]:?неизвестная платформа $P}"
  OUT="$ROOT/third_party/xray/$P"
  if [ -f "$OUT/.version" ] && [ "$(cat "$OUT/.version")" = "$VERSION $EXP" ]; then echo "xray $P $VERSION — уже есть"; continue; fi
  TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
  curl -4 -fsSL -o "$TMP/x.zip" "https://github.com/XTLS/Xray-core/releases/download/$VERSION/Xray-$P.zip"
  GOT="$(sha256sum "$TMP/x.zip" | cut -d' ' -f1)"
  [ "$GOT" = "$EXP" ] || { echo "sha256 НЕ совпал для $P: $GOT (ожидался $EXP)" >&2; exit 1; }
  rm -rf "$OUT"; mkdir -p "$OUT"
  python3 -c "import sys,zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$TMP/x.zip" "$OUT"
  chmod +x "$OUT/xray" 2>/dev/null || true
  echo "$VERSION $EXP" > "$OUT/.version"
  echo "xray $P $VERSION → $OUT"
done
