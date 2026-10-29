package com.picosoft.xrayproxydroid

import com.picosoft.xrayproxydroid.subscription.SubscriptionDecoder
import com.picosoft.xrayproxydroid.xray.XrayConfigBuilder
import com.picosoft.xrayproxydroid.xray.link.JsonConfigParser
import com.picosoft.xrayproxydroid.xray.link.ParseResult
import com.picosoft.xrayproxydroid.xray.link.ServerLinkParser
import com.picosoft.xrayproxydroid.xray.link.ServerProfile
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.encodeToJsonElement
import kotlinx.serialization.json.put
import org.junit.Test
import java.io.File

/**
 * X-Gates Win: эталон (golden) для C#-порта — НЕ часть Android-проекта. Копируется во временную копию проекта
 * скриптом tools/golden/run-golden.sh и гоняет НАСТОЯЩИЙ Kotlin-код по корпусу tools/golden/corpus/.
 * android.util.Base64 и org.json в JVM-тестах — заглушки (returnDefaultValues), поэтому скрипт подкладывает в
 * test-исходники их реальные AOSP-реализации (tools/golden/aosp/).
 * Выход — JSON Lines в build/golden/; C#-тесты сравнивают с ними свой результат.
 */
class GoldenDumpTest {
    private val json = Json { encodeDefaults = true }
    private val corpus = File(System.getenv("GOLDEN_CORPUS") ?: error("GOLDEN_CORPUS"))
    private val out = File(System.getenv("GOLDEN_OUT") ?: error("GOLDEN_OUT")).apply { mkdirs() }

    private fun profileJson(p: ServerProfile): JsonElement = json.encodeToJsonElement(p)

    private fun attempt(block: () -> String): JsonElement =
        try { JsonPrimitive(block()) } catch (e: Throwable) { JsonPrimitive("ERROR: ${e.javaClass.simpleName}: ${e.message}") }

    @Test fun dump() {
        // Страховка: работают РЕАЛЬНЫЕ AOSP-классы, а не заглушки android.jar (те вернули бы null/0).
        check(android.util.Base64.decode("QUJD", 0)?.let { String(it) } == "ABC") { "android.util.Base64 — заглушка" }
        check(org.json.JSONObject("{\"a\":7}").optInt("a") == 7) { "org.json — заглушка" }
        // 1) Ссылки: разбор + боевой конфиг + конфиг замера
        val links = File(corpus, "links").listFiles()!!.filter { it.isFile }.sortedBy { it.name }
        for (f in links) {
            val lines = f.readLines(Charsets.UTF_8).filter { it.isNotEmpty() && !it.startsWith("##") }
            File(out, "links-${f.nameWithoutExtension}.jsonl").bufferedWriter(Charsets.UTF_8).use { w ->
                for (line in lines) {
                    val r = ServerLinkParser.parse(line)
                    val o = buildJsonObject {
                        put("in", line)
                        when (r) {
                            is ParseResult.Supported -> {
                                put("kind", "Supported")
                                put("profile", profileJson(r.profile))
                                put("config", attempt { XrayConfigBuilder.build(r.profile) })
                                put("speedConfig", attempt { XrayConfigBuilder.buildForSpeedTest(r.profile, 23456) })
                            }
                            is ParseResult.Unsupported -> { put("kind", "Unsupported"); put("scheme", r.scheme); put("raw", r.raw) }
                            is ParseResult.Invalid -> { put("kind", "Invalid"); put("reason", r.reason); put("raw", r.raw) }
                        }
                    }
                    w.write(o.toString()); w.newLine()
                }
            }
        }
        // 2) Тела подписок: SubscriptionDecoder.decode + JsonConfigParser.parse (каждый файл — одно тело)
        val bodies = File(corpus, "bodies").listFiles()!!.filter { it.isFile }.sortedBy { it.name }
        File(out, "bodies.jsonl").bufferedWriter(Charsets.UTF_8).use { w ->
            for (f in bodies) {
                val body = f.readText(Charsets.UTF_8)
                val o = buildJsonObject {
                    put("file", f.name)
                    put("decoded", JsonArray(SubscriptionDecoder.decode(body).map { JsonPrimitive(it) }))
                    put("jsonConfigs", JsonArray(JsonConfigParser.parse(body).map { profileJson(it) }))
                }
                w.write(o.toString()); w.newLine()
            }
        }
    }
}
