using System.Windows;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        private static readonly CancellationTokenSource ShutdownTokenSource = new();
        private static int shutdownStarted;

        App()
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            // Persist first-run defaults and upgrade-added config keys loaded by Translator.
            Translator.Setting?.Save();

            StartBackgroundLoop("SyncLoop", () => Translator.SyncLoop(ShutdownTokenSource.Token));
            StartBackgroundLoop("TranslateLoop", () => Translator.TranslateLoop(ShutdownTokenSource.Token));
            StartBackgroundLoop("DisplayLoop", () => Translator.DisplayLoop(ShutdownTokenSource.Token));
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ShutdownBackgroundServices();
            base.OnExit(e);
        }

        private static void StartBackgroundLoop(string loopName, Func<Task> loop)
        {
            _ = Task.Run(() => RunLoopWithRestart(loopName, loop));
        }

        private static void StartBackgroundLoop(string loopName, Action loop)
        {
            StartBackgroundLoop(loopName, () =>
            {
                loop();
                return Task.CompletedTask;
            });
        }

        private static async Task RunLoopWithRestart(string loopName, Func<Task> loop)
        {
            while (!ShutdownTokenSource.IsCancellationRequested)
            {
                try
                {
                    await loop();
                    return;
                }
                catch (OperationCanceledException) when (ShutdownTokenSource.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"{loopName} crashed and will be restarted.", ex);
                    ShowLoopFailure(loopName, ex);

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), ShutdownTokenSource.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private static void ShowLoopFailure(string loopName, Exception exception)
        {
            try
            {
                Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        SnackbarHost.Show(
                            "[ERROR] Background loop restarted.",
                            $"{loopName}: {exception.Message}",
                            SnackbarType.Error,
                            timeout: 4,
                            closeButton: true);
                    }
                    catch (Exception snackbarException)
                    {
                        AppLogger.Warning("Failed to show loop failure snackbar.", snackbarException);
                    }
                }));
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Failed to dispatch loop failure notification.", ex);
            }
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            ShutdownBackgroundServices();
        }

        private static void ShutdownBackgroundServices()
        {
            if (Interlocked.Exchange(ref shutdownStarted, 1) != 0)
                return;

            ShutdownTokenSource.Cancel();

            try
            {
                BatchSettingsSave.CommitAllPendingChangesAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Failed to flush pending setting changes during shutdown.", ex);
            }

            try
            {
                if (Translator.Window != null)
                    LiveCaptionsHandler.KillLiveCaptions(Translator.Window);
                else
                    LiveCaptionsHandler.KillAllLiveCaptions();
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Failed to kill LiveCaptions by window handle; falling back to process cleanup.", ex);
                LiveCaptionsHandler.KillAllLiveCaptions();
            }
        }
    }
}
