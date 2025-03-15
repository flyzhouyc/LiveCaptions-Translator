using System.IO;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class SQLiteHistoryLogger
    {
        private static readonly string CONNECTION_STRING = "Data Source=translation_history.db;";
        private static SqliteConnection _sharedConnection;
        private static readonly object _connectionLock = new object();
        
        // 批量处理队列
        private static readonly ConcurrentQueue<TranslationHistoryEntry> _pendingEntries = new ConcurrentQueue<TranslationHistoryEntry>();
        private static readonly SemaphoreSlim _batchProcessingSemaphore = new SemaphoreSlim(1, 1);
        private static readonly int BATCH_SIZE = 10;
        private static readonly Timer _flushTimer;
        private static readonly TimeSpan FLUSH_INTERVAL = TimeSpan.FromSeconds(10);
        
        // 内存缓存
        private static TranslationHistoryEntry _lastEntry = null;
        private static readonly ConcurrentDictionary<long, TranslationHistoryEntry> _recentEntries = new ConcurrentDictionary<long, TranslationHistoryEntry>();
        private static readonly int MAX_RECENT_ENTRIES = 100;
        private static long _lastId = 0;

        static SQLiteHistoryLogger()
        {
            try
            {
                InitializeDatabase();
                // 启动定时刷新
                _flushTimer = new Timer(_ => FlushPendingEntries().ConfigureAwait(false), null, FLUSH_INTERVAL, FLUSH_INTERVAL);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to initialize SQLiteHistoryLogger: {ex.Message}");
            }
        }

        private static void InitializeDatabase()
        {
            try
            {
                GetConnection();

                using (var command = new SqliteCommand(@"
                    CREATE TABLE IF NOT EXISTS TranslationHistory (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Timestamp TEXT,
                        SourceText TEXT,
                        TranslatedText TEXT,
                        TargetLanguage TEXT,
                        ApiUsed TEXT
                    );
                    
                    -- Add index to improve query performance
                    CREATE INDEX IF NOT EXISTS idx_translation_timestamp ON TranslationHistory(Timestamp);
                    PRAGMA journal_mode=WAL;", GetConnection()))
                {
                    command.ExecuteNonQuery();
                }
                
                // 获取最后一条记录的ID
                using (var command = new SqliteCommand("SELECT MAX(Id) FROM TranslationHistory", GetConnection()))
                {
                    var result = command.ExecuteScalar();
                    if (result != null && result != DBNull.Value)
                    {
                        _lastId = Convert.ToInt64(result);
                    }
                }
                
                // 预加载最近记录
                LoadRecentEntries();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Database initialization failed: {ex.Message}");
                throw;
            }
        }

        private static void LoadRecentEntries()
        {
            try
            {
                using (var command = new SqliteCommand(
                    "SELECT Id, Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed " +
                    "FROM TranslationHistory ORDER BY Id DESC LIMIT @limit", GetConnection()))
                {
                    command.Parameters.AddWithValue("@limit", MAX_RECENT_ENTRIES);
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            long id = reader.GetInt64(reader.GetOrdinal("Id"));
                            string timestamp = reader.GetString(reader.GetOrdinal("Timestamp"));
                            DateTime localTime = DateTimeOffset.FromUnixTimeSeconds(
                                (long)Convert.ToDouble(timestamp)).LocalDateTime;
                                
                            var entry = new TranslationHistoryEntry
                            {
                                Timestamp = localTime.ToString("MM/dd HH:mm"),
                                TimestampFull = localTime.ToString("MM/dd/yy, HH:mm:ss"),
                                SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                                TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                                TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                                ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
                            };
                            
                            _recentEntries[id] = entry;
                            if (id > _lastId)
                                _lastId = id;
                                
                            // 记录最后一条
                            if (_lastEntry == null)
                                _lastEntry = entry;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to load recent entries: {ex.Message}");
            }
        }

        private static SqliteConnection GetConnection()
        {
            lock (_connectionLock)
            {
                if (_sharedConnection == null)
                {
                    _sharedConnection = new SqliteConnection(CONNECTION_STRING);
                    _sharedConnection.Open();
                }
                else if (_sharedConnection.State != System.Data.ConnectionState.Open)
                {
                    try
                    {
                        _sharedConnection.Open();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Error] Failed to open connection: {ex.Message}");
                        _sharedConnection.Dispose();
                        _sharedConnection = new SqliteConnection(CONNECTION_STRING);
                        _sharedConnection.Open();
                    }
                }

                return _sharedConnection;
            }
        }

        public static async Task LogTranslation(string sourceText, string translatedText,
            string targetLanguage, string apiUsed, CancellationToken token = default)
        {
            try
            {
                var entry = new TranslationHistoryEntry
                {
                    Timestamp = DateTime.Now.ToString("MM/dd HH:mm"),
                    TimestampFull = DateTime.Now.ToString("MM/dd/yy, HH:mm:ss"),
                    SourceText = sourceText,
                    TranslatedText = translatedText,
                    TargetLanguage = targetLanguage,
                    ApiUsed = apiUsed
                };
                
                // 更新最后一条记录缓存
                _lastEntry = entry;
                
                // 加入队列等待批处理
                _pendingEntries.Enqueue(entry);
                
                // 检查队列长度，达到阈值则触发批处理
                if (_pendingEntries.Count >= BATCH_SIZE)
                {
                    await FlushPendingEntries();
                }
            }
            catch (Exception ex) 
            {
                Console.WriteLine($"[Error] Failed to log translation: {ex.Message}");
            }
        }

        private static async Task FlushPendingEntries()
        {
            if (_pendingEntries.IsEmpty)
                return;
                
            // 防止多个线程同时刷新
            if (!await _batchProcessingSemaphore.WaitAsync(0))
                return;
                
            try
            {
                // 从队列中取出批量数据
                var entries = new List<TranslationHistoryEntry>();
                while (entries.Count < BATCH_SIZE && _pendingEntries.TryDequeue(out var entry))
                {
                    entries.Add(entry);
                }
                
                if (entries.Count == 0)
                    return;

                using (var transaction = GetConnection().BeginTransaction())
                {
                    string insertQuery = @"
                        INSERT INTO TranslationHistory (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed)
                        VALUES (@Timestamp, @SourceText, @TranslatedText, @TargetLanguage, @ApiUsed);
                        SELECT last_insert_rowid();";

                    using (var command = new SqliteCommand(insertQuery, GetConnection(), transaction))
                    {
                        // 重用参数对象减少对象创建 - 修复类型问题
                        var timestampParam = command.Parameters.AddWithValue("@Timestamp", "");
                        var sourceTextParam = command.Parameters.AddWithValue("@SourceText", "");
                        var translatedTextParam = command.Parameters.AddWithValue("@TranslatedText", "");
                        var targetLanguageParam = command.Parameters.AddWithValue("@TargetLanguage", "");
                        var apiUsedParam = command.Parameters.AddWithValue("@ApiUsed", "");

                        foreach (var entry in entries)
                        {
                            timestampParam.Value = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
                            sourceTextParam.Value = entry.SourceText;
                            translatedTextParam.Value = entry.TranslatedText;
                            targetLanguageParam.Value = entry.TargetLanguage;
                            apiUsedParam.Value = entry.ApiUsed;

                            long id = Convert.ToInt64(command.ExecuteScalar());
                            _lastId = id;
                            
                            // 更新内存缓存
                            _recentEntries[id] = entry;
                            if (_recentEntries.Count > MAX_RECENT_ENTRIES)
                            {
                                // 移除最早的条目
                                var oldestId = _recentEntries.Keys.Min();
                                _recentEntries.TryRemove(oldestId, out _);
                            }
                        }
                    }
                    
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to flush pending entries: {ex.Message}");
            }
            finally
            {
                _batchProcessingSemaphore.Release();
            }
        }

        public static async Task<(List<TranslationHistoryEntry>, int)> LoadHistoryAsync(
            int page, int maxRow, string searchText, CancellationToken token = default)
        {
            // 确保所有挂起的条目被写入数据库
            await FlushPendingEntries();
            
            var history = new List<TranslationHistoryEntry>();
            int maxPage = 1;
            
            try
            {
                // 计算总页数
                using (var command = new SqliteCommand(@$"SELECT COUNT() AS maxPage
                    FROM TranslationHistory
                    WHERE SourceText LIKE @SearchText OR TranslatedText LIKE @SearchText",
                    GetConnection()))
                {
                    command.Parameters.AddWithValue("@SearchText", $"%{searchText}%");
                    maxPage = (Convert.ToInt32(await command.ExecuteScalarAsync(token)) + maxRow - 1) / maxRow;
                    maxPage = Math.Max(1, maxPage);
                }

                // 查询历史记录
                using (var command = new SqliteCommand(@$"
                    SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                    FROM TranslationHistory
                    WHERE SourceText LIKE @SearchText OR TranslatedText LIKE @SearchText
                    ORDER BY Timestamp DESC
                    LIMIT @Limit OFFSET @Offset",
                    GetConnection()))
                {
                    command.Parameters.AddWithValue("@SearchText", $"%{searchText}%");
                    command.Parameters.AddWithValue("@Limit", maxRow);
                    command.Parameters.AddWithValue("@Offset", page * maxRow - maxRow);
                    
                    using (var reader = await command.ExecuteReaderAsync(token))
                    {
                        while (await reader.ReadAsync(token))
                        {
                            string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
                            DateTime localTime;
                            try
                            {
                                localTime = DateTimeOffset.FromUnixTimeSeconds((long)Convert.ToDouble(unixTime)).LocalDateTime;
                            }
                            catch (FormatException)
                            {
                                // DEPRECATED
                                await MigrateOldTimestampFormat();
                                return await LoadHistoryAsync(page, maxRow, string.Empty);
                            }
                            history.Add(new TranslationHistoryEntry
                            {
                                Timestamp = localTime.ToString("MM/dd HH:mm"),
                                TimestampFull = localTime.ToString("MM/dd/yy, HH:mm:ss"),
                                SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                                TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                                TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                                ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to load history: {ex.Message}");
            }
            
            return (history, maxPage);
        }

        public static async Task ClearHistory(CancellationToken token = default)
        {
            try
            {
                await FlushPendingEntries();
                
                string selectQuery = "DELETE FROM TranslationHistory; DELETE FROM sqlite_sequence WHERE NAME='TranslationHistory'";
                using (var command = new SqliteCommand(selectQuery, GetConnection()))
                {
                    await command.ExecuteNonQueryAsync(token);
                }
                
                // 清空缓存
                _pendingEntries.Clear();
                _recentEntries.Clear();
                _lastEntry = null;
                _lastId = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to clear history: {ex.Message}");
            }
        }

        public static async Task<string> LoadLastSourceText(CancellationToken token = default)
        {
            // 优先使用缓存
            if (_lastEntry != null)
                return _lastEntry.SourceText;

            try
            {
                string selectQuery = @"
                    SELECT SourceText
                    FROM TranslationHistory
                    ORDER BY Id DESC
                    LIMIT 1";

                using (var command = new SqliteCommand(selectQuery, GetConnection()))
                using (var reader = await command.ExecuteReaderAsync(token))
                {
                    if (await reader.ReadAsync(token))
                        return reader.GetString(reader.GetOrdinal("SourceText"));
                    else
                        return string.Empty;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to load last source text: {ex.Message}");
                return string.Empty;
            }
        }

        public static async Task<TranslationHistoryEntry?> LoadLastTranslation(CancellationToken token = default)
        {
            // 优先使用缓存
            if (_lastEntry != null)
                return _lastEntry;

            try
            {
                string selectQuery = @"
                    SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                    FROM TranslationHistory
                    ORDER BY Id DESC
                    LIMIT 1";

                using (var command = new SqliteCommand(selectQuery, GetConnection()))
                using (var reader = await command.ExecuteReaderAsync(token))
                {
                    if (await reader.ReadAsync(token))
                    {
                        string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
                        DateTime localTime = DateTimeOffset.FromUnixTimeSeconds((long)Convert.ToDouble(unixTime)).LocalDateTime;
                        
                        var entry = new TranslationHistoryEntry
                        {
                            Timestamp = localTime.ToString("MM/dd HH:mm"),
                            TimestampFull = localTime.ToString("MM/dd/yy, HH:mm:ss"),
                            SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                            TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                            TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                            ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
                        };
                        
                        // 更新缓存
                        _lastEntry = entry;
                        
                        return entry;
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to load last translation: {ex.Message}");
                return null;
            }
        }

        public static async Task DeleteLastTranslation(CancellationToken token = default)
        {
            try
            {
                await FlushPendingEntries();
                
                using (var command = new SqliteCommand(@"
                    DELETE FROM TranslationHistory
                    WHERE Id IN (SELECT Id FROM TranslationHistory ORDER BY Id DESC LIMIT 1)",
                    GetConnection()))
                {
                    await command.ExecuteNonQueryAsync(token);
                }
                
                // 更新缓存
                if (_recentEntries.TryRemove(_lastId, out _))
                {
                    _lastId--;
                    if (_recentEntries.TryGetValue(_lastId, out var entry))
                    {
                        _lastEntry = entry;
                    }
                    else
                    {
                        _lastEntry = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to delete last translation: {ex.Message}");
            }
        }

        public static async Task ExportToCSV(string filePath, CancellationToken token = default)
        {
            try
            {
                await FlushPendingEntries();
                
                var history = new List<TranslationHistoryEntry>();

                string selectQuery = @"
                    SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                    FROM TranslationHistory
                    ORDER BY Timestamp DESC";

                using (var command = new SqliteCommand(selectQuery, GetConnection()))
                using (var reader = await command.ExecuteReaderAsync(token))
                {
                    while (await reader.ReadAsync(token))
                    {
                        string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
                        DateTime localTime = DateTimeOffset.FromUnixTimeSeconds((long)Convert.ToDouble(unixTime)).LocalDateTime;
                        history.Add(new TranslationHistoryEntry
                        {
                            Timestamp = localTime.ToString("MM/dd HH:mm"),
                            TimestampFull = localTime.ToString("MM/dd/yy, HH:mm:ss"),
                            SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                            TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                            TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                            ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
                        });
                    }
                }

                // 使用批量写入提高大文件导出性能
                using (var streamWriter = new StreamWriter(filePath, false, Encoding.UTF8))
                {
                    await streamWriter.WriteLineAsync("Timestamp,SourceText,TranslatedText,TargetLanguage,ApiUsed");
                    
                    foreach (var batch in history.Chunk(500)) // 每批500条
                    {
                        var csvLines = new StringBuilder();
                        foreach (var entry in batch)
                        {
                            // 转义CSV字段
                            string sourceText = entry.SourceText.Replace("\"", "\"\"");
                            string translatedText = entry.TranslatedText.Replace("\"", "\"\"");
                            
                            csvLines.AppendLine(
                                $"\"{entry.Timestamp}\",\"{sourceText}\",\"{translatedText}\"," +
                                $"\"{entry.TargetLanguage}\",\"{entry.ApiUsed}\"");
                        }
                        await streamWriter.WriteAsync(csvLines.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to export to CSV: {ex.Message}");
                throw;
            }
        }

        // DEPRECATED
        private static async Task MigrateOldTimestampFormat()
        {
            var records = new List<(long id, string timestamp)>();
            using (var command = new SqliteCommand("SELECT Id, Timestamp FROM TranslationHistory", GetConnection()))
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    long id = reader.GetInt64(reader.GetOrdinal("Id"));
                    string timestamp = reader.GetString(reader.GetOrdinal("Timestamp"));
                    records.Add((id, timestamp));
                }
            }

            // 使用事务优化批量更新
            using (var transaction = GetConnection().BeginTransaction())
            {
                using (var updateCommand = new SqliteCommand(
                    "UPDATE TranslationHistory SET Timestamp = @Timestamp WHERE Id = @Id",
                    GetConnection(), transaction))
                {
                    // 修复参数类型问题
                    var idParam = updateCommand.Parameters.AddWithValue("@Id", 0);
                    var timestampParam = updateCommand.Parameters.AddWithValue("@Timestamp", "");
                    
                    foreach (var (id, timestamp) in records)
                    {
                        if (DateTime.TryParse(timestamp, out DateTime dt))
                        {
                            idParam.Value = id;
                            timestampParam.Value = ((DateTimeOffset)dt).ToUnixTimeSeconds().ToString();
                            await updateCommand.ExecuteNonQueryAsync();
                        }
                    }
                }
                
                transaction.Commit();
            }
            
            // 刷新缓存
            _recentEntries.Clear();
            _lastEntry = null;
            LoadRecentEntries();
        }
        
        // 添加清理方法，应用退出时调用
        public static async Task Cleanup()
        {
            try
            {
                await FlushPendingEntries();
                
                lock (_connectionLock)
                {
                    if (_sharedConnection != null && _sharedConnection.State == System.Data.ConnectionState.Open)
                    {
                        _sharedConnection.Close();
                        _sharedConnection.Dispose();
                        _sharedConnection = null;
                    }
                }
                
                _flushTimer?.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Failed to cleanup SQLiteHistoryLogger: {ex.Message}");
            }
        }
    }
}