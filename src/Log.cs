using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Lumen
{
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static string _path;

        public static string Path
        {
            get
            {
                if (_path == null)
                {
                    _path = ChoosePath();
                }
                return _path;
            }
        }

        private static string ChoosePath()
        {
            try
            {
                string directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Lumen");
                Directory.CreateDirectory(directory);
                string probe = System.IO.Path.Combine(directory, "write.test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return System.IO.Path.Combine(directory, "lumen.log");
            }
            catch { }

            try
            {
                string exeDirectory = System.IO.Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(exeDirectory))
                {
                    string probe = System.IO.Path.Combine(exeDirectory, "write.test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return System.IO.Path.Combine(exeDirectory, "lumen.log");
                }
            }
            catch { }

            try
            {
                return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lumen.log");
            }
            catch
            {
                return "lumen.log";
            }
        }

        public static void Write(string message)
        {
            Write(message, null);
        }

        public static void Write(string message, Exception exception)
        {
            try
            {
                lock (Sync)
                {
                    string path = Path;
                    try
                    {
                        FileInfo info = new FileInfo(path);
                        if (info.Exists && info.Length > 1024 * 1024)
                        {
                            File.Delete(path);
                        }
                    }
                    catch { }

                    StringBuilder builder = new StringBuilder();
                    builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    builder.Append("  ");
                    builder.Append(message);
                    if (exception != null)
                    {
                        builder.AppendLine();
                        builder.Append(exception.ToString());
                    }
                    builder.AppendLine();
                    File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never crash the app.
            }
        }
    }
}
