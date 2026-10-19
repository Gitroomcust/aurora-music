using System;
using System.Windows;

namespace Lumen
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Log.Write("Lumen starting. User=" + Environment.UserName + ", OS=" + Environment.OSVersion);
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log.Write("Unhandled AppDomain exception", e.ExceptionObject as Exception);
            };

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += delegate(object sender,
                System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                Log.Write("Unhandled dispatcher exception", e.Exception);
            };

            try
            {
                ResourceDictionary theme = XamlLoader.LoadDictionary("Theme.xaml");
                app.Resources.MergedDictionaries.Add(theme);
            }
            catch (Exception ex)
            {
                Log.Write("Theme load failed", ex);
                MessageBox.Show("Не удалось загрузить тему приложения.\r\n\r\n" + ex.Message,
                    "Lumen", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AppState state = new AppState(args);
            try
            {
                state.Server.Start();
                Log.Write("LAN server: " + state.Server.LanUrl);
            }
            catch (Exception ex)
            {
                Log.Write("LAN server failed to start", ex);
            }

            try
            {
                MainWindow window = new MainWindow(state);
                app.MainWindow = window;
                window.Show();
                app.Run();
            }
            catch (Exception ex)
            {
                Log.Write("Fatal UI error", ex);
                MessageBox.Show("Lumen не смог запуститься.\r\n\r\n" + ex.Message,
                    "Lumen", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { state.Server.Dispose(); }
                catch { }
                Log.Write("Lumen stopped.");
            }
        }
    }
}
