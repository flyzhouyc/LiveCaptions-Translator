using System;
using System.Collections.Concurrent;
using System.Data.SQLite;
using System.Threading.Tasks;
using System.IO;
using System.Threading;

namespace LiveCaptionsTranslator.models
{
    public class PersistentCacheEntry
    {
        public string TranslatedText { get; private set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastAccessTime { get; set; }
        public int AccessCount { get; set; }
        public TimeSpan ExpirationTime { get; private set; }
        public bool IsExpired => DateTime.Now - CreatedAt > ExpirationTime;

        public PersistentCacheEntry(string translatedText, TimeSpan expirationTime)
        {
            TranslatedText = translatedText;
            ExpirationTime = expirationTime;
            CreatedAt = DateTime.Now;
            LastAccessTime = DateTime.Now;
            AccessCount = 1;
        }

        public void IncrementAccess()
        {
            LastAccessTime = DateTime.Now;
            AccessCount++;
        }
    }

    public class PersistentCache
    {
        private readonly string _dbPath;
        private readonly ConcurrentDictionary<string, PersistentCacheEntry> _memoryCache;
        private readonly SemaphoreSlim _dbLock = new(1);
        private readonly Timer _persistTimer;
        private const int MAX_MEMORY_CACHE_SIZE = 500;
        private const int CLEANUP_THRESHOLD = 400;
        private DateTime _lastCleanup = DateTime.Now;

        public PersistentCache(string dbPath = "translation_cache.db")
        {
            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dbPath);
            _memoryCache = new ConcurrentDictionary<string, PersistentCacheEntry>();
            InitializeDatabase();
            LoadCacheFromDb();

            // 设置定期持久化定时器 (每5分钟)
            _persistTimer = new Timer(PersistCacheCallback, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        private void InitializeDatabase()
        {
            if (!File.Exists(_dbPath))
            {
                SQLiteConnection.CreateFile(_dbPath);
            }

            using var connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
            connection.Open();
            using var command = new SQLiteCommand(
                @"CREATE TABLE IF NOT EXISTS translations (
                    source_text TEXT PRIMARY KEY,
                    translated_text TEXT NOT NULL,
                    created_at DATETIME NOT NULL,
                    last_access DATETIME NOT NULL,
                    access_count INTEGER NOT NULL,
                    expiration_time INTEGER NOT NULL
                )", connection);
            command.ExecuteNonQuery();
        }

        private void LoadCacheFromDb()
        {
            using var connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
            connection.Open();
            using var command = new SQLiteCommand(
                "SELECT * FROM translations WHERE datetime(last_access) > datetime('now', '-1 day')", 
                connection);
            
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var sourceText = reader.GetString(0);
                var entry = new PersistentCacheEntry(
                    reader.GetString(1),
                    TimeSpan.FromSeconds(reader.GetInt32(5)))
                {
                    CreatedAt = DateTime.Parse(reader.GetString(2)),
                    LastAccessTime = DateTime.Parse(reader.GetString(3)),
                    AccessCount = reader.GetInt32(4)
                };

                if (!entry.IsExpired)
                {
                    _memoryCache.TryAdd(sourceText, entry);
                }
            }
        }

        private async void PersistCacheCallback(object state)
        {
            await PersistCacheToDb();
        }

