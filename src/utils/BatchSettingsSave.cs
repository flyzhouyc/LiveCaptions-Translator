using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LiveCaptionsTranslator.utils
{
    public class BatchSettingsSave
    {
        private static readonly SemaphoreSlim saveLocker = new(1, 1);
        private static readonly Timer batchTimer;
        private static readonly object batchLock = new();
        private static readonly Dictionary<string, object> pendingChanges = new();
        private static readonly TimeSpan BatchSaveDelay = TimeSpan.FromMilliseconds(500);
        private static bool saveScheduled;

        static BatchSettingsSave()
        {
            batchTimer = new Timer(ProcessBatchSave, null, Timeout.Infinite, Timeout.Infinite);
        }

        public static void AddChange(string propertyPath, object value)
        {
            lock (batchLock)
            {
                pendingChanges[propertyPath] = value;

                if (!saveScheduled)
                {
                    saveScheduled = true;
                    batchTimer.Change(BatchSaveDelay, Timeout.InfiniteTimeSpan);
                }
            }
        }

        private static void ProcessBatchSave(object? state)
        {
            try
            {
                lock (batchLock)
                {
                    if (pendingChanges.Count == 0)
                    {
                        saveScheduled = false;
                        return;
                    }

                    pendingChanges.Clear();
                    saveScheduled = false;
                }

                if (Translator.Setting != null)
                    _ = Task.Run(CommitChangesAsync);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to batch-save settings: {ex.Message}");
            }
        }

        public static async Task CommitChangesAsync()
        {
            if (Translator.Setting == null)
                return;

            try
            {
                await saveLocker.WaitAsync();
                await Task.Run(() => Translator.Setting.Save());
            }
            finally
            {
                saveLocker.Release();
            }
        }

        public static async Task CommitAllPendingChangesAsync()
        {
            if (Translator.Setting == null)
                return;

            lock (batchLock)
            {
                pendingChanges.Clear();
                saveScheduled = false;
                batchTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }

            try
            {
                await saveLocker.WaitAsync();
                await Task.Run(() => Translator.Setting.Save());
            }
            finally
            {
                saveLocker.Release();
            }
        }
    }
}
