using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Lumen.Audio;

namespace Lumen.Net
{
    internal sealed class LanServer : IDisposable
    {
        private readonly LiveAudioHub _hub;
        private readonly string _userName;
        private readonly string _computerName;
        private readonly object _trackSync = new object();
        private readonly object _lifecycleSync = new object();

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private List<Track> _tracks = new List<Track>();

        public int Port { get; private set; }
        public string LanUrl { get; private set; }
        public string LocalUrl { get; private set; }
        public volatile bool IsLive;
        private long _liveMilliseconds;

        public double LiveSeconds
        {
            get { return Interlocked.CompareExchange(ref _liveMilliseconds, 0, 0) / 1000.0; }
            set
            {
                double seconds = value;
                if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
                if (seconds > 31536000) seconds = 31536000;
                Interlocked.Exchange(ref _liveMilliseconds, (long)(seconds * 1000.0));
            }
        }

        public LanServer(LiveAudioHub hub, string userName, string computerName)
        {
            _hub = hub;
            _userName = userName;
            _computerName = computerName;
            LanUrl = "";
            LocalUrl = "";
        }

        public string UserName
        {
            get { return _userName; }
        }

        public string ComputerName
        {
            get { return _computerName; }
        }

        public void Start()
        {
            lock (_lifecycleSync)
            {
                if (_running) return;

                try
                {
                    // Online playback is relayed through this process, so allow
                    // several parallel range requests and modern TLS.
                    ServicePointManager.DefaultConnectionLimit = 24;
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                }
                catch { }

                IPAddress address = FindLanAddress();
                Exception lastError = null;
                for (int port = 8737; port < 8758; port++)
                {
                    try
                    {
                        TcpListener listener = new TcpListener(IPAddress.Any, port);
                        listener.Start();
                        _listener = listener;
                        Port = port;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }

                if (_listener == null)
                {
                    throw new InvalidOperationException("Не удалось занять сетевой порт.", lastError);
                }

                LanUrl = "http://" + FormatAddress(address) + ":" + Port + "/";
                LocalUrl = "http://127.0.0.1:" + Port + "/";
                _running = true;
                _acceptThread = new Thread(AcceptLoop);
                _acceptThread.IsBackground = true;
                _acceptThread.Name = "Lumen LAN server";
                _acceptThread.Start();
            }
        }

        public void SetTracks(IList<Track> tracks)
        {
            List<Track> copy = new List<Track>();
            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (tracks[i] != null) copy.Add(tracks[i]);
                }
            }
            lock (_trackSync)
            {
                _tracks = copy;
            }
        }

