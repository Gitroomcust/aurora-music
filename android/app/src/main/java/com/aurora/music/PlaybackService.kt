package com.aurora.music

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.support.v4.media.MediaMetadataCompat
import android.support.v4.media.session.MediaSessionCompat
import android.support.v4.media.session.PlaybackStateCompat
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import androidx.media.app.NotificationCompat.MediaStyle

/**
 * Keeps WebView audio alive while the app is in the background and exposes
 * lock-screen / notification controls through a platform MediaSession.
 *
 * The WebView remains the actual audio engine: commands received here are
 * forwarded to the page through [commandListener], and the page reports its
 * state back through the AndroidMedia JavaScript bridge in MainActivity.
 */
class PlaybackService : Service() {

    private lateinit var mediaSession: MediaSessionCompat
    private var wakeLock: PowerManager.WakeLock? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()

        mediaSession = MediaSessionCompat(this, "AuroraMusic").apply {
            setFlags(
                MediaSessionCompat.FLAG_HANDLES_MEDIA_BUTTONS or
                    MediaSessionCompat.FLAG_HANDLES_TRANSPORT_CONTROLS
            )
            setCallback(object : MediaSessionCompat.Callback() {
                override fun onPlay() {
                    sendCommand("play")
                }

                override fun onPause() {
                    sendCommand("pause")
                }

                override fun onSkipToNext() {
                    sendCommand("next")
                }

                override fun onSkipToPrevious() {
                    sendCommand("prev")
                }

                override fun onStop() {
                    sendCommand("pause")
                }
            })
            isActive = true
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_COMMAND -> {
                val command = intent.getStringExtra(EXTRA_COMMAND)
                if (!command.isNullOrEmpty()) {
                    sendCommand(command)
                }
                return START_NOT_STICKY
            }
        }

        val title = intent?.getStringExtra(EXTRA_TITLE) ?: ""
        val artist = intent?.getStringExtra(EXTRA_ARTIST) ?: ""
        val playing = intent?.getBooleanExtra(EXTRA_PLAYING, false) ?: false

        if (title.isEmpty() && !playing) {
            stopPlayback()
            return START_NOT_STICKY
        }

        updateSession(title, artist, playing)
        startForeground(NOTIFICATION_ID, buildNotification(title, artist, playing))
        setWakeLock(playing)
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        setWakeLock(false)
        if (::mediaSession.isInitialized) {
            mediaSession.release()
        }
        super.onDestroy()
    }

    private fun sendCommand(command: String) {
        commandListener?.invoke(command)
    }

    private fun updateSession(title: String, artist: String, playing: Boolean) {
        val metadata = MediaMetadataCompat.Builder()
            .putString(MediaMetadataCompat.METADATA_KEY_TITLE, title)
            .putString(MediaMetadataCompat.METADATA_KEY_ARTIST, artist)
            .build()

        val actions = PlaybackStateCompat.ACTION_PLAY or
            PlaybackStateCompat.ACTION_PAUSE or
            PlaybackStateCompat.ACTION_PLAY_PAUSE or
            PlaybackStateCompat.ACTION_SKIP_TO_NEXT or
            PlaybackStateCompat.ACTION_SKIP_TO_PREVIOUS

        val state = PlaybackStateCompat.Builder()
            .setActions(actions)
            .setState(
                if (playing) {
                    PlaybackStateCompat.STATE_PLAYING
                } else {
                    PlaybackStateCompat.STATE_PAUSED
                },
                PlaybackStateCompat.PLAYBACK_POSITION_UNKNOWN,
                1f
            )
            .build()

        mediaSession.setMetadata(metadata)
        mediaSession.setPlaybackState(state)
        mediaSession.isActive = true
    }

    private fun buildNotification(title: String, artist: String, playing: Boolean): Notification {
        val openIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val builder = NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_music)
            .setContentTitle(title.ifEmpty { "Aurora Music" })
            .setContentText(artist)
            .setContentIntent(openIntent)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setCategory(NotificationCompat.CATEGORY_TRANSPORT)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setOngoing(playing)
            .addAction(
                android.R.drawable.ic_media_previous,
                "Предыдущий",
                commandIntent("prev", 11)
            )
            .addAction(
                if (playing) android.R.drawable.ic_media_pause else android.R.drawable.ic_media_play,
                if (playing) "Пауза" else "Воспроизвести",
                commandIntent(if (playing) "pause" else "play", 12)
            )
            .addAction(
                android.R.drawable.ic_media_next,
                "Следующий",
                commandIntent("next", 13)
            )
            .setStyle(
                MediaStyle()
                    .setMediaSession(mediaSession.sessionToken)
                    .setShowActionsInCompactView(0, 1, 2)
            )

        return builder.build()
    }

    private fun commandIntent(command: String, requestCode: Int): PendingIntent {
        val intent = Intent(this, PlaybackService::class.java).apply {
            action = ACTION_COMMAND
            putExtra(EXTRA_COMMAND, command)
        }
        return PendingIntent.getService(
            this,
            requestCode,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            if (manager.getNotificationChannel(CHANNEL_ID) == null) {
                val channel = NotificationChannel(
                    CHANNEL_ID,
                    "Воспроизведение",
                    NotificationManager.IMPORTANCE_LOW
                ).apply {
                    description = "Управление музыкой и прямым эфиром"
                    setShowBadge(false)
                    setSound(null, null)
                }
                manager.createNotificationChannel(channel)
            }
        }
    }

    private fun setWakeLock(enabled: Boolean) {
        if (enabled) {
            if (wakeLock == null) {
                val power = getSystemService(Context.POWER_SERVICE) as PowerManager
                wakeLock = power.newWakeLock(
                    PowerManager.PARTIAL_WAKE_LOCK,
                    "AuroraMusic:playback"
                ).apply {
                    setReferenceCounted(false)
                }
            }
            wakeLock?.acquire(12 * 60 * 60 * 1000L)
        } else {
            wakeLock?.let { lock ->
                if (lock.isHeld) {
                    lock.release()
                }
            }
        }
    }

    private fun stopPlayback() {
        setWakeLock(false)
        if (::mediaSession.isInitialized) {
            mediaSession.isActive = false
        }
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    companion object {
        private const val CHANNEL_ID = "aurora_playback"
        private const val NOTIFICATION_ID = 4101
        private const val ACTION_COMMAND = "com.aurora.music.action.COMMAND"
        private const val EXTRA_COMMAND = "command"
        private const val EXTRA_TITLE = "title"
        private const val EXTRA_ARTIST = "artist"
        private const val EXTRA_PLAYING = "playing"

        /**
         * Set by MainActivity while the WebView is alive. Kept outside the
         * service so the UI owns the WebView reference.
         */
        @Volatile
        var commandListener: ((String) -> Unit)? = null

        fun update(context: Context, title: String, artist: String, playing: Boolean) {
            val intent = Intent(context, PlaybackService::class.java).apply {
                putExtra(EXTRA_TITLE, title)
                putExtra(EXTRA_ARTIST, artist)
                putExtra(EXTRA_PLAYING, playing)
            }
            try {
                ContextCompat.startForegroundService(context, intent)
            } catch (e: RuntimeException) {
                // Android 12+ forbids starting a foreground service from the
                // background. If the service was already stopped there is no
                // way to restore the notification, but the app must not crash.
                android.util.Log.w("AuroraMusic", "Cannot start playback service", e)
            }
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, PlaybackService::class.java))
        }
    }
}
