using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;
using System.IO;

namespace DesktopAssistant;

internal sealed record StoredConversation(string Id, string Title, DateTime CreatedUtc, DateTime UpdatedUtc);
internal sealed record StoredMessage(string Role, string Content, DateTime CreatedUtc);
internal sealed record StoredPreference(long Id, string Content, DateTime CreatedUtc);

/// <summary>SQLite persistence for local conversations and user-approved preferences.</summary>
internal sealed class ConversationMemoryStore
{
    private const int SchemaVersion = 2;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConversationMemoryStore(string? databasePath = null)
    {
        databasePath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopAssistant", "conversation-memory.db");
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, Pooling = false }.ToString();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > SchemaVersion) throw new InvalidOperationException("The local memory database was created by a newer version of the app.");
        using var transaction = connection.BeginTransaction();
        if (version < 1)
        {
            Execute(connection, transaction, """
                CREATE TABLE sessions(session_id TEXT PRIMARY KEY, title TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
                CREATE TABLE messages(message_id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
                    role TEXT NOT NULL, content TEXT NOT NULL, created_utc TEXT NOT NULL);
                CREATE INDEX ix_messages_session_created ON messages(session_id, message_id);
                """);
        }
        if (version < 2)
            Execute(connection, transaction, "CREATE TABLE preferences(preference_id INTEGER PRIMARY KEY AUTOINCREMENT, memory_text TEXT NOT NULL, created_utc TEXT NOT NULL);");
        Execute(connection, transaction, $"PRAGMA user_version={SchemaVersion};");
        transaction.Commit();
    }, cancellationToken);

    public Task<string> CreateSessionAsync(string? title = null, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        using var connection = Open();
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O");
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sessions(session_id,title,created_utc,updated_utc) VALUES($id,$title,$now,$now)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", CleanTitle(title));
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        return id;
    }, cancellationToken);

    public Task<StoredConversation?> GetLatestSessionAsync(CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id,title,created_utc,updated_utc FROM sessions ORDER BY updated_utc DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSession(reader) : null;
    }, cancellationToken);

    public Task<IReadOnlyList<StoredConversation>> GetSessionsAsync(CancellationToken cancellationToken = default) => RunAsync<IReadOnlyList<StoredConversation>>(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id,title,created_utc,updated_utc FROM sessions ORDER BY updated_utc DESC LIMIT 500";
        using var reader = command.ExecuteReader();
        var result = new List<StoredConversation>();
        while (reader.Read()) result.Add(ReadSession(reader));
        return result;
    }, cancellationToken);

    public Task<IReadOnlyList<StoredMessage>> GetMessagesAsync(string sessionId, int limit = 200, CancellationToken cancellationToken = default) => RunAsync<IReadOnlyList<StoredMessage>>(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT role,content,created_utc FROM (SELECT role,content,created_utc,message_id FROM messages WHERE session_id=$id ORDER BY message_id DESC LIMIT $limit) ORDER BY message_id";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var result = new List<StoredMessage>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return result;
    }, cancellationToken);

    public Task AppendMessageAsync(string sessionId, string role, string content, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        if (role is not ("user" or "assistant" or "tool")) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var message = connection.CreateCommand();
        message.Transaction = transaction;
        message.CommandText = "INSERT INTO messages(session_id,role,content,created_utc) VALUES($id,$role,$content,$now)";
        message.Parameters.AddWithValue("$id", sessionId);
        message.Parameters.AddWithValue("$role", role);
        message.Parameters.AddWithValue("$content", content.Length <= 100_000 ? content : content[..100_000]);
        message.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        message.ExecuteNonQuery();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE sessions SET updated_utc=$now, title=CASE WHEN title='New conversation' AND $role='user' THEN substr($content,1,80) ELSE title END WHERE session_id=$id";
        update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", sessionId);
        update.Parameters.AddWithValue("$role", role);
        update.Parameters.AddWithValue("$content", content);
        update.ExecuteNonQuery();
        transaction.Commit();
    }, cancellationToken);

    public Task<long> SavePreferenceAsync(string content, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        var value = content.Trim();
        if (value.Length is 0 or > 500 || MemoryPrivacyFilter.IsSensitive(value)) throw new InvalidOperationException("That looks like sensitive information, so it was not saved as a preference.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO preferences(memory_text,created_utc) VALUES($text,$now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$text", value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return (long)command.ExecuteScalar()!;
    }, cancellationToken);

    public Task<IReadOnlyList<StoredPreference>> GetPreferencesAsync(CancellationToken cancellationToken = default) => RunAsync<IReadOnlyList<StoredPreference>>(() =>
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT preference_id,memory_text,created_utc FROM preferences ORDER BY preference_id DESC LIMIT 500";
        using var reader = command.ExecuteReader();
        var result = new List<StoredPreference>();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return result;
    }, cancellationToken);

    public async Task<IReadOnlyList<StoredPreference>> GetRelevantPreferencesAsync(string query, int maxItems = 4, int maxCharacters = 700, CancellationToken cancellationToken = default)
    {
        var all = await GetPreferencesAsync(cancellationToken);
        var terms = Keywords(query);
        if (terms.Count == 0) return [];
        return all.Select(item => (item, score: Keywords(item.Content).Count(terms.Contains)))
            .Where(pair => pair.score > 0).OrderByDescending(pair => pair.score).ThenByDescending(pair => pair.item.CreatedUtc)
            .Select(pair => pair.item).Take(Math.Clamp(maxItems, 0, 8))
            .Aggregate(new List<StoredPreference>(), (list, item) =>
            {
                var used = list.Sum(entry => entry.Content.Length);
                if (used + item.Content.Length <= maxCharacters) list.Add(item);
                return list;
            });
    }

    public Task DeletePreferenceAsync(long id, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM preferences WHERE preference_id=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }, cancellationToken);

    public Task<bool> DeleteMatchingPreferenceAsync(string query, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT preference_id,memory_text FROM preferences ORDER BY preference_id DESC";
        using var reader = command.ExecuteReader();
        var terms = Keywords(query);
        var matches = new List<long>();
        while (reader.Read()) if (Keywords(reader.GetString(1)).Intersect(terms).Any()) matches.Add(reader.GetInt64(0));
        reader.Close();
        if (matches.Count != 1) return false;
        using var delete = connection.CreateCommand(); delete.CommandText = "DELETE FROM preferences WHERE preference_id=$id";
        delete.Parameters.AddWithValue("$id", matches[0]); delete.ExecuteNonQuery(); return true;
    }, cancellationToken);

    public Task DeleteSessionAsync(string id, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE session_id=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }, cancellationToken);

    public Task ClearConversationsAsync(CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM sessions"; command.ExecuteNonQuery();
    }, cancellationToken);

    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }
    private static StoredConversation ReadSession(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind), DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind));
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery(); }
    private static string CleanTitle(string? title) => string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim()[..Math.Min(title.Trim().Length, 80)];
    private static HashSet<string> Keywords(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(match => match.Value).Where(word => word.Length > 2).ToHashSet();

    private async Task RunAsync(Action action, CancellationToken cancellationToken) => await RunAsync(() => { action(); return true; }, cancellationToken);
    private async Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(action, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}

internal static class MemoryPrivacyFilter
{
    private static readonly Regex Email = new(@"\b[^\s@]+@[^\s@]+\.[^\s@]+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Phone = new(@"(?<!\w)(?:\+?\d[\d ().-]{7,}\d)(?!\w)", RegexOptions.Compiled);
    private static readonly string[] SensitiveTerms = ["password", "passcode", "secret", "api key", "access token", "credit card", "card number", "bank account", "social security", "ssn", "medical", "health condition", "diagnosis", "medication", "date of birth", "home address"];
    public static bool IsSensitive(string text) => SensitiveTerms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase)) || Email.IsMatch(text) || Phone.IsMatch(text);
}

