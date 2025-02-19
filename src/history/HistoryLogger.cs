﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Reflection.PortableExecutable;

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
            VALUES (@Timestamp, @SourceText, @TranslatedText, @TargetLanguage, @ApiUsed)";

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
                string selectQuery = "SELECT COUNT() AS maxPage FROM TranslationHistory";
                using (var command = new SQLiteCommand(selectQuery, connection))
                maxPage = Convert.ToInt32(command.ExecuteScalar()) / maxRow;

                // Get table
                selectQuery = "SELECT Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed FROM TranslationHistory ORDER BY Id DESC LIMIT " + maxRow + " OFFSET " + ((page * maxRow) - maxRow);
                using (var command = new SQLiteCommand(selectQuery, connection))
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

        public static async Task ClaerHistory()
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                string selectQuery = "DELETE FROM TranslationHistory";
                using (var command = new SQLiteCommand(selectQuery, connection))
                {
                    command.ExecuteNonQuery();
                }

            }
        }
    }
}