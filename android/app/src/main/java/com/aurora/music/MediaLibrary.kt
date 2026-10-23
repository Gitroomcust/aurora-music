package com.aurora.music

import android.content.ContentUris
import android.content.Context
import android.graphics.Bitmap
import android.os.Build
import android.os.ParcelFileDescriptor
import android.provider.MediaStore
import android.util.Size
import android.webkit.WebResourceResponse
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.util.Locale

/**
 * Reads the device audio library from MediaStore and streams it to the WebView.
 *
 * The player itself is an HTML page, so audio files cannot be served with a
 * file:// URL. Instead every track gets a stable synthetic address
 * (https://aurora.local/audio/<id>) that [MainActivity] intercepts and answers
 * with the bytes from the content provider. Ids survive restarts, which also
 * makes them usable as keys for restoring the last played track.
 */
object MediaLibrary {

    const val HOST = "aurora.local"

    private const val ART_SIZE = 512

    fun audioUrl(id: Long): String = "https://" + HOST + "/audio/" + id

    fun artUrl(albumId: Long): String = "https://" + HOST + "/art/" + albumId

    /** Returns the music library as a JSON array for the JavaScript bridge. */
    fun list(context: Context): String {
        val result = JSONArray()
        val resolver = context.contentResolver

        val projection = arrayOf(
            MediaStore.Audio.Media._ID,
            MediaStore.Audio.Media.TITLE,
            MediaStore.Audio.Media.ARTIST,
            MediaStore.Audio.Media.ALBUM,
            MediaStore.Audio.Media.ALBUM_ID,
            MediaStore.Audio.Media.DURATION,
            MediaStore.Audio.Media.MIME_TYPE,
            MediaStore.Audio.Media.DISPLAY_NAME
        )

        val collection = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL)
        } else {
            MediaStore.Audio.Media.EXTERNAL_CONTENT_URI
        }

        val selection = MediaStore.Audio.Media.IS_MUSIC + " != 0"
        val sort = MediaStore.Audio.Media.TITLE + " COLLATE NOCASE ASC"

        try {
            resolver.query(collection, projection, selection, null, sort)?.use { cursor ->
                val idIndex = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media._ID)
                val titleIndex = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.TITLE)
                val artistIndex = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.ARTIST)
                val albumIndex = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.ALBUM)
                val albumIdIndex = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.ALBUM_ID)
                val durationIndex = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.DURATION)
                val mimeIndex = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.MIME_TYPE)
                val nameIndex = cursor.getColumnIndexOrThrow(MediaStore.Audio.Media.DISPLAY_NAME)

                while (cursor.moveToNext()) {
                    val id = cursor.getLong(idIndex)
                    if (id <= 0) continue

                    val name = cursor.getString(nameIndex) ?: ""
                    val mime = cursor.getString(mimeIndex) ?: ""
                    val albumId = cursor.getLong(albumIdIndex)

                    val track = JSONObject()
                    track.put("id", id)
                    track.put("title", cleanTitle(cursor.getString(titleIndex), name))
                    track.put("artist", cursor.getString(artistIndex) ?: "")
                    track.put("album", cursor.getString(albumIndex) ?: "")
                    track.put("duration", cursor.getLong(durationIndex))
                    track.put("ext", extensionOf(name, mime))
                    track.put("url", audioUrl(id))
                    if (albumId > 0) track.put("art", artUrl(albumId))
                    result.put(track)
                }
            }
        } catch (e: Exception) {
            android.util.Log.w("AuroraMusic", "MediaStore query failed", e)
        }

        return result.toString()
    }

    /** Streams a MediaStore audio item with byte-range support for seeking. */
    fun openAudio(context: Context, id: Long, rangeHeader: String?, head: Boolean): WebResourceResponse {
        val resolver = context.contentResolver
        val collection = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            MediaStore.Audio.Media.getContentUri(MediaStore.VOLUME_EXTERNAL)
        } else {
            MediaStore.Audio.Media.EXTERNAL_CONTENT_URI
        }
        val uri = ContentUris.withAppendedId(collection, id)

        var size = -1L
        var mime = "audio/mpeg"
        try {
            resolver.query(
                uri,
                arrayOf(MediaStore.Audio.Media.SIZE, MediaStore.Audio.Media.MIME_TYPE),
                null, null, null
            )?.use { cursor ->
                if (cursor.moveToFirst()) {
                    size = cursor.getLong(0)
                    val known = cursor.getString(1)
                    if (!known.isNullOrEmpty()) mime = known
                }
            }
        } catch (e: Exception) {
            android.util.Log.w("AuroraMusic", "Cannot read audio metadata for $id", e)
        }

        var start = 0L
        var end = if (size > 0) size - 1 else -1L
        val range = parseRange(rangeHeader, size)
        if (range != null) {
            start = range.first
            end = range.second
        }
        if (end < start) end = if (size > 0) size - 1 else -1L

        val headers = HashMap<String, String>()
        headers["Accept-Ranges"] = "bytes"
        headers["Access-Control-Allow-Origin"] = "*"
        headers["Cache-Control"] = "no-store"

        val partial = start > 0 || (size > 0 && end >= 0 && end < size - 1)
        val length = if (end >= start) end - start + 1 else -1L

        if (size > 0) {
            headers["Content-Range"] = "bytes " + start + "-" + end + "/" + size
        }
        if (length >= 0) {
            headers["Content-Length"] = length.toString()
        }

        val status = if (partial) 206 else 200
        val reason = if (partial) "Partial Content" else "OK"

        if (head) {
            return WebResourceResponse(mime, null, status, reason, headers, ByteArrayInputStream(ByteArray(0)))
        }

        val descriptor: ParcelFileDescriptor? = try {
            resolver.openFileDescriptor(uri, "r")
        } catch (e: Exception) {
            android.util.Log.w("AuroraMusic", "Cannot open audio stream for $id", e)
            null
        }

        if (descriptor == null) {
            return WebResourceResponse(
                "text/plain", "utf-8", 404, "Not Found",
                HashMap<String, String>(), ByteArrayInputStream(ByteArray(0))
            )
        }

        val stream = ParcelFileDescriptor.AutoCloseInputStream(descriptor)
        try {
            if (start > 0) stream.channel.position(start)
            headers["Access-Control-Allow-Origin"] = "*"
            val body: java.io.InputStream =
                if (length >= 0 && end < size - 1) BoundedStream(stream, length) else stream
            return WebResourceResponse(mime, null, status, reason, headers, body)
        } catch (e: Exception) {
            try { stream.close() } catch (ignored: Exception) {}
            return WebResourceResponse(
                "text/plain", "utf-8", 500, "Internal Error",
                HashMap<String, String>(), ByteArrayInputStream(ByteArray(0))
            )
        }
    }

    /** Returns embedded album art, or a 404 response when the album has none. */
    fun openArt(context: Context, albumId: Long): WebResourceResponse {
        val resolver = context.contentResolver
        var stream: InputStream? = null

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            try {
                val uri = ContentUris.withAppendedId(
                    MediaStore.Audio.Albums.EXTERNAL_CONTENT_URI, albumId
                )
                val bitmap = resolver.loadThumbnail(uri, Size(ART_SIZE, ART_SIZE), null)
                val buffer = ByteArrayOutputStream()
                bitmap.compress(Bitmap.CompressFormat.JPEG, 88, buffer)
                bitmap.recycle()
                stream = ByteArrayInputStream(buffer.toByteArray())
            } catch (e: Exception) {
                stream = null
            }
        }

        if (stream == null) {
            try {
                stream = resolver.openInputStream(
                    android.net.Uri.parse("content://media/external/audio/albumart/" + albumId)
                )
            } catch (e: Exception) {
                stream = null
            }
        }

        val headers = HashMap<String, String>()
        headers["Access-Control-Allow-Origin"] = "*"
        headers["Cache-Control"] = "public, max-age=86400"

        if (stream == null) {
            return WebResourceResponse(
                "text/plain", "utf-8", 404, "Not Found",
                headers, ByteArrayInputStream(ByteArray(0))
            )
        }

        return WebResourceResponse("image/jpeg", null, 200, "OK", headers, stream)
    }

    /** Caps a file stream at Content-Length so partial responses stay exact. */
    private class BoundedStream(
        private val inner: InputStream,
        private var remaining: Long
    ) : InputStream() {
        override fun read(): Int {
            if (remaining <= 0) return -1
            val value = inner.read()
            if (value >= 0) remaining--
            return value
        }

        override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
            if (remaining <= 0) return -1
            val wanted = minOf(length.toLong(), remaining).toInt()
            val read = inner.read(buffer, offset, wanted)
            if (read > 0) remaining -= read
            return read
        }

        override fun close() {
            inner.close()
        }
    }

    private fun parseRange(header: String?, size: Long): Pair<Long, Long>? {        if (header.isNullOrEmpty() || size <= 0) return null
        val value = header.trim()
        if (!value.startsWith("bytes=", ignoreCase = true)) return null
        val spec = value.substring(6).split(",")[0].trim()
        if (spec.isEmpty()) return null

        val dash = spec.indexOf('-')
        if (dash < 0) return null

        return try {
            val startText = spec.substring(0, dash).trim()
            val endText = spec.substring(dash + 1).trim()
            if (startText.isEmpty()) {
                // Suffix range: the last N bytes.
                val length = endText.toLongOrNull() ?: return null
                if (length <= 0) return null
                val start = maxOf(0, size - length)
                Pair(start, size - 1)
            } else {
                val start = startText.toLongOrNull() ?: return null
                if (start >= size) return null
                val end = if (endText.isEmpty()) size - 1
                else minOf(endText.toLongOrNull() ?: (size - 1), size - 1)
                Pair(start, end)
            }
        } catch (e: Exception) {
            null
        }
    }

    private fun cleanTitle(title: String?, fileName: String): String {
        val trimmed = title?.trim().orEmpty()
        if (trimmed.isNotEmpty() && trimmed != "<unknown>") return trimmed
        val name = fileName.substringBeforeLast('.')
        return name.ifBlank { "Аудиофайл" }
    }

    private fun extensionOf(fileName: String, mime: String): String {
        val dot = fileName.lastIndexOf('.')
        if (dot >= 0 && dot < fileName.length - 1) {
            return fileName.substring(dot + 1).uppercase(Locale.ROOT)
        }
        return when {
            mime.contains("mpeg") -> "MP3"
            mime.contains("mp4") || mime.contains("aac") -> "M4A"
            mime.contains("flac") -> "FLAC"
            mime.contains("ogg") -> "OGG"
            mime.contains("wav") -> "WAV"
            else -> ""
        }
    }
}
