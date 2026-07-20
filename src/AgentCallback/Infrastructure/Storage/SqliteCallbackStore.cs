using System.Globalization;
using System.Text.Json;
using AgentCallback.Domain;
using AgentCallback.Infrastructure.Security;
using Microsoft.Data.Sqlite;

namespace AgentCallback.Infrastructure.Storage;

public sealed class SqliteCallbackStore : ICallbackStore
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _connectionString;
    private readonly ISecretProtector _protector;

    public SqliteCallbackStore(AppPaths paths, ISecretProtector protector)
    {
        paths.EnsureDataDirectory();
        _protector = protector;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS callbacks (
                callback_id TEXT PRIMARY KEY,
                label TEXT NULL,
                provider TEXT NOT NULL,
                target_thread_id TEXT NOT NULL,
                source_kind TEXT NOT NULL,
                process_id INTEGER NULL,
                process_creation_utc TEXT NULL,
                process_executable_path TEXT NULL,
                process_command_line TEXT NULL,
                expected_command_line_contains TEXT NULL,
                working_directory TEXT NOT NULL,
                instruction_cipher BLOB NOT NULL,
                evidence_paths_json TEXT NOT NULL,
                delivery_mode TEXT NOT NULL,
                state TEXT NOT NULL,
                client_message_id TEXT NOT NULL,
                trigger_secret_hash BLOB NULL,
                reported_outcome TEXT NULL,
                exit_code INTEGER NULL,
                summary TEXT NULL,
                reported_evidence_json TEXT NOT NULL,
                attempt_count INTEGER NOT NULL,
                next_attempt_utc TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                expires_utc TEXT NULL,
                completion_observed_utc TEXT NULL,
                delivered_utc TEXT NULL,
                acknowledged_utc TEXT NULL,
                delivery_transport TEXT NULL,
                attached_to_existing INTEGER NULL,
                last_error TEXT NULL,
                version INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_callbacks_state_next_attempt
                ON callbacks(state, next_attempt_utc);
            CREATE INDEX IF NOT EXISTS ix_callbacks_process
                ON callbacks(source_kind, process_id, process_creation_utc);
            """;
        await create.ExecuteNonQueryAsync(cancellationToken);

        var version = connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = """
            INSERT INTO metadata(key, value) VALUES('schema_version', $version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        version.Parameters.AddWithValue("$version", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
        await version.ExecuteNonQueryAsync(cancellationToken);

        var recover = connection.CreateCommand();
        recover.Transaction = transaction;
        recover.CommandText = """
            UPDATE callbacks
            SET state = 'Ambiguous',
                updated_utc = $now,
                last_error = 'Host restarted while delivery was in progress; automatic retry is disabled.',
                version = version + 1
            WHERE state = 'Dispatching';
            """;
        recover.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await recover.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CallbackRecord> CreateAsync(
        CallbackRecord callback,
        byte[]? triggerSecretHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO callbacks (
                callback_id, label, provider, target_thread_id, source_kind,
                process_id, process_creation_utc, process_executable_path,
                process_command_line, expected_command_line_contains,
                working_directory, instruction_cipher, evidence_paths_json,
                delivery_mode, state, client_message_id, trigger_secret_hash,
                reported_outcome, exit_code, summary, reported_evidence_json,
                attempt_count, next_attempt_utc, created_utc, updated_utc,
                expires_utc, completion_observed_utc, delivered_utc,
                acknowledged_utc, delivery_transport, attached_to_existing,
                last_error, version)
            VALUES (
                $callback_id, $label, $provider, $target_thread_id, $source_kind,
                $process_id, $process_creation_utc, $process_executable_path,
                $process_command_line, $expected_command_line_contains,
                $working_directory, $instruction_cipher, $evidence_paths_json,
                $delivery_mode, $state, $client_message_id, $trigger_secret_hash,
                $reported_outcome, $exit_code, $summary, $reported_evidence_json,
                $attempt_count, $next_attempt_utc, $created_utc, $updated_utc,
                $expires_utc, $completion_observed_utc, $delivered_utc,
                $acknowledged_utc, $delivery_transport, $attached_to_existing,
                $last_error, $version);
            """;
        BindRecord(command, callback, triggerSecretHash);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return callback;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                $"Callback id already exists: {callback.CallbackId}",
                exception);
        }
    }

    public async Task<CallbackRecord?> GetAsync(
        string callbackId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM callbacks WHERE callback_id = $id;";
        command.Parameters.AddWithValue("$id", callbackId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    public async Task<byte[]?> GetTriggerSecretHashAsync(
        string callbackId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT trigger_secret_hash FROM callbacks WHERE callback_id = $id;";
        command.Parameters.AddWithValue("$id", callbackId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is byte[] bytes ? bytes : null;
    }

    public Task<IReadOnlyList<CallbackRecord>> ListAsync(
        CallbackState? state,
        int limit,
        CancellationToken cancellationToken)
    {
        var where = state.HasValue ? "WHERE state = $state" : "";
        return QueryAsync(
            $"SELECT * FROM callbacks {where} ORDER BY created_utc DESC LIMIT $limit;",
            command =>
            {
                if (state.HasValue)
                {
                    command.Parameters.AddWithValue("$state", state.Value.ToString());
                }

                command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<CallbackRecord>> ListWatchingAsync(
        int limit,
        CancellationToken cancellationToken) =>
        QueryAsync(
            "SELECT * FROM callbacks WHERE state = 'Watching' ORDER BY created_utc LIMIT $limit;",
            command => command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500)),
            cancellationToken);

    public Task<IReadOnlyList<CallbackRecord>> ListDispatchableAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken) =>
        QueryAsync(
            """
            SELECT * FROM callbacks
            WHERE state = 'Ready'
               OR (state = 'Retryable' AND (next_attempt_utc IS NULL OR next_attempt_utc <= $now))
            ORDER BY created_utc
            LIMIT $limit;
            """,
            command =>
            {
                command.Parameters.AddWithValue("$now", Format(now));
                command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
            },
            cancellationToken);

    public async Task<bool> TryMarkReadyAsync(
        string callbackId,
        CompletionObservation observation,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE callbacks
            SET state = 'Ready',
                reported_outcome = $reported_outcome,
                exit_code = $exit_code,
                summary = $summary,
                reported_evidence_json = $reported_evidence_json,
                completion_observed_utc = $observed_utc,
                updated_utc = $observed_utc,
                next_attempt_utc = NULL,
                last_error = NULL,
                version = version + 1
            WHERE callback_id = $id AND state IN ('Registered', 'Watching');
            """;
        command.Parameters.AddWithValue("$id", callbackId);
        command.Parameters.AddWithValue("$reported_outcome", observation.ReportedOutcome);
        command.Parameters.AddWithValue("$exit_code", DbValue(observation.ExitCode));
        command.Parameters.AddWithValue("$summary", DbValue(observation.Summary));
        command.Parameters.AddWithValue("$reported_evidence_json", JsonSerializer.Serialize(observation.EvidenceRefs));
        command.Parameters.AddWithValue("$observed_utc", Format(observation.ObservedUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryBeginDispatchAsync(
        string callbackId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE callbacks
            SET state = 'Dispatching',
                attempt_count = attempt_count + 1,
                updated_utc = $now,
                last_error = NULL,
                version = version + 1
            WHERE callback_id = $id
              AND (state = 'Ready'
                   OR (state = 'Retryable' AND (next_attempt_utc IS NULL OR next_attempt_utc <= $now)));
            """;
        command.Parameters.AddWithValue("$id", callbackId);
        command.Parameters.AddWithValue("$now", Format(now));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task CompleteDispatchAsync(
        string callbackId,
        CallbackDeliveryResult result,
        DateTimeOffset completedUtc,
        DateTimeOffset? nextAttemptUtc,
        CancellationToken cancellationToken)
    {
        var state = result.Kind switch
        {
            DeliveryOutcomeKind.Accepted => CallbackState.Delivered,
            DeliveryOutcomeKind.Retryable => CallbackState.Retryable,
            DeliveryOutcomeKind.Ambiguous => CallbackState.Ambiguous,
            DeliveryOutcomeKind.Failed => CallbackState.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE callbacks
            SET state = $state,
                updated_utc = $completed_utc,
                delivered_utc = $delivered_utc,
                delivery_transport = $delivery_transport,
                attached_to_existing = $attached_to_existing,
                next_attempt_utc = $next_attempt_utc,
                last_error = $last_error,
                version = version + 1
            WHERE callback_id = $id AND state = 'Dispatching';
            """;
        command.Parameters.AddWithValue("$id", callbackId);
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$completed_utc", Format(completedUtc));
        command.Parameters.AddWithValue(
            "$delivered_utc",
            state == CallbackState.Delivered ? Format(completedUtc) : DBNull.Value);
        command.Parameters.AddWithValue("$delivery_transport", result.Transport);
        command.Parameters.AddWithValue(
            "$attached_to_existing",
            result.AttachedToExisting.HasValue ? result.AttachedToExisting.Value ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$next_attempt_utc", DbValue(Format(nextAttemptUtc)));
        command.Parameters.AddWithValue("$last_error", DbValue(result.Error));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Callback {callbackId} was not dispatching when its delivery completed.");
        }
    }

    public Task<bool> TryMarkFailedAsync(
        string callbackId,
        string error,
        CancellationToken cancellationToken) =>
        ExecuteStateUpdateAsync(
            """
            UPDATE callbacks
            SET state = 'Failed', updated_utc = $now, last_error = $error, version = version + 1
            WHERE callback_id = $id AND state IN ('Registered', 'Watching', 'Ready', 'Retryable');
            """,
            callbackId,
            command => command.Parameters.AddWithValue("$error", error),
            cancellationToken);

    public Task<bool> TryCancelAsync(string callbackId, CancellationToken cancellationToken) =>
        ExecuteStateUpdateAsync(
            """
            UPDATE callbacks
            SET state = 'Canceled', updated_utc = $now, next_attempt_utc = NULL, version = version + 1
            WHERE callback_id = $id AND state IN ('Registered', 'Watching', 'Ready', 'Retryable');
            """,
            callbackId,
            configure: null,
            cancellationToken);

    public Task<bool> TryAcknowledgeAsync(string callbackId, CancellationToken cancellationToken) =>
        ExecuteStateUpdateAsync(
            """
            UPDATE callbacks
            SET state = 'Acknowledged', updated_utc = $now, acknowledged_utc = $now, version = version + 1
            WHERE callback_id = $id AND state = 'Delivered';
            """,
            callbackId,
            configure: null,
            cancellationToken);

    public async Task<int> ExpireDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE callbacks
            SET state = 'Expired', updated_utc = $now, next_attempt_utc = NULL, version = version + 1
            WHERE expires_utc IS NOT NULL
              AND expires_utc <= $now
              AND state IN ('Registered', 'Watching', 'Ready', 'Retryable');
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> ExecuteStateUpdateAsync(
        string sql,
        string callbackId,
        Action<SqliteCommand>? configure,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", callbackId);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        configure?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<IReadOnlyList<CallbackRecord>> QueryAsync(
        string sql,
        Action<SqliteCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        configure(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<CallbackRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private void BindRecord(
        SqliteCommand command,
        CallbackRecord callback,
        byte[]? triggerSecretHash)
    {
        command.Parameters.AddWithValue("$callback_id", callback.CallbackId);
        command.Parameters.AddWithValue("$label", DbValue(callback.Label));
        command.Parameters.AddWithValue("$provider", callback.Provider);
        command.Parameters.AddWithValue("$target_thread_id", callback.TargetThreadId);
        command.Parameters.AddWithValue("$source_kind", callback.SourceKind.ToString());
        command.Parameters.AddWithValue("$process_id", DbValue(callback.ProcessId));
        command.Parameters.AddWithValue("$process_creation_utc", DbValue(Format(callback.ProcessCreationUtc)));
        command.Parameters.AddWithValue("$process_executable_path", DbValue(callback.ProcessExecutablePath));
        command.Parameters.AddWithValue("$process_command_line", DbValue(callback.ProcessCommandLine));
        command.Parameters.AddWithValue("$expected_command_line_contains", DbValue(callback.ExpectedCommandLineContains));
        command.Parameters.AddWithValue("$working_directory", callback.WorkingDirectory);
        command.Parameters.AddWithValue("$instruction_cipher", _protector.Protect(callback.Instruction));
        command.Parameters.AddWithValue("$evidence_paths_json", JsonSerializer.Serialize(callback.EvidencePaths));
        command.Parameters.AddWithValue("$delivery_mode", callback.DeliveryMode.ToString());
        command.Parameters.AddWithValue("$state", callback.State.ToString());
        command.Parameters.AddWithValue("$client_message_id", callback.ClientMessageId);
        command.Parameters.AddWithValue("$trigger_secret_hash", DbValue(triggerSecretHash));
        command.Parameters.AddWithValue("$reported_outcome", DbValue(callback.ReportedOutcome));
        command.Parameters.AddWithValue("$exit_code", DbValue(callback.ExitCode));
        command.Parameters.AddWithValue("$summary", DbValue(callback.Summary));
        command.Parameters.AddWithValue("$reported_evidence_json", JsonSerializer.Serialize(callback.ReportedEvidenceRefs));
        command.Parameters.AddWithValue("$attempt_count", callback.AttemptCount);
        command.Parameters.AddWithValue("$next_attempt_utc", DbValue(Format(callback.NextAttemptUtc)));
        command.Parameters.AddWithValue("$created_utc", Format(callback.CreatedUtc));
        command.Parameters.AddWithValue("$updated_utc", Format(callback.UpdatedUtc));
        command.Parameters.AddWithValue("$expires_utc", DbValue(Format(callback.ExpiresUtc)));
        command.Parameters.AddWithValue("$completion_observed_utc", DbValue(Format(callback.CompletionObservedUtc)));
        command.Parameters.AddWithValue("$delivered_utc", DbValue(Format(callback.DeliveredUtc)));
        command.Parameters.AddWithValue("$acknowledged_utc", DbValue(Format(callback.AcknowledgedUtc)));
        command.Parameters.AddWithValue("$delivery_transport", DbValue(callback.DeliveryTransport));
        command.Parameters.AddWithValue(
            "$attached_to_existing",
            callback.AttachedToExisting.HasValue ? callback.AttachedToExisting.Value ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$last_error", DbValue(callback.LastError));
        command.Parameters.AddWithValue("$version", callback.Version);
    }

    private CallbackRecord ReadRecord(SqliteDataReader reader)
    {
        var instructionCipher = (byte[])reader["instruction_cipher"];
        return new CallbackRecord
        {
            CallbackId = GetString(reader, "callback_id")!,
            Label = GetString(reader, "label"),
            Provider = GetString(reader, "provider")!,
            TargetThreadId = GetString(reader, "target_thread_id")!,
            SourceKind = Enum.Parse<CallbackSourceKind>(GetString(reader, "source_kind")!, true),
            ProcessId = GetInt32(reader, "process_id"),
            ProcessCreationUtc = GetDateTimeOffset(reader, "process_creation_utc"),
            ProcessExecutablePath = GetString(reader, "process_executable_path"),
            ProcessCommandLine = GetString(reader, "process_command_line"),
            ExpectedCommandLineContains = GetString(reader, "expected_command_line_contains"),
            WorkingDirectory = GetString(reader, "working_directory")!,
            Instruction = _protector.Unprotect(instructionCipher),
            EvidencePaths = DeserializeList(GetString(reader, "evidence_paths_json")),
            DeliveryMode = Enum.Parse<CallbackDeliveryMode>(GetString(reader, "delivery_mode")!, true),
            State = Enum.Parse<CallbackState>(GetString(reader, "state")!, true),
            ClientMessageId = GetString(reader, "client_message_id")!,
            ReportedOutcome = GetString(reader, "reported_outcome"),
            ExitCode = GetInt32(reader, "exit_code"),
            Summary = GetString(reader, "summary"),
            ReportedEvidenceRefs = DeserializeList(GetString(reader, "reported_evidence_json")),
            AttemptCount = Convert.ToInt32(reader["attempt_count"], CultureInfo.InvariantCulture),
            NextAttemptUtc = GetDateTimeOffset(reader, "next_attempt_utc"),
            CreatedUtc = GetDateTimeOffset(reader, "created_utc")!.Value,
            UpdatedUtc = GetDateTimeOffset(reader, "updated_utc")!.Value,
            ExpiresUtc = GetDateTimeOffset(reader, "expires_utc"),
            CompletionObservedUtc = GetDateTimeOffset(reader, "completion_observed_utc"),
            DeliveredUtc = GetDateTimeOffset(reader, "delivered_utc"),
            AcknowledgedUtc = GetDateTimeOffset(reader, "acknowledged_utc"),
            DeliveryTransport = GetString(reader, "delivery_transport"),
            AttachedToExisting = GetBoolean(reader, "attached_to_existing"),
            LastError = GetString(reader, "last_error"),
            Version = Convert.ToInt64(reader["version"], CultureInfo.InvariantCulture)
        };
    }

    private static IReadOnlyList<string> DeserializeList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : JsonSerializer.Deserialize<string[]>(value) ?? [];

    private static string? GetString(SqliteDataReader reader, string name) =>
        reader[name] is DBNull ? null : Convert.ToString(reader[name], CultureInfo.InvariantCulture);

    private static int? GetInt32(SqliteDataReader reader, string name) =>
        reader[name] is DBNull ? null : Convert.ToInt32(reader[name], CultureInfo.InvariantCulture);

    private static bool? GetBoolean(SqliteDataReader reader, string name) =>
        reader[name] is DBNull ? null : Convert.ToInt32(reader[name], CultureInfo.InvariantCulture) != 0;

    private static DateTimeOffset? GetDateTimeOffset(SqliteDataReader reader, string name) =>
        ParseDateTimeOffset(GetString(reader, name));

    private static DateTimeOffset? ParseDateTimeOffset(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Format(DateTimeOffset? value) =>
        value.HasValue ? Format(value.Value) : null;

    private static object DbValue(object? value) => value ?? DBNull.Value;
}
