package com.picosoft.xrayproxydroid

import android.content.Context
import android.content.ContextWrapper
import com.picosoft.xrayproxydroid.subscription.SubscriptionManager
import com.picosoft.xrayproxydroid.subscription.SubscriptionStore
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Test
import java.io.File

/**
 * X-Gates Win golden (см. GoldenDumpTest): сценарий SubscriptionManager на настоящем Kotlin-коде с файловым Context
 * (ContextWrapper с getFilesDir во временную папку) — после каждого шага sources.json; C# повторяет сценарий.
 */
class GoldenSubscriptionScenarioTest {
    private val out = File(System.getenv("GOLDEN_OUT") ?: error("GOLDEN_OUT")).apply { mkdirs() }
    private val corpus = File(System.getenv("GOLDEN_CORPUS") ?: error("GOLDEN_CORPUS"))

    private class FileContext(private val dir: File) : ContextWrapper(null) {
        override fun getFilesDir(): File = dir
        override fun getApplicationContext(): Context = this
    }

    @Test fun subscriptionScenario() {
        val dir = createTempDir()
        try {
            val ctx = FileContext(dir)
            val steps = File(out, "subscription-scenario.jsonl").bufferedWriter(Charsets.UTF_8)
            fun body(name: String) = File(corpus, "bodies/$name").readText(Charsets.UTF_8)
            fun snap(step: String) {
                val txt = File(dir, "sources.json").takeIf { it.exists() }?.readText() ?: "null"
                steps.write(Json.encodeToString(mapOf("step" to step, "sources" to txt))); steps.newLine()
            }
            SubscriptionManager.init(ctx); snap("init")
            val (a, ra) = SubscriptionManager.addLocalFromBody(ctx, body("plain.txt"), "A"); snap("addA ${ra.ok} ${ra.added} ${ra.invalid}")
            val (b, rb) = SubscriptionManager.addLocalFromBody(ctx, body("b64-urlsafe-nopad.txt"), "B"); snap("addB ${rb.ok} ${rb.added}")
            val (c, rc) = SubscriptionManager.addLocalFromBody(ctx, body("json-array.json"), "C"); snap("addC ${rc.ok} ${rc.added}")
            val (_, rd) = SubscriptionManager.addLocalFromBody(ctx, body("html-error.txt"), "D"); snap("addD ${rd.ok} ${rd.error}")
            val keys = SubscriptionStore.load(ctx).servers.map { SubscriptionManager.serverKey(it.profile) }
            SubscriptionManager.applyPingResults(ctx, mapOf(keys[0] to 120, keys[1] to -1)); snap("ping")
            SubscriptionManager.applySpeedResultsTagged(ctx, mapOf(keys[0] to (12.5 to true), keys[2] to (-1.0 to false))); snap("speed")
            SubscriptionManager.applySpeedResults(ctx, mapOf(keys[0] to -1.0, keys[3] to 3.0)); snap("speed2")
            val r2 = SubscriptionManager.importInto(ctx, a, body("plain-with-comments.txt"), "re"); snap("reimportA ${r2.ok} ${r2.added}")
            SubscriptionManager.setEnabled(ctx, b, false); snap("disableB")
            SubscriptionManager.rename(ctx, c, "  Новое имя  "); snap("renameC")
            SubscriptionManager.remove(ctx, a); snap("removeA")
            SubscriptionManager.setEnabled(ctx, b, true); snap("enableB")
            steps.write(Json.encodeToString(mapOf("step" to "allServers", "sources" to SubscriptionManager.allServers(ctx).joinToString("\n") { SubscriptionManager.serverKey(it) }))); steps.newLine()
            steps.close()
        } finally { dir.deleteRecursively() }
    }
}
