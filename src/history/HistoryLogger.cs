﻿using System;
using System.Collections.Generic;
using System.Data.SQLite;

namespace LiveCaptionsTranslator.models
{
    public class TranslationHistoryEntry
    {
        public string Timestamp { get; set; }
        public string SourceText { get; set; }
        public string TranslatedText { get; set; }
        public string TargetLanguage { get; set; }
        public string ApiUsed { get; set; }
    }

    public static class SQLiteHistoryLogger
    {
        private static readonly string ConnectionString = "Data Source=translation_history.db;Version=3;";

        static SQLiteHistoryLogger()
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();
                string createTableQuery = @"
                    CREATE TABLE IF NOT EXISTS TranslationHistory (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Timestamp TEXT,
                        SourceText TEXT,
                        TranslatedText TEXT,
                        TargetLanguage TEXT,
                        ApiUsed TEXT
                    )";
                using (var command = new SQLiteCommand(createTableQuery, connection))
                {
                    command.ExecuteNonQuery();
                }
            }
        }

        public static async Task LogTranslationAsync(string sourceText, string translatedText, string targetLanguage,
            string apiUsed)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                string insertQuery = @"
                    INSERT INTO TranslationHistory (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed)
                    VALUES (@Timestamp, @SourceText, @TranslatedText, @TargetLanguage, @ApiUsed)"
                ;

                using (var command = new SQLiteCommand(insertQuery, connection))
                {
                    command.Parameters.AddWithValue("@Timestamp", DateTime.Now.ToString("MM/dd HH:mm"));
                    command.Parameters.AddWithValue("@SourceText", sourceText);
                    command.Parameters.AddWithValue("@TranslatedText", translatedText);
                    command.Parameters.AddWithValue("@TargetLanguage", targetLanguage);
                    command.Parameters.AddWithValue("@ApiUsed", apiUsed);
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        public static async Task<(List<TranslationHistoryEntry>, int)> LoadHistoryAsync(int page, int maxRow)
        {
            var history = new List<TranslationHistoryEntry>();
            int maxPage = 1;

            using (var connection = new SQLiteConnection(ConnectionString))
            {
                await connection.OpenAsync();

                // Get max page
                using (var command = new SQLiteCommand("SELECT COUNT() AS maxPage FROM TranslationHistory", connection))
                    maxPage = Convert.ToInt32(command.ExecuteScalar()) / maxRow;

                // Get table
                using (var command = new SQLiteCommand(@"
                    SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed
                    FROM TranslationHistory
                    ORDER BY Id DESC
                    LIMIT " + maxRow + " OFFSET " + ((page * maxRow) - maxRow), connection))
                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        history.Add(new TranslationHistoryEntry
                        {
                            Timestamp = reader.GetString(reader.GetOrdinal("Timestamp")),
                            SourceText = reader.GetString(reader.GetOrdinal("SourceText")),
                            TranslatedText = reader.GetString(reader.GetOrdinal("TranslatedText")),
                            TargetLanguage = reader.GetString(reader.GetOrdinal("TargetLanguage")),
                            ApiUsed = reader.GetString(reader.GetOrdinal("ApiUsed"))
                        });
                    }
                }
            }
            return (history, maxPage);
        }

        public static async Task ClearHistory()
        {
            await Task.Run(() =>
            {
                using var connection = new SQLiteConnection(ConnectionString);
                connection.Open();
                using var command = new SQLiteCommand("DELETE FROM TranslationHistory", connection);
                command.ExecuteNonQuery();
            });
        }
    }
}