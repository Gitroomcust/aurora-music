using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Lumen
{
    internal sealed class AppSettings
    {
        public string FolderPath = "";
        public double Volume = 0.70;

        private static string _settingsPath;

        private static string SettingsPath
        {
            get
            {
                if (_settingsPath == null) _settingsPath = ChoosePath();
                return _settingsPath;
            }
        }

        private static string ChoosePath()
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Lumen");
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, "write.test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return Path.Combine(directory, "settings.ini");
            }
            catch { }

            try
            {
                string exeDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(exeDirectory))
                {
                    string probe = Path.Combine(exeDirectory, "write.test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return Path.Combine(exeDirectory, "settings.ini");
                }
            }
            catch { }

            return Path.Combine(Path.GetTempPath(), "lumen-settings.ini");
        }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            try
            {
                string path = SettingsPath;
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    foreach (string raw in lines)
                    {
                        if (raw == null) continue;
                        int split = raw.IndexOf('=');
                        if (split <= 0) continue;
                        string key = raw.Substring(0, split).Trim();
                        string value = raw.Substring(split + 1).Trim();
                        if (string.Equals(key, "folder", StringComparison.OrdinalIgnoreCase))
                        {
                            settings.FolderPath = value;
                        }
                        else if (string.Equals(key, "volume", StringComparison.OrdinalIgnoreCase))
                        {
                            double volume;
                            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out volume))
                            {
                                settings.Volume = Math.Max(0, Math.Min(1, volume));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Settings load failed", ex);
            }
            return settings;
        }

        public void Save()
        {
            try
            {
                string path = SettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("folder=" + FolderPath);
                builder.AppendLine("volume=" + Volume.ToString("0.###", CultureInfo.InvariantCulture));
                File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log.Write("Settings save failed", ex);
            }
        }
    }
}
