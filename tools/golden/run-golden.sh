#!/usr/bin/env bash
# Снять эталон (golden) с Kotlin-кода Android для C#-тестов X-Gates Win.
# Временная копия проекта (репозиторий не трогаем) + реальные AOSP org.json/Base64 вместо заглушек android.jar.
# Использование: tools/golden/run-golden.sh [каталог-корпуса] [каталог-выхода]
#   по умолчанию корпус tools/golden/corpus, выход tests/XGates.Core.Tests/Golden.
#   GOLDEN_SRC=<каталог Android-проекта> — брать Kotlin-код оттуда (напр. копия Android с патчами
#   docs/android-handoff/patches, ещё не принятыми в Android); по умолчанию — Kotlin-код самого форка.
#   GOLDEN_SOURCE_LABEL — что записать в SOURCE-COMMIT вместо короткого sha источника.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SRC="$(realpath "${GOLDEN_SRC:-$ROOT}")"
CORPUS="$(realpath "${1:-$ROOT/tools/golden/corpus}")"
OUT="$(realpath -m "${2:-$ROOT/tests/XGates.Core.Tests/Golden}")"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/xgates-golden.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

export JAVA_HOME="${JAVA_HOME:-$HOME/.local/opt/jdk-21}" ANDROID_HOME="${ANDROID_HOME:-$HOME/.local/opt/android-sdk}"
export PATH="$JAVA_HOME/bin:$PATH"

# Копия только того, что нужно Gradle (без .git, сборок и секретов).
for f in settings.gradle.kts build.gradle.kts gradle.properties gradlew gradle; do cp -r "$SRC/$f" "$WORK/"; done
mkdir -p "$WORK/app"; cp -r "$SRC/app/build.gradle.kts" "$SRC/app/libs" "$SRC/app/src" "$WORK/app/"
T="$WORK/app/src/test/java"
cp "$ROOT/tools/golden/kotlin/"*.kt "$T/com/picosoft/xrayproxydroid/"
# Реальные org.json/Base64 из AOSP — отдельным jar: исходниками в test/ их перекрывает android.jar (он в bootclasspath
# компиляции), а как зависимость jar идёт в runtime-classpath РАНЬШЕ заглушечного android.jar.
mkdir -p "$WORK/aosp-classes"
javac -nowarn -encoding UTF-8 -d "$WORK/aosp-classes" $(find "$ROOT/tools/golden/aosp" "$ROOT/tools/golden/stubs" -name '*.java')
jar cf "$WORK/aosp-real.jar" -C "$WORK/aosp-classes" .
printf '\ndependencies { testImplementation(files("%s")) }\n' "$WORK/aosp-real.jar" >> "$WORK/app/build.gradle.kts"

rm -rf "$OUT.tmp"
( cd "$WORK" && GOLDEN_CORPUS="$CORPUS" GOLDEN_OUT="$OUT.tmp" ./gradlew -q --no-daemon \
    -Pkotlin.compiler.execution.strategy=in-process \
    :app:testDebugUnitTest --tests 'com.picosoft.xrayproxydroid.Golden*' )
mkdir -p "$OUT"
cp -r "$OUT.tmp"/. "$OUT/"; rm -rf "$OUT.tmp"
if [ -n "${GOLDEN_SOURCE_LABEL:-}" ]; then echo "$GOLDEN_SOURCE_LABEL" > "$OUT/SOURCE-COMMIT"
else git -C "$SRC" rev-parse --short HEAD > "$OUT/SOURCE-COMMIT" 2>/dev/null || true; fi
echo "golden → $OUT ($(ls "$OUT"/*.jsonl | wc -l) файлов)"
