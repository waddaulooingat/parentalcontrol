using Microsoft.Data.Sqlite;

namespace ParentalGuard.Core;

public record RequestLogEntry(DateTime TimestampUtc, string Host, string Url, bool Blocked);

/// <summary>Logs every proxied request to a local SQLite database (requests.db).</summary>
public class Logger
{
    private readonly string _connectionString;
    private readonly object _lock = new();

    public Logger(string? databasePath = null)
    {
        var path = databasePath ?? Path.Combine(AppPaths.DataDirectory, "requests.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = $"Data Source={path}";
        Initialize();
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Requests (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT NOT NULL,
                Host TEXT NOT NULL,
                Url TEXT NOT NULL,
                Blocked INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Requests_TimestampUtc ON Requests (TimestampUtc);
            """;
        command.ExecuteNonQuery();
    }

    public void LogRequest(string host, string url, bool blocked)
    {
        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO Requests (TimestampUtc, Host, Url, Blocked) VALUES ($ts, $host, $url, $blocked)";
            command.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$host", host);
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$blocked", blocked ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<RequestLogEntry> GetRecent(int count = 200)
    {
        var results = new List<RequestLogEntry>();

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT TimestampUtc, Host, Url, Blocked FROM Requests ORDER BY Id DESC LIMIT $count";
            command.Parameters.AddWithValue("$count", count);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new RequestLogEntry(
                    DateTime.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3) != 0));
            }
        }

        return results;
    }

    /// <summary>Deletes log rows older than the given retention window (defaults to 90 days).</summary>
    public void PurgeOlderThan(TimeSpan retention)
    {
        var cutoff = (DateTime.UtcNow - retention).ToString("O");

        lock (_lock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Requests WHERE TimestampUtc < $cutoff";
            command.Parameters.AddWithValue("$cutoff", cutoff);
            command.ExecuteNonQuery();
        }
    }
}
