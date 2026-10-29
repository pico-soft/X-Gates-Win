# Golden-эталон: паритет C# ↔ Kotlin (Android)

Зачем: C#-ядро X-Gates Win — порт Kotlin-кода Android 1:1. Чтобы «1:1» было фактом, а не намерением, один и тот же
корпус входов прогоняется через **настоящий** Kotlin-код и через C#, результаты обязаны совпасть (см.
`docs/decisions.md`, раздел «принципы порта»).

- `make-corpus.py` → `corpus/` — синтетические ссылки и тела подписок под ветки кода и полевые случаи.
- `run-golden.sh [корпус] [выход]` — временная копия Android-проекта + `kotlin/GoldenDumpTest.kt`; реальные
  `org.json`/`android.util.Base64` из AOSP (`aosp/`, заглушки скрытых аннотаций — `stubs/`) собираются отдельным jar
  (в JVM-тестах android.jar отдаёт заглушки). По умолчанию пишет в `tests/XGates.Core.Tests/Golden/`.
- C#: `tests/XGates.Core.Tests/GoldenParityTests.cs` (`dotnet test`).

Когда перегенерировать: после переноса фикса из Android в парсеры/конфиги/декодер — сначала `run-golden.sh` на
новом Android-коде, затем C# должен снова стать зелёным.

Разовая сверка на реальных подписках (в репо не класть — там живые пароли серверов):
```bash
tools/golden/run-golden.sh /path/real-corpus /path/real-corpus/golden
XGATES_GOLDEN_DIR=/path/real-corpus/golden XGATES_CORPUS_DIR=/path/real-corpus dotnet test tests/XGates.Core.Tests
```
