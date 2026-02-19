using Microsoft.Data.Sqlite;

namespace VibeMMO.Server;

internal readonly record struct PersistedPlayerRecord(
    byte[] ReconnectToken,
    string DisplayName,
    float PositionX,
    float PositionY,
    int Coins,
    DateTimeOffset LastSeenUtc);

internal sealed class SqlitePlayerStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqlitePlayerStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={fullPath}");
        _connection.Open();
        EnsureSchema();
    }

    public bool TryLoadByReconnectToken(byte[] reconnectToken, out PersistedPlayerRecord record)
    {
        record = default;
        if (reconnectToken.Length == 0)
        {
            return false;
        }

        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT display_name, x, y, coins, last_seen_utc
            FROM players
            WHERE reconnect_token = $token
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$token", reconnectToken);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var displayName = reader.GetString(0);
        var x = reader.GetFloat(1);
        var y = reader.GetFloat(2);
        var coins = reader.GetInt32(3);
        var lastSeen = DateTimeOffset.Parse(reader.GetString(4));
        record = new PersistedPlayerRecord(reconnectToken, displayName, x, y, coins, lastSeen);
        return true;
    }

    public void Save(PersistedPlayerRecord record)
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO players (reconnect_token, display_name, x, y, coins, last_seen_utc)
            VALUES ($token, $name, $x, $y, $coins, $lastSeenUtc)
            ON CONFLICT(reconnect_token) DO UPDATE SET
                display_name = excluded.display_name,
                x = excluded.x,
                y = excluded.y,
                coins = excluded.coins,
                last_seen_utc = excluded.last_seen_utc
            """;
        command.Parameters.AddWithValue("$token", record.ReconnectToken);
        command.Parameters.AddWithValue("$name", record.DisplayName);
        command.Parameters.AddWithValue("$x", record.PositionX);
        command.Parameters.AddWithValue("$y", record.PositionY);
        command.Parameters.AddWithValue("$coins", record.Coins);
        command.Parameters.AddWithValue("$lastSeenUtc", record.LastSeenUtc.UtcDateTime.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private void EnsureSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS players (
                reconnect_token BLOB PRIMARY KEY,
                display_name TEXT NOT NULL,
                x REAL NOT NULL,
                y REAL NOT NULL,
                coins INTEGER NOT NULL DEFAULT 0,
                last_seen_utc TEXT NOT NULL
            )
            """;
        command.ExecuteNonQuery();

        EnsureColumnExists("players", "coins", "INTEGER NOT NULL DEFAULT 0");
    }

    private void EnsureColumnExists(string tableName, string columnName, string columnDefinition)
    {
        using var check = _connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            var existingName = reader.GetString(1);
            if (string.Equals(existingName, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        try
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
}
