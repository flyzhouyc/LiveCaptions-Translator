using System.Globalization;
using System.IO;
using System.Text;
using CsvHelper;
using Microsoft.Data.Sqlite;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class SQLiteHistoryLogger
    {
        public static readonly string CONNECTION_STRING = "Data Source=translation_history.db;";

        static SQLiteHistoryLogger()
        {
            InitializeDatabase();
        }

        private static SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(CONNECTION_STRING);
            connection.Open();
            return connection;
        }

        private static void InitializeDatabase()
        {
            using var connection = OpenConnection();
            using var command = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS TranslationHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT,
                    SourceText TEXT,
                    TranslatedText TEXT,
                    TargetLanguage TEXT,
                    ApiUsed TEXT
                );", connection);
            command.ExecuteNonQuery();
        }

        public static async Task LogTranslation(string sourceText, string translatedText,
            string targetLanguage, string apiUsed, CancellationToken token = default)
        {
            const string insertQuery = @"
                INSERT INTO TranslationHistory (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed)
                VALUES (@Timestamp, @SourceText, @TranslatedText, @TargetLanguage, @ApiUsed)";

            using var connection = OpenConnection();
            using var command = new SqliteCommand(insertQuery, connection);
            command.Parameters.AddWithValue("@Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@SourceText", sourceText);
            command.Parameters.AddWithValue("@TranslatedText", translatedText);
            command.Parameters.AddWithValue("@TargetLanguage", targetLanguage);
            command.Parameters.AddWithValue("@ApiUsed", apiUsed);
            await command.ExecuteNonQueryAsync(token);
        }

        public static async Task<(List<TranslationHistoryEntry>, int)> LoadHistoryAsync(
            int page, int maxRow, string searchText, CancellationToken token = default)
        {
            var history = new List<TranslationHistoryEntry>();
            int totalCount;
            bool requiresTimestampMigration = false;

            using var connection = OpenConnection();
            using (var command = new SqliteCommand(@"
                SELECT COUNT(*) 
                FROM TranslationHistory
                WHERE SourceText LIKE @search OR TranslatedText LIKE @search", connection))
            {
                command.Parameters.AddWithValue("@search", $"%{searchText}%");
                totalCount = Convert.ToInt32(await command.ExecuteScalarAsync(token));
            }

            int maxPage = Math.Max(1, (int)Math.Ceiling(totalCount / (double)maxRow));
            int offset = Math.Max(0, (page - 1) * maxRow);

            using (var command = new SqliteCommand(@"
                SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                FROM TranslationHistory
                WHERE SourceText LIKE @search OR TranslatedText LIKE @search
                ORDER BY Timestamp DESC
                LIMIT @maxRow OFFSET @offset", connection))
            {
                command.Parameters.AddWithValue("@search", $"%{searchText}%");
                command.Parameters.AddWithValue("@maxRow", maxRow);
                command.Parameters.AddWithValue("@offset", offset);

                using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
                    DateTime localTime;
                    try
                    {
                        localTime = DateTimeOffset.FromUnixTimeSeconds(
                            long.Parse(unixTime, CultureInfo.InvariantCulture)).LocalDateTime;
                    }
                    catch (FormatException)
                    {
                        requiresTimestampMigration = true;
                        break;
                    }

                    history.Add(new TranslationHistoryEntry
                    {
                        Timestamp = localTime.ToString("yyyy-MM-dd HH:mm"),
                        TimestampFull = localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                        TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                        TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                        ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
                    });
                }
            }

            if (requiresTimestampMigration)
            {
                await MigrateOldTimestampFormat(token);
                return await LoadHistoryAsync(page, maxRow, searchText, token);
            }

            return (history, maxPage);
        }

        public static async Task ClearHistory(CancellationToken token = default)
        {
            const string selectQuery = "DELETE FROM TranslationHistory; DELETE FROM sqlite_sequence WHERE NAME='TranslationHistory'";
            using var connection = OpenConnection();
            using var command = new SqliteCommand(selectQuery, connection);
            await command.ExecuteNonQueryAsync(token);
        }

        public static async Task<string> LoadLastSourceText(CancellationToken token = default)
        {
            const string selectQuery = @"
                SELECT SourceText
                FROM TranslationHistory
                ORDER BY Id DESC
                LIMIT 1";

            using var connection = OpenConnection();
            using var command = new SqliteCommand(selectQuery, connection);
            using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token)
                ? reader.GetString(reader.GetOrdinal("SourceText"))
                : string.Empty;
        }

        public static async Task<TranslationHistoryEntry?> LoadLastTranslation(CancellationToken token = default)
        {
            const string selectQuery = @"
                SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                FROM TranslationHistory
                ORDER BY Id DESC
                LIMIT 1";

            using var connection = OpenConnection();
            using var command = new SqliteCommand(selectQuery, connection);
            using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return null;

            string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
            DateTime localTime = DateTimeOffset.FromUnixTimeSeconds(
                long.Parse(unixTime, CultureInfo.InvariantCulture)).LocalDateTime;
            return new TranslationHistoryEntry
            {
                Timestamp = localTime.ToString("yyyy-MM-dd HH:mm"),
                TimestampFull = localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
            };
        }

        public static async Task DeleteLastTranslation(CancellationToken token = default)
        {
            using var connection = OpenConnection();
            using var command = new SqliteCommand(@"
                DELETE FROM TranslationHistory
                WHERE Id IN (SELECT Id FROM TranslationHistory ORDER BY Id DESC LIMIT 1)",
                connection);
            await command.ExecuteNonQueryAsync(token);
        }

        public static async Task ExportToCSV(string filePath, CancellationToken token = default)
        {
            var history = new List<TranslationHistoryEntry>();

            const string selectQuery = @"
                SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                FROM TranslationHistory
                ORDER BY Timestamp DESC";

            using (var connection = OpenConnection())
            using (var command = new SqliteCommand(selectQuery, connection))
            using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    string unixTime = reader.GetString(reader.GetOrdinal("Timestamp"));
                    DateTime localTime = DateTimeOffset.FromUnixTimeSeconds(
                        long.Parse(unixTime, CultureInfo.InvariantCulture)).LocalDateTime;
                    history.Add(new TranslationHistoryEntry
                    {
                        Timestamp = localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        TimestampFull = localTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                        TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                        TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                        ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
                    });
                }
            }

            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
            using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
            await csvWriter.WriteRecordsAsync(history, token);
        }

        private static async Task MigrateOldTimestampFormat(CancellationToken token = default)
        {
            var records = new List<(long id, string timestamp)>();

            using var connection = OpenConnection();
            using (var command = new SqliteCommand("SELECT Id, Timestamp FROM TranslationHistory", connection))
            using (var reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    long id = reader.GetInt64(reader.GetOrdinal("Id"));
                    string timestamp = reader.GetString(reader.GetOrdinal("Timestamp"));
                    records.Add((id, timestamp));
                }
            }

            foreach (var (id, timestamp) in records)
            {
                if (!DateTime.TryParse(timestamp, out DateTime dt))
                    continue;

                long unixTime = ((DateTimeOffset)dt).ToUnixTimeSeconds();
                using var updateCommand = new SqliteCommand(
                    "UPDATE TranslationHistory SET Timestamp = @Timestamp WHERE Id = @Id",
                    connection);
                updateCommand.Parameters.AddWithValue("@Id", id);
                updateCommand.Parameters.AddWithValue("@Timestamp", unixTime.ToString(CultureInfo.InvariantCulture));
                await updateCommand.ExecuteNonQueryAsync(token);
            }
        }
    }
}
