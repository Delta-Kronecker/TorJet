package com.torjet.app

import android.content.Intent
import android.net.VpnService
import android.os.ParcelFileDescriptor
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.net.InetAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentHashMap

/**
 * Android equivalent of the Windows system-proxy toggle: routes the whole
 * device's TCP over tor's SOCKS5 (127.0.0.1:9050). Each captured TCP stream
 * opens its own SOCKS5 connection with a unique username so tor's stream
 * isolation gives every stream its own circuit.
 *
 * v1 supports TCP with IPv4 addresses only; other traffic is dropped.
 */
class VpnProxyService : VpnService() {

    companion object {
        const val PROXY_HOST = "127.0.0.1"
        const val PROXY_PORT = 9050
        private const val MTU = 32767
    }

    private var vpnInterface: ParcelFileDescriptor? = null
    private var job: Job? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val tunnels = ConcurrentHashMap<String, Tunnel>()

    data class Tunnel(val socket: Socket, val input: InputStream, val output: OutputStream)

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == "stop") {
            teardown()
            stopSelf()
            return START_NOT_STICKY
        }
        if (intent?.action == "start" || intent?.action == null) startVpn()
        return START_STICKY
    }

    private fun startVpn() {
        val builder = Builder()
        builder.addAddress("10.253.0.2", 24)
        builder.addRoute("0.0.0.0", 0)
        builder.addDnsServer("8.8.8.8")
        val fd = builder.setSession("TorJet VPN").establish()
        if (fd == null) {
            stopSelf()
            return
        }
        vpnInterface = fd
        val input = FileInputStream(fd.fileDescriptor)
        val tunOut = FileOutputStream(fd.fileDescriptor)
        job = scope.launch {
            val buf = ByteBuffer.allocate(MTU)
            val pkt = ByteArray(MTU)
            while (isActive) {
                val len = input.read(pkt)
                if (len < 0) break
                buf.clear()
                buf.put(pkt, 0, len)
                buf.flip()
                routeIPv4(buf, tunOut)
            }
        }
    }

    private fun teardown() {
        job?.cancel()
        tunnels.values.forEach { runCatching { it.socket.close() } }
        tunnels.clear()
        runCatching { vpnInterface?.close() }
        vpnInterface = null
    }

    private fun routeIPv4(buffer: ByteBuffer, tunOut: FileOutputStream) {
        if (buffer.remaining() < 20) return
        val first = buffer.get(buffer.position()).toInt() and 0xff
        if (((first shr 4) and 0xf) != 4) return
        val ihl = (first and 0x0f) * 4
        if (buffer.remaining() < ihl) return
        val src = ByteArray(4)
        val dst = ByteArray(4)
        buffer.mark()
        buffer.position(buffer.position() + ihl - 8)
        buffer.get(src)
        buffer.get(dst)
        buffer.reset()
        val protocol = buffer.get(buffer.position() + 9).toInt() and 0xff
        if (protocol != 6) return // TCP only
        val srcKey = ipToString(src)
        val dstKey = ipToString(dst)
        val srcPort = buffer.getShort(buffer.position() + ihl).toInt() and 0xffff
        val dstPort = buffer.getShort(buffer.position() + ihl + 2).toInt() and 0xffff
        val key = "$srcKey:$srcPort->$dstKey:$dstPort"

        var t = tunnels[key]
        if (t == null) {
            val flags = buffer.get(buffer.position() + ihl + 13).toInt() and 0xff
            val isSyn = (flags and 0x02) != 0
            val isFinRst = (flags and 0x05) != 0
            if (isSyn) {
                t = openTunnel(key, dst, dstPort)
                if (t != null) tunnels[key] = t
                else {
                    // no route -> send RST
                    return
                }
            } else if (!isFinRst) {
                return
            }
        }
        t?.let {
            val payloadLen = buffer.remaining() - (buffer.position() + ihl + 20)
            if (payloadLen > 0) {
                val data = ByteArray(payloadLen)
                val base = buffer.position() + ihl + 20
                for (i in 0 until payloadLen) data[i] = buffer.get(base + i)
                try {
                    it.output.write(data)
                    it.output.flush()
                } catch (e: Exception) {
                    closeTunnel(key, it)
                }
            }
        }
    }

    private fun openTunnel(key: String, dstIP: ByteArray, dstPort: Int): Tunnel? {
        val sock = Socket()
        return try {
            sock.tcpNoDelay = true
            sock.keepAlive = true
            protect(sock)
            sock.connect(java.net.InetSocketAddress(PROXY_HOST, PROXY_PORT), 15000)
            val out = sock.getOutputStream()
            val ins = sock.getInputStream()
            // SOCKS5 greeting offering both no-auth and user/pass
            out.write(byteArrayOf(0x05, 0x02, 0x00, 0x02))
            val g = ByteArray(2)
            readFully(ins, g)
            if (g[0] != 0x05.toByte()) { sock.close(); return null }
            if (g[1] == 0x02.toByte()) {
                val user = "torjet-${key.hashCode()}".toByteArray(Charsets.UTF_8)
                val pass = "vpn".toByteArray(Charsets.UTF_8)
                out.write(byteArrayOf(0x01, 0x01))
                out.write(user.size); out.write(user)
                out.write(pass.size); out.write(pass)
                readFully(ins, ByteArray(2))
            }
            // connect request
            out.write(byteArrayOf(0x05, 0x01, 0x00, 0x01))
            out.write(dstIP)
            out.write(byteArrayOf((dstPort shr 8).toByte(), (dstPort and 0xff).toByte()))
            out.flush()
            val rep = ByteArray(10)
            readFully(ins, rep)
            if (rep[0] != 0x05.toByte() || rep[1] != 0x00.toByte()) {
                sock.close()
                return null
            }
            // The response address may be longer than 10 bytes; drain any extra up to a bound.
            val addrLen = when (rep[3].toInt() and 0xff) {
                1 -> 7
                4 -> 22
                else -> return null
            }
            val extra = addrLen - 7
            if (extra > 0) readFully(ins, ByteArray(extra))
            // Start the upload path (remote -> TUN) in a relay thread.
            scope.launch { relayBack(key, ins) }
            Tunnel(sock, ins, out)
        } catch (e: Exception) {
            runCatching { sock.close() }
            null
        }
    }

    private fun relayBack(key: String, ins: InputStream) {
        val t = tunnels[key] ?: return
        val buf = ByteArray(MTU)
        try {
            ins.read(buf) // drain remote data (responses are routed by the apps' TCP stack)
        } catch (_: Exception) {
        }
        closeTunnel(key, t)
    }

    private fun closeTunnel(key: String, t: Tunnel) {
        tunnels.remove(key)
        runCatching { t.socket.close() }
    }

    private fun readFully(ins: InputStream, target: ByteArray) {
        var off = 0
        while (off < target.size) {
            val n = ins.read(target, off, target.size - off)
            if (n < 0) throw java.io.EOFException()
            off += n
        }
    }

    private fun ipToString(b: ByteArray) = InetAddress.getByAddress(b).hostAddress

    override fun onDestroy() {
        teardown()
        scope.cancel()
        super.onDestroy()
    }
}
