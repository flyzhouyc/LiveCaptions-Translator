using System.Windows;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        App()
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            this.Exit += App_Exit;

            Task.Run(() => Translator.SyncLoop());
            Task.Run(() => Translator.TranslateLoop());
        }

        static async void OnProcessExit(object sender, EventArgs e)
        {
            try
            {
                await Translator.Cleanup();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed during application exit: {ex.Message}");
            }
        }
        
        private async void App_Exit(object sender, ExitEventArgs e)
        {
            try
            {
                await Translator.Cleanup();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed during application exit: {ex.Message}");
            }
        }
        
        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                Console.WriteLine($"[Fatal Error] Unhandled exception: {e.ExceptionObject}");
                Translator.Cleanup().Wait();
            }
            catch
            {
                // 最后的防御，确保不再抛出异常
            }
        }
        
        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                Console.WriteLine($"[Error] Dispatcher unhandled exception: {e.Exception.Message}");
                e.Handled = true; // 防止应用程序崩溃
            }
            catch
            {
                // 确保不再抛出异常
            }
        }
    }
}