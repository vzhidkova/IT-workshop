
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogHive.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;

namespace LogHive.Controllers;

[ApiController]
[Route("api/logs")]
public class LogsController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IConfiguration _configuration;

    public LogsController(
        NpgsqlDataSource dataSource,
        IConfiguration configuration)
    {
        _dataSource = dataSource;
        _configuration = configuration;
    }

    [HttpPost]
    public async Task<IActionResult> ReceiveLog(
        [FromBody] LogEventRequest request,
        [FromHeader(Name = "X-Api-Key")] string? apiKey)
    {
        // 1. Проверяем API-ключ.
        var expectedApiKey = _configuration["Ingestion:ApiKey"];

        if (string.IsNullOrWhiteSpace(expectedApiKey))
        {
            return StatusCode(500, new
            {
                Error = "API key is not configured"
            });
        }

        if (string.IsNullOrWhiteSpace(apiKey) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(apiKey),
                Encoding.UTF8.GetBytes(expectedApiKey)))
        {
            return Unauthorized(new
            {
                Error = "Invalid API key"
            });
        }

        // 2. Нормализуем уровень лога.
        var level = request.Level.Trim().ToUpperInvariant();

        if (level == "WARNING")
            level = "WARN";

        if (level != "ERROR" &&
            level != "WARN" &&
            level != "INFO")
        {
            return BadRequest(new
            {
                Error = "Unsupported log level"
            });
        }

        if (request.Timestamp == null ||
            string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new
            {
                Error = "Timestamp and msg are required"
            });
        }

        // 3. Создаём fingerprint самостоятельно.
        // Номер строки в caller не учитываем.
        var callerPath = NormalizeCaller(request.Caller);

        var fingerprintSource =
            $"{level}|{request.Message}|{callerPath}";

        var fingerprintBytes =
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource));

        var fingerprint =
            Convert.ToHexString(fingerprintBytes).ToLowerInvariant();

        // В issues.title ограничение VARCHAR(500).
        var title = request.Message.Length > 500
            ? request.Message[..500]
            : request.Message;

        // 4. Подготавливаем дополнительные поля JSON.
        var additionalData = request.AdditionalFields == null
            ? null
            : JsonSerializer.Serialize(request.AdditionalFields);

        await using var connection =
            await _dataSource.OpenConnectionAsync();

        await using var transaction =
            await connection.BeginTransactionAsync();

        try
        {
            // 5. Создаём проблему или обновляем существующую.
            const string issueSql = """
                INSERT INTO issues
                    (fingerprint, level, title, count,
                     first_seen, last_seen)
                VALUES
                    (@fingerprint, @level, @title, 1,
                     @timestamp, @timestamp)
                ON CONFLICT (fingerprint)
                DO UPDATE SET
                    count = issues.count + 1,
                    resolved = false,
                    last_seen = GREATEST(
                        issues.last_seen,
                        EXCLUDED.last_seen
                    ),
                    first_seen = LEAST(
                        issues.first_seen,
                        EXCLUDED.first_seen
                    )
                RETURNING id;
                """;

            int issueId;

            await using (var command =
                new NpgsqlCommand(issueSql, connection, transaction))
            {
                command.Parameters.AddWithValue(
                    "fingerprint", fingerprint);

                command.Parameters.AddWithValue(
                    "level", level);

                command.Parameters.AddWithValue(
                    "title", title);

                command.Parameters.AddWithValue(
                    "timestamp", request.Timestamp.Value);

                issueId = Convert.ToInt32(
                    await command.ExecuteScalarAsync());
            }

            // 6. Сохраняем отдельное событие.
            const string eventSql = """
                INSERT INTO events
                    (issue_id, message, timestamp,
                     caller, additional_data)
                VALUES
                    (@issue_id, @message, @timestamp,
                     @caller, @additional_data)
                RETURNING id;
                """;

            long eventId;

            await using (var command =
                new NpgsqlCommand(eventSql, connection, transaction))
            {
                command.Parameters.AddWithValue(
                    "issue_id", issueId);

                command.Parameters.AddWithValue(
                    "message", request.Message);

                command.Parameters.AddWithValue(
                    "timestamp", request.Timestamp.Value);

                command.Parameters.AddWithValue(
                    "caller",
                    (object?)request.Caller ?? DBNull.Value);

                var additionalDataParameter =
                    new NpgsqlParameter("additional_data", NpgsqlDbType.Jsonb)
                    {
                        Value = (object?)additionalData ?? DBNull.Value
                    };

                command.Parameters.Add(additionalDataParameter);

                eventId = Convert.ToInt64(
                    await command.ExecuteScalarAsync());
            }

            // 7. Создаём задание на отправку уведомления.
            if (level == "ERROR" || level == "WARN")
            {
                const string notificationSql = """
                    INSERT INTO notification_outbox (event_id)
                    VALUES (@event_id);
                    """;

                await using (var command =
                    new NpgsqlCommand(notificationSql, connection, transaction))
                {
                    command.Parameters.AddWithValue("event_id", eventId);
                    await command.ExecuteNonQueryAsync();
                }
            }

            // 8. Сохраняем все три операции одной транзакцией.
            await transaction.CommitAsync();

            return Ok(new
            {
                Status = "Log received",
                IssueId = issueId,
                EventId = eventId
            });
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static string NormalizeCaller(string? caller)
    {
        if (string.IsNullOrWhiteSpace(caller))
            return "";

        var value = caller.Trim();
        var lastColon = value.LastIndexOf(':');

        // Убираем номер строки, например :210.
        if (lastColon >= 0 &&
            int.TryParse(value[(lastColon + 1)..], out _))
        {
            value = value[..lastColon];
        }

        return value;
    }
}
