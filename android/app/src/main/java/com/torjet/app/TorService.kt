package com.torjet.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.Binder
import android.os.IBinder
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * Foreground service keeping the tor daemon (and its monitoring loops) alive.
 * The UI binds to it and drives the controller through the exposed accessor.
 */
class TorService : Service() {

    companion object {
        const val CHANNEL_ID = "torjet_service"
        const val NOTIF_ID = 1
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    lateinit var controller: TorController

    private val binder = LocalBinder()

    inner class LocalBinder : Binder() {
        fun getService(): TorService = this@TorService
    }

    override fun onCreate() {
        super.onCreate()
        controller = TorController(this)
        createChannel()
        startForeground(NOTIF_ID, buildNotification("TorJet idle", "Press the ring to connect"))
    }

    private fun createChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            "TorJet service",
            NotificationManager.IMPORTANCE_LOW
        )
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    fun updateNotification(title: String, text: String) {
        val nm = getSystemService(NotificationManager::class.java)
        nm.notify(NOTIF_ID, buildNotification(title, text))
    }

    private fun buildNotification(title: String, text: String): Notification {
        val pi = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle(title)
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_menu_compass)
            .setContentIntent(pi)
            .setOngoing(true)
            .build()
    }

    fun beginSession(mode: Int, strategy: Int) {
        scope.launch { controller.start(mode, strategy) }
        scope.launch { controller.pollBootstrapLoop() }
        scope.launch { KeepAliveLoop.run(this@TorService, controller) { m, t -> updateNotification(m, t) } }
    }

    fun stopSession() {
        scope.launch { controller.stop() }
        updateNotification("TorJet", "Tor stopped")
    }

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }
}
