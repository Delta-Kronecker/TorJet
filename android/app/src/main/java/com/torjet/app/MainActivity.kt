package com.torjet.app

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.net.VpnService
import android.os.Bundle
import android.os.IBinder
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

class MainActivity : AppCompatActivity() {

    private var service: TorService? = null
    private var bound = false
    private var observing: Job? = null
    private lateinit var ring: PowerRingView
    private lateinit var stateText: TextView
    private lateinit var socksLine: TextView
    private lateinit var bootstrapLine: TextView
    private lateinit var proxySwitch: Switch

    private val vpnPermission =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            if (result.resultCode == RESULT_OK) startVpn()
            else proxySwitch.isChecked = false
        }

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName?, binder: IBinder?) {
            service = (binder as TorService.LocalBinder).getService()
            bound = true
            observeController()
        }

        override fun onServiceDisconnected(name: ComponentName?) {
            bound = false
            service = null
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        bindService(
            Intent(this, TorService::class.java),
            connection, Context.BIND_AUTO_CREATE
        )
        ring = findViewById(R.id.powerRing)
        stateText = findViewById(R.id.stateText)
        socksLine = findViewById(R.id.socksLine)
        bootstrapLine = findViewById(R.id.bootstrapLine)
        proxySwitch = findViewById(R.id.proxySwitch)

        ring.setOnClickListener {
            val svc = service ?: return@setOnClickListener
            val current = svc.controller.ui.value.state
            if (current == TorController.State.IDLE) beginSession()
            else if (current == TorController.State.CONNECTING || current == TorController.State.CONNECTED) svc.stopSession()
        }

        findViewById<android.view.View>(R.id.settingsRow).setOnClickListener {
            startActivity(Intent(this, SettingsActivity::class.java))
        }
        findViewById<android.view.View>(R.id.proxyRow).setOnClickListener {
            proxySwitch.isChecked = !proxySwitch.isChecked
        }
        proxySwitch.setOnCheckedChangeListener { _, checked -> onProxyToggled(checked) }
    }

    private fun beginSession() {
        val svc = service ?: return
        val settings = svc.controller.settings
        svc.beginSession(settings.mode, settings.strategy)
    }

    private fun onProxyToggled(checked: Boolean) {
        val svc = service ?: return
        if (checked) {
            val intent = VpnService.prepare(this)
            if (intent != null) vpnPermission.launch(intent)
            else startVpn()
        } else {
            stopService(Intent(this, VpnProxyService::class.java))
        }
    }

    private fun startVpn() {
        startService(Intent(this, VpnProxyService::class.java).setAction("start"))
        Toast.makeText(this, "Proxy on - all traffic via Tor", Toast.LENGTH_SHORT).show()
    }

    private fun observeController() {
        observing?.cancel()
        observing = lifecycleScope.launch {
            service?.controller?.ui?.collect { ui -> render(ui) }
        }
    }

    private fun render(ui: TorController.UiState) {
        val ringState = when (ui.state) {
            TorController.State.IDLE -> PowerRingView.RingState.IDLE
            TorController.State.CONNECTING -> PowerRingView.RingState.CONNECTING
            TorController.State.CONNECTED -> PowerRingView.RingState.CONNECTED
            TorController.State.RESTARTING -> PowerRingView.RingState.RESTARTING
            TorController.State.STOPPING -> PowerRingView.RingState.STOPPING
            TorController.State.ERROR -> PowerRingView.RingState.IDLE
        }
        ring.ringState = ringState
        ring.progress = ui.bootPct

        val state = when (ui.state) {
            TorController.State.CONNECTED -> getString(R.string.title_connected)
            TorController.State.CONNECTING -> getString(R.string.title_connecting)
            TorController.State.RESTARTING -> getString(R.string.title_restarting)
            TorController.State.STOPPING -> getString(R.string.title_stopping)
            else -> getString(R.string.title_idle)
        }
        stateText.text = state
        stateText.setTextColor(
            when (ui.state) {
                TorController.State.CONNECTED -> getColor(R.color.green)
                TorController.State.CONNECTING, TorController.State.STOPPING -> getColor(R.color.amber)
                TorController.State.RESTARTING -> getColor(R.color.red)
                else -> getColor(R.color.muted)
            }
        )
        if (ui.state == TorController.State.CONNECTED || ui.state == TorController.State.CONNECTING) {
            socksLine.visibility = android.view.View.VISIBLE
            socksLine.text = "SOCKS 127.0.0.1:${ui.socksPort} | HTTP 127.0.0.1:${ui.httpPort}"
        }
        bootProgress(ui)
    }

    private fun bootProgress(ui: TorController.UiState) {
        val line = if (ui.state == TorController.State.CONNECTING || ui.state == TorController.State.CONNECTED) {
            val tag = if (ui.bootTag.isNotEmpty()) " (${ui.bootTag})" else ""
            if (ui.bootPct >= 100) "connected$tag"
            else "Bootstrapped ${ui.bootPct}%$tag"
        } else {
            ""
        }
        bootstrapLine.text = line
    }

    override fun onDestroy() {
        observing?.cancel()
        if (bound) unbindService(connection)
        super.onDestroy()
    }
}
