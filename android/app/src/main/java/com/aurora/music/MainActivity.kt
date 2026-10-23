package com.aurora.music

import android.Manifest
import android.annotation.SuppressLint
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.view.View
import android.webkit.JavascriptInterface
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Toast
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import org.json.JSONObject

class MainActivity : AppCompatActivity() {

    private lateinit var webView: WebView
    private var filePathCallback: ValueCallback<Array<Uri>>? = null

    companion object {
        private const val FILE_CHOOSER_REQUEST_CODE = 1001
        private const val NOTIFICATION_REQUEST_CODE = 1002
        private const val AUDIO_PERMISSION_REQUEST_CODE = 1003
        private const val LOCAL_ASSET_URL = "file:///android_asset/index.html"
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Edge-to-edge transparent system bars
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.statusBarColor = android.graphics.Color.TRANSPARENT
        window.navigationBarColor = android.graphics.Color.TRANSPARENT

        webView = WebView(this).apply {
            setBackgroundColor(0xFF050509.toInt())
            overScrollMode = View.OVER_SCROLL_NEVER
        }
        setContentView(webView)

        configureWebView()

        webView.addJavascriptInterface(MediaBridge(), "AndroidMedia")
        PlaybackService.commandListener = { command ->
            webView.post {
                if (!isFinishing && !isDestroyed) {
                    webView.evaluateJavascript(
                        "window.auroraRemote && window.auroraRemote(" +
                            JSONObject.quote(command) + ")",
                        null
                    )
                }
            }
        }
        requestNotificationPermission()

        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (webView.canGoBack()) {
                    webView.goBack()
                } else {
                    isEnabled = false
                    onBackPressedDispatcher.onBackPressed()
                }
            }
        })

        loadPlayer()
    }

    @SuppressLint("SetJavaScriptEnabled")
    @Suppress("DEPRECATION")
    private fun configureWebView() {
        webView.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            databaseEnabled = true
            allowFileAccess = true
            allowContentAccess = true
            // The player is bundled as file:///android_asset/index.html and
            // talks to the PC server over plain HTTP, so file:// pages need
            // permission to issue cross-origin requests.
            allowFileAccessFromFileURLs = true
            allowUniversalAccessFromFileURLs = true
            mediaPlaybackRequiresUserGesture = false
            mixedContentMode = WebSettings.MIXED_CONTENT_ALWAYS_ALLOW
            useWideViewPort = true
            loadWithOverviewMode = true
            cacheMode = WebSettings.LOAD_DEFAULT
        }

        webView.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(
                view: WebView?,
                request: WebResourceRequest?
            ): WebResourceResponse? {
                val url = request?.url ?: return null
                if (!MediaLibrary.HOST.equals(url.host, ignoreCase = true)) return null
                val path = url.path ?: return null

                return try {
                    when {
                        path.startsWith("/audio/") -> {
                            val id = path.substringAfterLast('/').toLongOrNull() ?: return null
                            val headers = request.requestHeaders
                            val range = headers?.get("Range") ?: headers?.get("range")
                            MediaLibrary.openAudio(
                                this@MainActivity,
                                id,
                                range,
                                request.method == "HEAD"
                            )
                        }
                        path.startsWith("/art/") -> {
                            val albumId = path.substringAfterLast('/').toLongOrNull() ?: return null
                            MediaLibrary.openArt(this@MainActivity, albumId)
                        }
                        else -> null
                    }
                } catch (e: Exception) {
                    android.util.Log.w("AuroraMusic", "Cannot serve $url", e)
                    null
                }
            }

            override fun onReceivedError(
                view: WebView?,
                request: WebResourceRequest?,
                error: WebResourceError?
            ) {
                // Only a failed main-frame navigation should fall back to the
                // bundled page. Subresource failures (audio, /api, icons) must
                // not reload the whole UI, otherwise an offline server causes
                // a reload loop.
                if (request != null && request.isForMainFrame) {
                    webView.loadUrl(LOCAL_ASSET_URL)
                }
            }
        }

        webView.webChromeClient = object : WebChromeClient() {
            override fun onShowFileChooser(
                webView: WebView?,
                filePathCallback: ValueCallback<Array<Uri>>?,
                fileChooserParams: FileChooserParams?
            ): Boolean {
                this@MainActivity.filePathCallback?.onReceiveValue(null)
                this@MainActivity.filePathCallback = filePathCallback

                val intent = Intent(Intent.ACTION_GET_CONTENT).apply {
                    addCategory(Intent.CATEGORY_OPENABLE)
                    type = "audio/*"
                    putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true)
                }

                try {
                    startActivityForResult(
                        Intent.createChooser(intent, "Выберите аудиофайлы"),
                        FILE_CHOOSER_REQUEST_CODE
                    )
                } catch (e: Exception) {
                    this@MainActivity.filePathCallback = null
                    Toast.makeText(this@MainActivity, "Не удалось открыть файлы", Toast.LENGTH_SHORT).show()
                    return false
                }
                return true
            }
        }
    }

    private fun loadPlayer() {
        // Load bundled asset by default, ready to connect or play phone tracks
        webView.loadUrl(LOCAL_ASSET_URL)
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(
                this,
                Manifest.permission.POST_NOTIFICATIONS
            ) != PackageManager.PERMISSION_GRANTED
        ) {
            ActivityCompat.requestPermissions(
                this,
                arrayOf(Manifest.permission.POST_NOTIFICATIONS),
                NOTIFICATION_REQUEST_CODE
            )
        }
    }

    /**
     * Reports the WebView playback state to the foreground service so that
     * audio keeps running in the background and the media notification can
     * show the current track.
     */
    private inner class MediaBridge {
        @JavascriptInterface
        fun setNowPlaying(title: String, artist: String, playing: Boolean) {
            runOnUiThread {
                if (title.isEmpty() && !playing) {
                    PlaybackService.stop(this@MainActivity)
                } else {
                    PlaybackService.update(this@MainActivity, title, artist, playing)
                }
            }
        }

        /** True when the app may read the device audio library. */
        @JavascriptInterface
        fun hasAudioPermission(): Boolean = hasAudioPermissionInternal()

        /** Asks the user for the audio permission; the result arrives in window.onAuroraAudioPermission. */
        @JavascriptInterface
        fun requestAudioPermission() {
            runOnUiThread { requestAudioPermissionInternal() }
        }

        /** The whole MediaStore music library as JSON, or "[]" without permission. */
        @JavascriptInterface
        fun listAudio(): String {
            return if (hasAudioPermissionInternal()) {
                MediaLibrary.list(this@MainActivity)
            } else {
                "[]"
            }
        }
    }

    private fun audioPermissionName(): String =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            Manifest.permission.READ_MEDIA_AUDIO
        } else {
            Manifest.permission.READ_EXTERNAL_STORAGE
        }

    private fun hasAudioPermissionInternal(): Boolean =
        ContextCompat.checkSelfPermission(
            this,
            audioPermissionName()
        ) == PackageManager.PERMISSION_GRANTED

    private fun requestAudioPermissionInternal() {
        if (hasAudioPermissionInternal()) {
            notifyAudioPermission(true)
            return
        }
        ActivityCompat.requestPermissions(
            this,
            arrayOf(audioPermissionName()),
            AUDIO_PERMISSION_REQUEST_CODE
        )
    }

    private fun notifyAudioPermission(granted: Boolean) {
        webView.post {
            if (!isFinishing && !isDestroyed) {
                webView.evaluateJavascript(
                    "window.onAuroraAudioPermission && window.onAuroraAudioPermission(" +
                        (if (granted) "true" else "false") + ");",
                    null
                )
            }
        }
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == AUDIO_PERMISSION_REQUEST_CODE) {
            val granted = grantResults.isNotEmpty() &&
                grantResults[0] == PackageManager.PERMISSION_GRANTED
            notifyAudioPermission(granted)
        }
    }

    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == FILE_CHOOSER_REQUEST_CODE) {
            if (filePathCallback != null) {
                val results: Array<Uri>? = when {
                    resultCode != RESULT_OK -> null
                    data?.clipData != null -> {
                        val clip = data.clipData!!
                        Array(clip.itemCount) { i -> clip.getItemAt(i).uri }
                    }
                    data?.data != null -> arrayOf(data.data!!)
                    else -> null
                }
                filePathCallback?.onReceiveValue(results)
                filePathCallback = null
            }
        }
    }

    override fun onDestroy() {
        PlaybackService.commandListener = null
        PlaybackService.stop(this)
        webView.destroy()
        super.onDestroy()
    }
}
