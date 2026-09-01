using Microsoft.Data.Sqlite;
using SuperCV.Application.History;
using SuperCV.Application.Ports;
using System.Globalization;
using System.Text;

namespace SuperCV.Infrastructure.Windows;

public sealed class SqlitePersistentHistoryRepository : IPersistentHistoryRepository
{
    private const int CurrentSchemaVersion = 1;
    private const string RichHistorySeedKey = "rich-history-seed-v1";
    private readonly V2Paths _paths;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly HashSet<string> _initializedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public SqlitePersistentHistoryRepository()
        : this(V2Paths.ForCurrentUser())
    {
    }

    public SqlitePersistentHistoryRepository(string rootDirectory)
        : this(V2Paths.FromRootDirectory(rootDirectory))
    {
    }

    internal SqlitePersistentHistoryRepository(V2Paths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async ValueTask AppendRangeAsync(
        Guid workspaceId,
        IReadOnlyList<PersistentHistoryWrite> entries,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        string databasePath = _paths.GetWorkspacePersistentHistoryFilePath(workspaceId);
        await EnsureInitializedAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection =
            await OpenConnectionAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        InsertEntries(connection, transaction, entries, cancellationToken);
        TrimOverflow(connection, transaction);
        transaction.Commit();
    }

    public async ValueTask SeedOnceAsync(
        Guid workspaceId,
        IReadOnlyList<PersistentHistoryWrite> entries,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        ArgumentNullException.ThrowIfNull(entries);

        string databasePath = _paths.GetWorkspacePersistentHistoryFilePath(workspaceId);
        await EnsureInitializedAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection =
            await OpenConnectionAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();

        await using var markerCommand = connection.CreateCommand();
        markerCommand.Transaction = transaction;
        markerCommand.CommandText =
            """
            INSERT OR IGNORE INTO persistent_history_metadata(key, value)
            VALUES ($key, '1');
            """;
        markerCommand.Parameters.AddWithValue("$key", RichHistorySeedKey);
        int markerCreated = await markerCommand.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (markerCreated > 0 && entries.Count > 0)
        {
            InsertEntries(connection, transaction, entries, cancellationToken);
            TrimOverflow(connection, transaction);
        }

        transaction.Commit();
    }

    public async ValueTask<PersistentHistoryPage> QueryAsync(
        Guid workspaceId,
        string searchText,
        int limit,
        PersistentHistoryCursor? cursor = null,
        PersistentHistoryDateRange? dateRange = null,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        ArgumentNullException.ThrowIfNull(searchText);
        dateRange?.Validate();
        int normalizedLimit = Math.Clamp(
            limit,
            1,
            PersistentHistoryService.MaximumPageSize);
        string databasePath = _paths.GetWorkspacePersistentHistoryFilePath(workspaceId);
        await EnsureInitializedAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection =
            await OpenConnectionAsync(databasePath, cancellationToken).ConfigureAwait(false);

        int totalCount = cursor?.TotalCount
            ?? await CountAsync(connection, searchText, dateRange, cancellationToken).ConfigureAwait(false);
        if (totalCount == 0)
        {
            return new PersistentHistoryPage(
                Array.Empty<PersistentHistoryItem>(),
                NextCursor: null,
                TotalCount: 0);
        }

        var commandText = new StringBuilder(
            """
            SELECT rowid, captured_utc_ticks, text
            FROM persistent_history
            WHERE 1 = 1
            """);
        commandText.AppendLine();
        using var command = connection.CreateCommand();
        AddSearchCondition(commandText, command, searchText);
        AddDateRangeCondition(commandText, command, dateRange);
        if (cursor is not null)
        {
            commandText.AppendLine(
                """
                  AND (
                      captured_utc_ticks < $cursor_ticks
                      OR (
                          captured_utc_ticks = $cursor_ticks
                          AND rowid < $cursor_key
                      )
                  )
                """);
            command.Parameters.AddWithValue("$cursor_ticks", cursor.CapturedAtUtcTicks);
            command.Parameters.AddWithValue("$cursor_key", cursor.StorageKey);
        }

        commandText.AppendLine(
            """
            ORDER BY captured_utc_ticks DESC, rowid DESC
            LIMIT $limit;
            """);
        command.CommandText = commandText.ToString();
        command.Parameters.AddWithValue("$limit", normalizedLimit + 1);

        var rows = new List<(long StorageKey, long CapturedAtUtcTicks, string Text)>(
            normalizedLimit + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2)));
        }

        bool hasMore = rows.Count > normalizedLimit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        long firstDisplayId = cursor?.NextDisplayId ?? 1;
        PersistentHistoryItem[] items = rows
            .Select((row, index) => new PersistentHistoryItem(
                row.StorageKey,
                firstDisplayId + index,
                row.Text,
                new DateTimeOffset(row.CapturedAtUtcTicks, TimeSpan.Zero)))
            .ToArray();
        PersistentHistoryCursor? nextCursor = null;
        if (hasMore && rows.Count > 0)
        {
            (long storageKey, long capturedAtUtcTicks, _) = rows[^1];
            nextCursor = new PersistentHistoryCursor(
                capturedAtUtcTicks,
                storageKey,
                firstDisplayId + rows.Count,
                totalCount);
        }

        return new PersistentHistoryPage(items, nextCursor, totalCount);
    }

    public async ValueTask<bool> DeleteAsync(
        Guid workspaceId,
        long storageKey,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        if (storageKey <= 0)
        {
            return false;
        }

        string databasePath = _paths.GetWorkspacePersistentHistoryFilePath(workspaceId);
        await EnsureInitializedAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection =
            await OpenConnectionAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM persistent_history WHERE rowid = $storage_key;";
        command.Parameters.AddWithValue("$storage_key", storageKey);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask<bool> DeleteMatchingAsync(
        Guid workspaceId,
        PersistentHistoryMatch match,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        ArgumentNullException.ThrowIfNull(match);
        string databasePath = _paths.GetWorkspacePersistentHistoryFilePath(workspaceId);
        await EnsureInitializedAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection =
            await OpenConnectionAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = CreateDeleteMatchingCommand(connection);
        SetMatchParameters(command, match);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask<int> DeleteMatchingRangeAsync(
        Guid workspaceId,
        IReadOnlyList<PersistentHistoryMatch> matches,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0)
        {
            return 0;
        }

        string databasePath = _paths.GetWorkspacePersistentHistoryFilePath(workspaceId);
        await EnsureInitializedAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection =
            await OpenConnectionAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = CreateDeleteMatchingCommand(connection, transaction);
        int deletedCount = 0;
        foreach (PersistentHistoryMatch match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(match);
            SetMatchParameters(command, match);
            deletedCount += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return deletedCount;
    }

        public async ValueTask<bool> ReplaceMatchingTextAsync(
            Guid workspaceId,
            PersistentHistoryMatch match,
            string replacementText,
            CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        ArgumentNullException.ThrowIfNull(match);
        string databasePath = _paths.GetWorkspacePersistentHistoryFilePath(workspaceId);
        await EnsureInitializedAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection =
            await OpenConnectionAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE persistent_history
            SET text = $replacement_text
            WHERE rowid = (
                SELECT rowid
                FROM persistent_history
                WHERE captured_utc_ticks = $captured_utc_ticks
                  AND text = $text
                ORDER BY rowid DESC
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue("$replacement_text", replacementText ?? string.Empty);
        command.Parameters.AddWithValue(
            "$captured_utc_ticks",
            match.CapturedAtUtc.ToUniversalTime().Ticks);
            command.Parameters.AddWithValue("$text", match.Text ?? string.Empty);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }

    public async ValueTask<int> TrimToLimitAsync(
            Guid workspaceId,
            int retainedItems,
            CancellationToken cancellationToken = default)
        {
            ValidateWorkspaceId(workspaceId);
            int normalizedRetainedItems = Math.Clamp(
                retainedItems,
                0,
                PersistentHistoryService.MaximumItemsPerWorkspace);
            string databasePath = _paths.GetWorkspacePersistentHistoryFilePath(workspaceId);
            await EnsureInitializedAsync(databasePath, cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection =
                await OpenConnectionAsync(databasePath, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM persistent_history
                WHERE rowid IN (
                    SELECT rowid
                    FROM persistent_history
                    ORDER BY captured_utc_ticks DESC, rowid DESC
                    LIMIT -1 OFFSET $retained_items
                );
                """;
            command.Parameters.AddWithValue("$retained_items", normalizedRetainedItems);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> DeleteImagesAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        string databasePath = _paths.GetWorkspacePersistentHistoryFilePath(workspaceId);
        await EnsureInitializedAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(
            databasePath,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM persistent_history
            WHERE substr(text, 1, length($image_prefix)) = $image_prefix;
            """;
        command.Parameters.AddWithValue("$image_prefix", PersistentHistoryCodec.ImagePrefix);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

        public async ValueTask DeleteWorkspaceAsync(
            Guid workspaceId,
            CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceId(workspaceId);
        cancellationToken.ThrowIfCancellationRequested();
        string databasePath = _paths.GetWorkspacePersistentHistoryFilePath(workspaceId);

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = _initializedPaths.Remove(databasePath);
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");

            string? workspaceDirectory = Path.GetDirectoryName(databasePath);
            if (workspaceDirectory is not null &&
                Directory.Exists(workspaceDirectory) &&
                !Directory.EnumerateFileSystemEntries(workspaceDirectory).Any())
            {
                Directory.Delete(workspaceDirectory);
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _initializationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _initializedPaths.Clear();
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async ValueTask EnsureInitializedAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initializedPaths.Contains(databasePath))
            {
                return;
            }

            string? directory = Path.GetDirectoryName(databasePath);
            if (directory is null)
            {
                throw new InvalidOperationException(
                    "The persistent history database directory is unavailable.");
            }

            Directory.CreateDirectory(directory);
            await using SqliteConnection connection =
                await OpenConnectionAsync(databasePath, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS persistent_history (
                    captured_utc_ticks INTEGER NOT NULL,
                    text TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_persistent_history_chronological
                    ON persistent_history(captured_utc_ticks DESC);
                CREATE TABLE IF NOT EXISTS persistent_history_metadata (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                ) WITHOUT ROWID;
                DROP TRIGGER IF EXISTS trim_persistent_history_after_insert;
                PRAGMA user_version = {CurrentSchemaVersion};
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initializedPaths.Add(databasePath);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA busy_timeout = 5000;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = FILE;
                PRAGMA cache_size = -4096;
                PRAGMA wal_autocheckpoint = 256;
                PRAGMA journal_size_limit = 4194304;
                PRAGMA mmap_size = 0;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void InsertEntries(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<PersistentHistoryWrite> entries,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO persistent_history(captured_utc_ticks, text)
            VALUES ($captured_utc_ticks, $text);
            """;
        SqliteParameter ticksParameter = command.Parameters.Add(
            "$captured_utc_ticks",
            SqliteType.Integer);
        SqliteParameter textParameter = command.Parameters.Add("$text", SqliteType.Text);
        command.Prepare();

        foreach (PersistentHistoryWrite entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ticksParameter.Value = entry.CapturedAtUtc.ToUniversalTime().Ticks;
            // Do not expand a complete seed batch before SQLite writes it. The temporary string
            // for one payload is released before the next entry is expanded.
            textParameter.Value = entry.GetText();
            command.ExecuteNonQuery();
            textParameter.Value = DBNull.Value;
        }
    }

    private static void TrimOverflow(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = "SELECT COUNT(*) FROM persistent_history;";
        long count = (long)(countCommand.ExecuteScalar() ?? 0L);
        long overflow = count - PersistentHistoryService.MaximumItemsPerWorkspace;
        if (overflow <= 0)
        {
            return;
        }

        using var trimCommand = connection.CreateCommand();
        trimCommand.Transaction = transaction;
        trimCommand.CommandText =
            """
            DELETE FROM persistent_history
            WHERE rowid IN (
                SELECT rowid
                FROM persistent_history
                ORDER BY captured_utc_ticks ASC, rowid ASC
                LIMIT $overflow
            );
            """;
        trimCommand.Parameters.AddWithValue("$overflow", overflow);
        trimCommand.ExecuteNonQuery();
    }

    private static async ValueTask<int> CountAsync(
        SqliteConnection connection,
        string searchText,
        PersistentHistoryDateRange? dateRange,
        CancellationToken cancellationToken)
    {
        var commandText = new StringBuilder(
            """
            SELECT COUNT(*)
            FROM persistent_history
            WHERE 1 = 1
            """);
        await using var command = connection.CreateCommand();
        AddSearchCondition(commandText, command, searchText);
        AddDateRangeCondition(commandText, command, dateRange);
        commandText.Append(';');
        command.CommandText = commandText.ToString();
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static SqliteCommand CreateDeleteMatchingCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM persistent_history
            WHERE rowid = (
                SELECT rowid
                FROM persistent_history
                WHERE captured_utc_ticks = $captured_utc_ticks
                  AND text = $text
                ORDER BY rowid DESC
                LIMIT 1
            );
            """;
        command.Parameters.Add("$captured_utc_ticks", SqliteType.Integer);
        command.Parameters.Add("$text", SqliteType.Text);
        return command;
    }

    private static void SetMatchParameters(
        SqliteCommand command,
        PersistentHistoryMatch match)
    {
        command.Parameters["$captured_utc_ticks"].Value =
            match.CapturedAtUtc.ToUniversalTime().Ticks;
        command.Parameters["$text"].Value = match.Text ?? string.Empty;
    }

    private static void AddSearchCondition(
        StringBuilder commandText,
        SqliteCommand command,
        string searchText)
    {
        if (searchText.Length == 0)
        {
            return;
        }

        bool includeImages = ImageSearchTerms.IncludesImages(searchText);
        commandText.AppendLine(includeImages
            ? """
                AND (
                  substr(text, 1, length($image_prefix)) = $image_prefix
                  OR (
                    substr(text, 1, length($image_prefix)) <> $image_prefix
                    AND text COLLATE NOCASE LIKE $search_pattern ESCAPE '\'
                  )
                )
              """
            : """
                AND substr(text, 1, length($image_prefix)) <> $image_prefix
                AND text COLLATE NOCASE LIKE $search_pattern ESCAPE '\'
              """);
        string escapedSearchText = searchText
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
        command.Parameters.AddWithValue("$search_pattern", $"%{escapedSearchText}%");
        command.Parameters.AddWithValue("$image_prefix", PersistentHistoryCodec.ImagePrefix);
    }

    private static void AddDateRangeCondition(
        StringBuilder commandText,
        SqliteCommand command,
        PersistentHistoryDateRange? dateRange)
    {
        if (dateRange is null)
        {
            return;
        }

        commandText.AppendLine();
        if (dateRange?.StartInclusiveUtc is { } startInclusiveUtc)
        {
            commandText.AppendLine("AND captured_utc_ticks >= $start_inclusive_utc_ticks");
            command.Parameters.AddWithValue(
                "$start_inclusive_utc_ticks",
                startInclusiveUtc.ToUniversalTime().Ticks);
        }

        if (dateRange?.EndExclusiveUtc is { } endExclusiveUtc)
        {
            commandText.AppendLine("AND captured_utc_ticks < $end_exclusive_utc_ticks");
            command.Parameters.AddWithValue(
                "$end_exclusive_utc_ticks",
                endExclusiveUtc.ToUniversalTime().Ticks);
        }
    }

    private static void ValidateWorkspaceId(Guid workspaceId)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A workspace id cannot be empty.", nameof(workspaceId));
        }
    }
}
