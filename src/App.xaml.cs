﻿﻿﻿using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator
{
    public partial class App : Application, IDisposable
    {
        private static AutomationElement? window = null;
        private static Caption? captions = null;
        private static Setting? settings = null;
        private CancellationTokenSource? _syncCts;
        private CancellationTokenSource? _translateCts;
        private bool _disposed;
        private readonly object _taskLock = new object();

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Captions
        {
            get => captions;
        }
        public static Setting? Settings
        {
            get => settings;
        }

        App()
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

            window = LiveCaptionsHandler.LaunchLiveCaptions();
            captions = Caption.GetInstance();
            settings = Setting.Load();

            // Initialize caption provider based on current API setting
            InitializeCaptionTasks();
        }

        private void InitializeCaptionTasks()
        {
            lock (_taskLock)
            {
                try
                {
                    // Cancel existing tasks if any
                    CancelCaptionTasks();

                    // Initialize provider
                    captions?.InitializeProvider(settings?.ApiName ?? "OpenAI");

                    // Create new cancellation tokens
                    _syncCts = new CancellationTokenSource();
                    _translateCts = new CancellationTokenSource();

                    // Start new tasks
                    Task.Run(async () => await RunCaptionSyncAsync(_syncCts.Token));
                    Task.Run(async () => await RunTranslationAsync(_translateCts.Token));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error initializing caption tasks: {ex.Message}");
                }
            }
        }

        public void RestartCaptionTasks()
        {
            InitializeCaptionTasks();
        }

        private void CancelCaptionTasks()
        {
            lock (_taskLock)
            {
                try
                {
                    _syncCts?.Cancel();
                    _translateCts?.Cancel();

                    _syncCts?.Dispose();
                    _translateCts?.Dispose();

                    _syncCts = null;
                    _translateCts = null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error canceling caption tasks: {ex.Message}");
                }
            }
        }

        private async Task RunCaptionSyncAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Captions != null)
                {
                    await Captions.SyncAsync();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown, no action needed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Caption sync error: {ex.Message}");
            }
        }

        private async Task RunTranslationAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (Captions != null)
                {
                    await Captions.TranslateAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal shutdown, no action needed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Translation error: {ex.Message}");
            }
        }

        private void OnProcessExit(object sender, EventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                CancelCaptionTasks();
                
                if (Captions != null)
                {
                    ((IDisposable)Captions).Dispose();
                }
                
                LiveCaptionsHandler.KillLiveCaptions();
            }
        }
    }
}