        public void Dispose()
        {
            lock (_lifecycleSync)
            {
                _running = false;
                if (_listener != null)
                {
                    try { _listener.Stop(); }
                    catch { }
                    _listener = null;
                }
            }
            try
            {
                if (_acceptThread != null) _acceptThread.Join(700);
            }
            catch { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Log.Write("LAN accept failed", ex);
                    Thread.Sleep(120);
                    continue;
                }

                Thread thread = new Thread(ClientThread);
                thread.IsBackground = true;
                thread.Name = "Lumen HTTP client";
                thread.Start(client);
            }
        }

        private void ClientThread(object state)
        {
            TcpClient client = state as TcpClient;
            if (client == null) return;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 12000;
                client.SendTimeout = 18000;
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true))
                {
                    string requestLine = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(requestLine)) return;

                    string[] parts = requestLine.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                    {
                        WriteText(stream, 400, "Bad Request", "text/plain; charset=utf-8", "Bad request", false);
                        return;
                    }

                    string method = parts[0].ToUpperInvariant();
                    string target = parts[1];
                    if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        Uri uri;
                        if (Uri.TryCreate(target, UriKind.Absolute, out uri))
                        {
                            target = uri.PathAndQuery;
                        }
                    }

                    Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string line;
                    while ((line = reader.ReadLine()) != null && line.Length > 0)
                    {
                        int colon = line.IndexOf(':');
                        if (colon > 0)
                        {
                            headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
                        }
                    }

                    string path = target;
                    string queryString = "";
                    int query = path.IndexOf('?');
                    if (query >= 0)
                    {
                        queryString = path.Substring(query + 1);
                        path = path.Substring(0, query);
                    }

                    bool head = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
                    if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && !head)
                    {
                        WriteText(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                            "Only GET and HEAD are supported.", head);
                        return;
                    }

                    if (path == "/" || path == "/index.html")
                    {
                        string html = WebUi.Render();
                        WriteText(stream, 200, "OK", "text/html; charset=utf-8", html, head);
                    }
                    else if (path == "/api/state")
                    {
                        WriteText(stream, 200, "OK", "application/json; charset=utf-8",
                            BuildStateJson(), head);
                    }
                    else if (path == "/api/tracks")
                    {
                        WriteText(stream, 200, "OK", "application/json; charset=utf-8",
                            BuildTracksJson(), head);
                    }
                    else if (path.StartsWith("/audio/", StringComparison.OrdinalIgnoreCase))
                    {
                        ServeAudio(stream, headers, path.Substring(7), head);
                    }
                    else if (path == "/proxy")
                    {
                        ServeProxy(stream, headers, queryString, head);
                    }
                    else if (path == "/live.wav")
                    {
                        ServeLive(stream, client, head);
                    }
                    else if (path == "/health")
                    {
                        WriteText(stream, 200, "OK", "text/plain; charset=utf-8", "ok", head);
                    }
                    else if (path == "/favicon.ico")
                    {
                        WriteText(stream, 204, "No Content", "image/x-icon", "", head);
                    }
                    else if (path == "/manifest.json")
                    {
                        WriteText(stream, 200, "OK", "application/manifest+json; charset=utf-8",
                            BuildManifestJson(), head);
                    }
                    else if (path == "/icon.png")
                    {
                        ServeIcon(stream, head);
                    }
                    else
                    {
                        WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8",
                            "Not found", head);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("HTTP client failed", ex);
                try { client.Close(); }
                catch { }
            }
        }

        private void ServeAudio(NetworkStream stream, Dictionary<string, string> headers,
            string indexText, bool head)
        {
            int index;
            if (!int.TryParse(Uri.UnescapeDataString(indexText), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out index))
            {
                WriteText(stream, 400, "Bad Request", "text/plain; charset=utf-8", "Bad track index", head);
                return;
            }

            Track track = null;
            lock (_trackSync)
            {
                if (index >= 0 && index < _tracks.Count) track = _tracks[index];
            }

            if (track == null || !File.Exists(track.Path))
            {
                WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "Track not found", head);
                return;
            }

            long length;
            try
            {
                length = new FileInfo(track.Path).Length;
            }
            catch
            {
                WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "Track not found", head);
                return;
            }
            if (length <= 0)
            {
                WriteText(stream, 404, "Not Found", "text/plain; charset=utf-8", "Empty track", head);
                return;
            }

            long start = 0;
            long end = length - 1;
            bool partial = false;
            string range;
            if (headers.TryGetValue("Range", out range) && !string.IsNullOrWhiteSpace(range))
            {
                if (!TryParseRange(range, length, out start, out end))
                {
                    WriteHeaders(stream, 416, "Range Not Satisfiable",
                        "text/plain; charset=utf-8", 0,
                        "bytes */" + length.ToString(CultureInfo.InvariantCulture), null, head);
                    return;
                }
                partial = true;
            }

            long contentLength = end - start + 1;
            string contentType = MimeFor(track.Path);
            string contentRange = partial
                ? "bytes " + start.ToString(CultureInfo.InvariantCulture) + "-" +
                  end.ToString(CultureInfo.InvariantCulture) + "/" +
                  length.ToString(CultureInfo.InvariantCulture)
                : null;

            WriteHeaders(stream, partial ? 206 : 200, partial ? "Partial Content" : "OK",
                contentType, contentLength, contentRange, "bytes", head);
            if (head) return;

            try
            {
                using (FileStream file = new FileStream(track.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
                {
                    file.Seek(start, SeekOrigin.Begin);
                    byte[] buffer = new byte[65536];
                    long remaining = contentLength;
                    while (remaining > 0)
                    {
                        int want = (int)Math.Min(buffer.Length, remaining);
                        int read = file.Read(buffer, 0, want);
                        if (read <= 0) break;
                        stream.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
            }
            catch (IOException)
            {
                // The browser closed the connection or seeked elsewhere.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // Relays an online audio stream (Internet Archive, radio, direct URL)
        // through the PC. This keeps the web player same-origin, so the Web
        // Audio analyser keeps working and streams without CORS headers can
        // still be played on the phone.
        private void ServeProxy(NetworkStream stream, Dictionary<string, string> requestHeaders,
            string queryString, bool head)
        {
            string rawUrl = GetQueryValue(queryString, "url");
            Uri uri;
            if (string.IsNullOrWhiteSpace(rawUrl) ||
                !Uri.TryCreate(rawUrl, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                WriteText(stream, 400, "Bad Request", "text/plain; charset=utf-8",
                    "A valid http(s) stream URL is required.", head);
                return;
            }

            if (IsPrivateTarget(uri))
            {
                WriteText(stream, 403, "Forbidden", "text/plain; charset=utf-8",
                    "Local and private network addresses are not proxied.", head);
                return;
            }

            HttpWebRequest request = null;
            HttpWebResponse response = null;
            bool headersSent = false;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(uri);
                request.Method = head ? "HEAD" : "GET";
                request.AllowAutoRedirect = true;
                request.MaximumAutomaticRedirections = 8;
                request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AuroraMusic/1.1";
                request.Accept = "audio/*,application/ogg,*/*;q=0.8";
                request.Timeout = 12000;
                request.ReadWriteTimeout = 30000;
                request.KeepAlive = false;
                request.AutomaticDecompression = DecompressionMethods.None;
                try { request.Headers["Icy-MetaData"] = "0"; }
                catch { }

                string range;
                if (requestHeaders.TryGetValue("Range", out range)) ApplyRange(request, range);

                try
                {
                    response = (HttpWebResponse)request.GetResponse();
                }
                catch (WebException ex)
                {
                    response = ex.Response as HttpWebResponse;
                    if (response == null)
                    {
                        WriteText(stream, 502, "Bad Gateway", "text/plain; charset=utf-8",
                            "Не удалось получить поток с источника.", head);
                        return;
                    }
                }

                using (response)
                {
                    int statusCode = (int)response.StatusCode;
                    if (statusCode >= 400)
                    {
                        WriteText(stream, 502, "Bad Gateway", "text/plain; charset=utf-8",
                            "Источник вернул ошибку " + statusCode.ToString(CultureInfo.InvariantCulture) + ".",
                            head);
                        return;
                    }

                    string contentType = response.ContentType;
                    if (string.IsNullOrEmpty(contentType)) contentType = "application/octet-stream";
                    if (!IsAudioContentType(contentType))
                    {
                        WriteText(stream, 415, "Unsupported Media Type", "text/plain; charset=utf-8",
                            "Источник вернул не аудио (" + contentType + ").", head);
                        return;
                    }

                    long contentLength = response.ContentLength;
                    string contentRange = response.Headers["Content-Range"];
                    string acceptRanges = response.Headers["Accept-Ranges"];
                    string statusText = response.StatusDescription;
                    if (string.IsNullOrEmpty(statusText))
                    {
                        statusText = statusCode == 206 ? "Partial Content" : "OK";
                    }
                    WriteProxyHeaders(stream, statusCode, statusText, contentType,
                        contentLength, contentRange, acceptRanges);
                    headersSent = true;
                    if (head) return;

                    try
                    {
                        using (Stream remote = response.GetResponseStream())
                        {
                            if (remote == null) return;
                            byte[] buffer = new byte[65536];
                            while (true)
                            {
                                int read = remote.Read(buffer, 0, buffer.Length);
                                if (read <= 0) break;
                                stream.Write(buffer, 0, read);
                            }
                            stream.Flush();
                        }
                    }
                    catch (IOException)
                    {
                        // The player seeked or closed the tab.
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Stream proxy failed", ex);
                if (!headersSent)
                {
                    try
                    {
                        WriteText(stream, 502, "Bad Gateway", "text/plain; charset=utf-8",
                            "Ошибка проксирования потока.", head);
                    }
                    catch { }
                }
                if (response != null) response.Dispose();
            }
        }

        private static string GetQueryValue(string queryString, string key)
        {
            if (string.IsNullOrEmpty(queryString)) return null;
            string[] pairs = queryString.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i];
                if (pair.Length == 0) continue;
                int equals = pair.IndexOf('=');
                string name = equals >= 0 ? pair.Substring(0, equals) : pair;
                if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string value = equals >= 0 ? pair.Substring(equals + 1) : "";
                return Uri.UnescapeDataString(value);
            }
            return null;
        }

        private static void ApplyRange(HttpWebRequest request, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return;
            string spec = value.Substring(6).Trim();
            int comma = spec.IndexOf(',');
            if (comma >= 0) spec = spec.Substring(0, comma).Trim();
            int dash = spec.IndexOf('-');
            if (dash < 0) return;

            string fromText = spec.Substring(0, dash).Trim();
            string toText = spec.Substring(dash + 1).Trim();
            long from;
            long to;
            if (fromText.Length == 0)
            {
                if (long.TryParse(toText, NumberStyles.Integer, CultureInfo.InvariantCulture, out to) && to > 0)
                {
                    request.AddRange(-to);
                }
                return;
            }

            if (!long.TryParse(fromText, NumberStyles.Integer, CultureInfo.InvariantCulture, out from) ||
                from < 0) return;
            if (toText.Length == 0)
            {
                request.AddRange(from);
            }
            else if (long.TryParse(toText, NumberStyles.Integer, CultureInfo.InvariantCulture, out to) &&
                to >= from)
            {
                request.AddRange(from, to);
            }
            else
            {
                request.AddRange(from);
            }
        }

        private static bool IsAudioContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return true;
            string value = contentType.ToLowerInvariant();
            return value.StartsWith("audio/") ||
                   value.StartsWith("video/") ||
                   value.StartsWith("application/ogg") ||
                   value.StartsWith("application/octet-stream") ||
                   value.StartsWith("binary/octet-stream");
        }

        private static bool IsPrivateTarget(Uri uri)
        {
            if (uri.IsLoopback) return true;
            string host = uri.DnsSafeHost;
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal)) return IsPrivateAddress(literal);

            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(host);
                for (int i = 0; i < addresses.Length; i++)
                {
                    if (IsPrivateAddress(addresses[i])) return true;
                }
            }
            catch
            {
                return true;
            }
            return false;
        }

        private static bool IsPrivateAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;
            byte[] bytes = address.GetAddressBytes();
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                if (bytes[0] == 0 || bytes[0] >= 224) return true;
                if (bytes[0] == 10) return true;
                if (bytes[0] == 127) return true;
                if (bytes[0] == 169 && bytes[1] == 254) return true;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                return false;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                {
                    return true;
                }
                if ((bytes[0] & 0xFE) == 0xFC) return true;
                // IPv4-mapped ::ffff:a.b.c.d
                if (bytes.Length == 16 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 &&
                    bytes[3] == 0 && bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 &&
                    bytes[7] == 0 && bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0xFF &&
                    bytes[11] == 0xFF)
                {
                    IPAddress mapped = new IPAddress(new byte[] { bytes[12], bytes[13], bytes[14], bytes[15] });
                    return IsPrivateAddress(mapped);
                }
                return false;
            }

            return true;
        }

        private static void WriteProxyHeaders(NetworkStream stream, int code, string status,
            string contentType, long contentLength, string contentRange, string acceptRanges)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("HTTP/1.1 ").Append(code).Append(' ').Append(status).Append("\r\n");
            builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
            if (contentLength >= 0)
            {
                builder.Append("Content-Length: ")
                    .Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            if (!string.IsNullOrEmpty(contentRange))
            {
                builder.Append("Content-Range: ").Append(contentRange).Append("\r\n");
            }
            if (!string.IsNullOrEmpty(acceptRanges))
            {
                builder.Append("Accept-Ranges: ").Append(acceptRanges).Append("\r\n");
            }
            else if (contentLength >= 0)
            {
                builder.Append("Accept-Ranges: bytes\r\n");
            }
            builder.Append("Access-Control-Allow-Origin: *\r\n");
            builder.Append("Cache-Control: no-store\r\n");
            builder.Append("Connection: close\r\n\r\n");
            byte[] headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Flush();
        }

        private void ServeLive(NetworkStream stream, TcpClient client, bool head)
        {
            if (!IsLive || !_hub.IsConfigured)
            {
                WriteText(stream, 503, "Service Unavailable", "text/plain; charset=utf-8",
                    "Live stream is not active. Start capture in Lumen.", head);
                return;
            }

            if (head)
            {
                WriteText(stream, 200, "OK", "audio/wav", "", true);
                return;
            }

            string headers =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: audio/wav\r\n" +
                "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
                "Pragma: no-cache\r\n" +
                "Connection: close\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +
                "X-Accel-Buffering: no\r\n" +
                "Transfer-Encoding: chunked\r\n\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Flush();

            LiveAudioHub.Subscriber subscriber = null;
            try
            {
                byte[] wavHeader = _hub.CreateWavHeader();
                WriteChunk(stream, wavHeader, wavHeader.Length);
                subscriber = _hub.Subscribe();

                while (true)
                {
                    byte[] data;
                    if (subscriber.TryTake(out data, 900))
                    {
                        if (data != null && data.Length > 0)
                        {
                            WriteChunk(stream, data, data.Length);
                        }
                        continue;
                    }

                    if (!IsLive || !_hub.IsConfigured) break;
                    if (!ClientStillConnected(client)) break;
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Log.Write("Live stream failed", ex);
            }
            finally
            {
                if (subscriber != null) _hub.Unsubscribe(subscriber);
                try
                {
                    byte[] terminator = Encoding.ASCII.GetBytes("0\r\n\r\n");
                    stream.Write(terminator, 0, terminator.Length);
                    stream.Flush();
                }
                catch { }
            }
        }

        private static bool ClientStillConnected(TcpClient client)
        {
            try
            {
                Socket socket = client.Client;
                if (socket == null || !socket.Connected) return false;
                return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch
            {
                return false;
            }
        }

        private static void WriteChunk(NetworkStream stream, byte[] data, int count)
        {
            if (count <= 0) return;
            string size = count.ToString("X", CultureInfo.InvariantCulture) + "\r\n";
            byte[] sizeBytes = Encoding.ASCII.GetBytes(size);
            stream.Write(sizeBytes, 0, sizeBytes.Length);
            stream.Write(data, 0, count);
            byte[] end = new byte[] { 13, 10 };
            stream.Write(end, 0, 2);
            stream.Flush();
        }

        private static void WriteText(NetworkStream stream, int code, string status,
            string contentType, string body, bool head)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
            WriteHeaders(stream, code, status, contentType, bytes.Length, null, null, head);
            if (head) return;
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static void WriteBytes(NetworkStream stream, int code, string status,
            string contentType, byte[] bytes, bool head)
        {
            int length = bytes != null ? bytes.Length : 0;
            WriteHeaders(stream, code, status, contentType, length, null, null, head);
            if (head || length == 0) return;
            stream.Write(bytes, 0, length);
            stream.Flush();
        }

        private static string BuildManifestJson()
        {
            return "{\"name\":\"Aurora Music\",\"short_name\":\"Aurora\",\"description\":\"Музыкальный плеер для Android и Windows\",\"start_url\":\"/\",\"display\":\"standalone\",\"background_color\":\"#050509\",\"theme_color\":\"#050509\",\"orientation\":\"any\",\"icons\":[{\"src\":\"/icon.png\",\"sizes\":\"256x256\",\"type\":\"image/png\",\"purpose\":\"any maskable\"}]}";
        }

        private static void ServeIcon(NetworkStream stream, bool head)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string iconPath = Path.Combine(baseDir, "assets", "lumen-256.png");
                if (!File.Exists(iconPath))
                {
                    string parent = Directory.GetParent(baseDir) != null ? Directory.GetParent(baseDir).FullName : null;
                    if (parent != null) iconPath = Path.Combine(parent, "assets", "lumen-256.png");
                }
                if (File.Exists(iconPath))
                {
                    byte[] data = File.ReadAllBytes(iconPath);
                    WriteBytes(stream, 200, "OK", "image/png", data, head);
                    return;
                }
            }
            catch { }
            WriteText(stream, 404, "Not Found", "text/plain", "Icon not found", head);
        }

        private static void WriteHeaders(NetworkStream stream, int code, string status,
            string contentType, long contentLength, string contentRange, string acceptRanges, bool head)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("HTTP/1.1 ").Append(code).Append(' ').Append(status).Append("\r\n");
            builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
            builder.Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (!string.IsNullOrEmpty(contentRange))
            {
                builder.Append("Content-Range: ").Append(contentRange).Append("\r\n");
            }
            if (!string.IsNullOrEmpty(acceptRanges))
            {
                builder.Append("Accept-Ranges: ").Append(acceptRanges).Append("\r\n");
            }
            builder.Append("Access-Control-Allow-Origin: *\r\n");
            builder.Append("Cache-Control: no-store\r\n");
            builder.Append("Connection: close\r\n\r\n");
            byte[] headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Flush();
        }

        private static bool TryParseRange(string value, long length, out long start, out long end)
        {
            start = 0;
            end = length - 1;
            if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
            string spec = value.Substring(6).Trim();
            int comma = spec.IndexOf(',');
            if (comma >= 0) spec = spec.Substring(0, comma).Trim();
            int dash = spec.IndexOf('-');
            if (dash < 0) return false;

            string startText = spec.Substring(0, dash).Trim();
            string endText = spec.Substring(dash + 1).Trim();
            if (startText.Length == 0)
            {
                long suffix;
                if (!long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out suffix) ||
                    suffix <= 0) return false;
                if (suffix > length) suffix = length;
                start = length - suffix;
                end = length - 1;
                return true;
            }

            if (!long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
                start < 0 || start >= length) return false;

            if (endText.Length == 0)
            {
                end = length - 1;
                return true;
            }

            if (!long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out end) ||
                end < start) return false;
            if (end >= length) end = length - 1;
            return true;
        }

        private string BuildStateJson()
        {
            int count;
            lock (_trackSync)
            {
                count = _tracks.Count;
            }
            bool live = IsLive && _hub.IsConfigured;
            string seconds = (live ? Math.Max(0, LiveSeconds) : 0).ToString("0.0", CultureInfo.InvariantCulture);
            StringBuilder builder = new StringBuilder();
            builder.Append("{\"user\":\"").Append(Json(_userName)).Append("\",");
            builder.Append("\"computer\":\"").Append(Json(_computerName)).Append("\",");
            builder.Append("\"port\":").Append(Port).Append(',');
            builder.Append("\"trackCount\":").Append(count).Append(',');
            builder.Append("\"live\":").Append(live ? "true" : "false").Append(',');
            builder.Append("\"seconds\":").Append(seconds).Append(',');
            builder.Append("\"subscribers\":").Append(_hub.SubscriberCount).Append('}');
            return builder.ToString();
        }

        private string BuildTracksJson()
        {
            List<Track> snapshot;
            lock (_trackSync)
            {
                snapshot = new List<Track>(_tracks);
            }

            StringBuilder builder = new StringBuilder();
            builder.Append("{\"tracks\":[");
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (i > 0) builder.Append(',');
                Track track = snapshot[i];
                builder.Append("{\"title\":\"").Append(Json(track.Title)).Append("\",");
                builder.Append("\"artist\":\"").Append(Json(track.Artist)).Append("\",");
                builder.Append("\"ext\":\"").Append(Json(track.Badge)).Append("\",");
                builder.Append("\"album\":\"").Append(Json(track.Album)).Append("\"}");
            }
            builder.Append("]}");
            return builder.ToString();
        }

        private static string Json(string value)
        {
            if (value == null) return "";
            StringBuilder builder = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 32)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            return builder.ToString();
        }

        private static string MimeFor(string path)
        {
            string extension = Path.GetExtension(path);
            if (extension == null) return "application/octet-stream";
            switch (extension.ToLowerInvariant())
            {
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".flac": return "audio/flac";
                case ".m4a":
                case ".aac": return "audio/mp4";
                case ".ogg":
                case ".opus": return "audio/ogg";
                case ".wma": return "audio/x-ms-wma";
                case ".aiff":
                case ".aif": return "audio/aiff";
                default: return "application/octet-stream";
            }
        }

        private static IPAddress FindLanAddress()
        {
            IPAddress best = null;
            int bestScore = -1;
            try
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    NetworkInterface ni = interfaces[i];
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    IPInterfaceProperties properties;
                    try { properties = ni.GetIPProperties(); }
                    catch { continue; }

                    foreach (UnicastIPAddressInformation info in properties.UnicastAddresses)
                    {
                        IPAddress address = info.Address;
                        if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(address)) continue;
                        byte[] bytes = address.GetAddressBytes();
                        if (bytes[0] == 169 && bytes[1] == 254) continue;

                        int score = 0;
                        if (bytes[0] == 192 && bytes[1] == 168) score += 6;
                        else if (bytes[0] == 10) score += 5;
                        else if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) score += 4;
                        else score += 1;

                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 2;
                        else if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 3;
                        else score -= 1;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = address;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("LAN address detection failed", ex);
            }

            if (best != null) return best;
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(Dns.GetHostName());
                for (int i = 0; i < addresses.Length; i++)
                {
                    if (addresses[i].AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addresses[i]))
                    {
                        return addresses[i];
                    }
                }
            }
            catch { }
            return IPAddress.Loopback;
        }

        private static string FormatAddress(IPAddress address)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return "[" + address.ToString() + "]";
            }
            return address.ToString();
        }
    }
}
