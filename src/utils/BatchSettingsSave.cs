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
                Dictionary<string, object> changesToApply;

                lock (batchLock)
                {
                    changesToApply = new Dictionary<string, object>(pendingChanges);
                    pendingChanges.Clear();
                    saveScheduled = false;
                }

                if (changesToApply.Count > 0 && Translator.Setting != null)
                    _ = Task.Run(async () => await CommitChangesAsync(changesToApply));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to batch-save settings: {ex.Message}");
            }
        }

        public static async Task CommitChangesAsync(Dictionary<string, object>? changes = null)
        {
            if (Translator.Setting == null)
                return;

            if (changes == null)
            {
                lock (batchLock)
                {
                    if (pendingChanges.Count == 0)
                        return;

                    changes = new Dictionary<string, object>(pendingChanges);
                    pendingChanges.Clear();
                    saveScheduled = false;
                }
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

        public static async Task CommitAllPendingChangesAsync()
        {
            await CommitChangesAsync();
        }
    }
}
