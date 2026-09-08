package com.torjet.app

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.HttpURLConnection
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.Socket
import java.net.URL

/**
 * Lightweight keep-alive + speed test over the live HTTP CONNECT tunnel,
 * mirroring the Windows core's keep-alive (generate_204 pings) and speed test
 * (10 MB download through the HTTP proxy).
 */
object KeepAliveLoop {

    private val httpPort = TorController.DEFAULT_HTTP
    private val socksPort = TorController.DEFAULT_SOCKS

    suspend fun run(
        appContext: Context,
        controller: TorController,
        notifyUpdate: (String, String) -> Unit
    ) = withContext(Dispatchers.IO) {
        // wait for the controller to actually spawn tor before looping
        while (!controller.isRunning) {
            Thread.sleep(200)
        }
        while (controller.isRunning) {
            try {
                val ok = ping()
                if (ok) notifyUpdate("TorJet", "Connected - tunnel healthy")
            } catch (e: Exception) {
                notifyUpdate("TorJet", "Keep-alive failed, checking...")
            }
            Thread.sleep(5000)
        }
    }

    private fun ping(): Boolean {
        return runCatching {
            val proxy = Proxy(Proxy.Type.HTTP, InetSocketAddress("127.0.0.1", httpPort))
            val c2 = URL("http://www.example.com/").openConnection(proxy) as HttpURLConnection
            c2.connectTimeout = 5000
            c2.readTimeout = 5000
            val code = c2.responseCode
            code in 200..299
        }.getOrDefault(false)
    }

    /** Mirrors FormatSpeed from the C# core. */
    fun formatSpeedBytesPerSec(bytesPerSec: Double): String {
        return when {
            bytesPerSec >= 1024 * 1024 -> "%.2f MB/s".format(bytesPerSec / (1024 * 1024))
            bytesPerSec >= 1024 -> "%.1f KB/s".format(bytesPerSec / 1024)
            else -> "%.0f B/s".format(bytesPerSec)
        }
    }

    /** Simple single-stream download speed test through the SOCKS proxy. */
    suspend fun speedTest(): String = withContext(Dispatchers.IO) {
        try {
            val url = URL("https://speed.cloudflare.com/__down?bytes=10000000")
            val proxy = Proxy(Proxy.Type.SOCKS, InetSocketAddress("127.0.0.1", socksPort))
            val conn = url.openConnection(proxy) as HttpURLConnection
            conn.connectTimeout = 60000
            conn.readTimeout = 60000
            val start = System.currentTimeMillis()
            var total = 0L
            conn.inputStream.use { ins ->
                val buf = ByteArray(64 * 1024)
                var n = ins.read(buf)
                while (n > 0) {
                    total += n
                    n = ins.read(buf)
                }
            }
            val secs = (System.currentTimeMillis() - start) / 1000.0
            if (secs <= 0) "0 B/s"
            else formatSpeedBytesPerSec(total / secs)
        } catch (e: Exception) {
            "speed test failed"
        }
    }
}