        private async Task PersistCacheToDb()
        {
            if (!await _dbLock.WaitAsync(TimeSpan.FromSeconds(1)))
                return;

            try
            {
                using var connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
                await connection.OpenAsync();
                using var transaction = connection.BeginTransaction();

                foreach (var (sourceText, entry) in _memoryCache)
                {
                    if (entry.IsExpired) continue;

                    using var command = new SQLiteCommand(
                        @"INSERT OR REPLACE INTO translations 
                        (source_text, translated_text, created_at, last_access, access_count, expiration_time)
                        VALUES (@source, @translated, @created, @lastAccess, @count, @expiration)", connection);

                    command.Parameters.AddWithValue("@source", sourceText);
                    command.Parameters.AddWithValue("@translated", entry.TranslatedText);
                    command.Parameters.AddWithValue("@created", entry.CreatedAt.ToString("O"));
                    command.Parameters.AddWithValue("@lastAccess", entry.LastAccessTime.ToString("O"));
                    command.Parameters.AddWithValue("@count", entry.AccessCount);
                    command.Parameters.AddWithValue("@expiration", (int)entry.ExpirationTime.TotalSeconds);

                    await command.ExecuteNonQueryAsync();
                }

                transaction.Commit();
            }
            finally
            {
                _dbLock.Release();
            }
        }

        private TimeSpan GetDynamicExpirationTime(string text)
        {
            return text.Length switch
            {
                < 30 => TimeSpan.FromHours(24),   // 短文本保留更长时间
                < 100 => TimeSpan.FromHours(12),
                < 300 => TimeSpan.FromHours(6),
                _ => TimeSpan.FromHours(3)        // 长文本更快过期
            };
        }

        private async Task CleanupCacheIfNeeded()
        {
            if (_memoryCache.Count < CLEANUP_THRESHOLD || 
                DateTime.Now - _lastCleanup < TimeSpan.FromMinutes(5))
                return;

            if (await _dbLock.WaitAsync(0))
            {
                try
                {
                    // 清理内存缓存
                    var expiredKeys = _memoryCache
                        .Where(kvp => kvp.Value.IsExpired)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var key in expiredKeys)
                    {
                        _memoryCache.TryRemove(key, out _);
                    }

                    // 如果仍然超过阈值，根据访问时间和频率移除
                    if (_memoryCache.Count > CLEANUP_THRESHOLD)
                    {
                        var leastUsed = _memoryCache
                            .OrderBy(kvp => kvp.Value.LastAccessTime)
                            .ThenBy(kvp => kvp.Value.AccessCount)
                            .Take(_memoryCache.Count - CLEANUP_THRESHOLD)
                            .Select(kvp => kvp.Key)
                            .ToList();

                        foreach (var key in leastUsed)
                        {
                            _memoryCache.TryRemove(key, out _);
                        }
                    }

                    // 清理数据库中的过期记录
                    using var connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
                    await connection.OpenAsync();
                    using var command = new SQLiteCommand(
                        "DELETE FROM translations WHERE datetime(last_access) <= datetime('now', '-7 days')",
                        connection);
                    await command.ExecuteNonQueryAsync();

                    _lastCleanup = DateTime.Now;
                }
                finally
                {
                    _dbLock.Release();
                }
            }
        }

        public async Task<string> GetOrTranslateAsync(string text, Func<string, Task<string>> translateFunc)
        {
            // 尝试从内存缓存获取
            if (_memoryCache.TryGetValue(text, out var entry))
            {
                if (!entry.IsExpired)
                {
                    entry.IncrementAccess();
                    return entry.TranslatedText;
                }
                _memoryCache.TryRemove(text, out _);
            }

            // 尝试从数据库获取
            if (await _dbLock.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                try
                {
                    using var connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
                    await connection.OpenAsync();
                    using var command = new SQLiteCommand(
                        "SELECT translated_text, created_at, last_access, access_count, expiration_time FROM translations WHERE source_text = @text",
                        connection);
                    command.Parameters.AddWithValue("@text", text);

                    using var reader = await command.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        var dbEntry = new PersistentCacheEntry(
                            reader.GetString(0),
                            TimeSpan.FromSeconds(reader.GetInt32(4)))
                        {
                            CreatedAt = DateTime.Parse(reader.GetString(1)),
                            LastAccessTime = DateTime.Parse(reader.GetString(2)),
                            AccessCount = reader.GetInt32(3)
                        };

                        if (!dbEntry.IsExpired)
                        {
                            dbEntry.IncrementAccess();
                            _memoryCache[text] = dbEntry;
                            return dbEntry.TranslatedText;
                        }
                    }
                }
                finally
                {
                    _dbLock.Release();
                }
            }

            // 异步清理缓存
            _ = CleanupCacheIfNeeded();

            // 执行翻译
            var translatedText = await translateFunc(text);
            var newEntry = new PersistentCacheEntry(translatedText, GetDynamicExpirationTime(text));
            _memoryCache[text] = newEntry;

            // 异步保存到数据库
            _ = PersistCacheToDb();

            return translatedText;
        }

        public async ValueTask DisposeAsync()
        {
            _persistTimer.Dispose();
            await PersistCacheToDb();
        }
    }
}
