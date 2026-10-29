package com.picosoft.xrayproxydroid

import com.picosoft.xrayproxydroid.settings.AppSettings
import com.picosoft.xrayproxydroid.settings.Blocklist
import com.picosoft.xrayproxydroid.settings.BlocklistStore
import com.picosoft.xrayproxydroid.settings.BlockedServer
import com.picosoft.xrayproxydroid.settings.PausedServer
import com.picosoft.xrayproxydroid.settings.ServerRename
import com.picosoft.xrayproxydroid.traffic.DayBucket
import com.picosoft.xrayproxydroid.traffic.TrafficFile
import com.picosoft.xrayproxydroid.xray.link.Protocol
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Test
import java.io.File

/**
 * X-Gates Win golden (см. GoldenDumpTest): (1) JSON сторов в формате Android — C# обязан их читать (данные
 * совместимы между платформами).
 */
class GoldenStoresTest {
    private val out = File(System.getenv("GOLDEN_OUT") ?: error("GOLDEN_OUT")).apply { mkdirs() }
    private val pretty = Json { prettyPrint = true; encodeDefaults = true }

    @Test fun dumpStores() {
        val d = File(out, "stores").apply { mkdirs() }
        File(d, "settings-default.json").writeText(pretty.encodeToString(AppSettings()))
        File(d, "settings-custom.json").writeText(pretty.encodeToString(AppSettings(
            speedWarmupSec = 1.5, pingPool = 3, allowedProtocols = setOf(Protocol.TROJAN, Protocol.VLESS),
            economyEnabled = true, directProbeUrl = "https://example.com/x?y=1&z=«»",
        )))
        File(d, "blocklist.json").writeText(pretty.encodeToString(Blocklist(
            words = BlocklistStore.DEFAULT_WORDS,
            servers = listOf(BlockedServer("k1", "Имя 1", 1700000000000L)),
            renames = listOf(ServerRename("k2", "Моё", "Orig", 5L)),
            paused = listOf(PausedServer("k3", "P", 7L)),
        )))
        File(d, "traffic.json").writeText(Json { encodeDefaults = true }.encodeToString(TrafficFile(
            days = mapOf("2026-10-28" to DayBucket(1, 2, 3), "2026-10-29" to DayBucket(10, 20, 30)),
            todayDate = "2026-10-29", today = DayBucket(4, 5, 6),
        )))
    }
}
