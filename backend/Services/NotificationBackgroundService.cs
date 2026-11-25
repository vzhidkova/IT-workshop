
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LogHive.Services;

public class NotificationBackgroundService : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationBackgroundService> _logger;

    private const int MaxAttempts = 8;

    public NotificationBackgroundService(
        NpgsqlDataSource dataSource,
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationBackgroundService> logger)
    {
        _dataSource = dataSource;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Notification background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var notification = await GetNextNotificationAsync(
                    stoppingToken);

                if (notification == null)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(1),
                        stoppingToken);

                    continue;
                }

                await ProcessNotificationAsync(
                    notification,
                    stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error while reading notification queue");

                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    stoppingToken);
            }
        }

        _logger.LogInformation(
            "Notification background service stopped");
    }

    private async Task ProcessNotificationAsync(
        NotificationTask notification,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = await GetNotificationMessageAsync(
                notification.EventId,
                cancellationToken);

            using var scope = _scopeFactory.CreateScope();

            var notificationService =
                scope.ServiceProvider
                    .GetRequiredService<NotificationService>();

            await notificationService.SendNotificationAsync(
                message,
                cancellationToken);

            await MarkAsSentAsync(
                notification.Id,
                cancellationToken);

            _logger.LogInformation(
                "Notification for event {EventId} sent",
                notification.EventId);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to send notification for event {EventId}",
                notification.EventId);

            await ScheduleRetryOrFailAsync(
                notification,
                ex.Message,
                cancellationToken);
        }
    }

    private async Task<NotificationTask?> GetNextNotificationAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH selected AS (
                SELECT id
                FROM notification_outbox
                WHERE
                    (
                        status = 'pending'
                        AND next_attempt_at <= now()
                    )
                    OR
                    (
                        status = 'processing'
                        AND locked_until <= now()
                    )
                ORDER BY next_attempt_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE notification_outbox AS n
            SET
                status = 'processing',
                attempts = n.attempts + 1,
                locked_until = now() + interval '2 minutes'
            FROM selected
            WHERE n.id = selected.id
            RETURNING n.id, n.event_id, n.attempts;
            """;

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using var command =
                new NpgsqlCommand(sql, connection, transaction);

            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);

            NotificationTask? result = null;

            if (await reader.ReadAsync(cancellationToken))
            {
                result = new NotificationTask(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetInt32(2));
            }

            await reader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);

            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<string> GetNotificationMessageAsync(
        long eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                i.level,
                i.title,
                e.message,
                e.timestamp,
                e.caller,
                e.additional_data
            FROM events e
            JOIN issues i ON i.id = e.issue_id
            WHERE e.id = @event_id;
            """;

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var command =
            new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("event_id", eventId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Event {eventId} was not found");
        }

        var level = reader.GetString(0);
        var title = reader.GetString(1);
        var message = reader.GetString(2);
        var timestamp = reader.GetDateTime(3);

        var caller = reader.IsDBNull(4)
            ? null
            : reader.GetString(4);

        var additionalData = reader.IsDBNull(5)
            ? null
            : reader.GetString(5);

        var result = new StringBuilder();

        result.AppendLine($"### LogHive — {level}");
        result.AppendLine($"**Проблема:** {title}");
        result.AppendLine(
            $"**Время:** {timestamp.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");

        if (!string.IsNullOrWhiteSpace(caller))
            result.AppendLine($"**Источник:** {caller}");

        if (!string.IsNullOrWhiteSpace(additionalData))
        {
            result.AppendLine(
                $"**Дополнительные данные:** ```json\n{additionalData}\n```");
        }

        return result.ToString();
    }

    private async Task MarkAsSentAsync(
        long notificationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE notification_outbox
            SET
                status = 'sent',
                sent_at = now(),
                locked_until = NULL,
                last_error = NULL
            WHERE id = @id;
            """;

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var command =
            new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("id", notificationId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ScheduleRetryOrFailAsync(
        NotificationTask notification,
        string error,
        CancellationToken cancellationToken)
    {
        var isFinalAttempt = notification.Attempts >= MaxAttempts;

        var delaySeconds = Math.Min(
            900,
            10 * Math.Pow(2, notification.Attempts - 1));

        var nextAttempt = DateTimeOffset.UtcNow.AddSeconds(
            delaySeconds);

        const string sql = """
            UPDATE notification_outbox
            SET
                status = @status,
                next_attempt_at = @next_attempt,
                locked_until = NULL,
                last_error = @error
            WHERE id = @id;
            """;

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var command =
            new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "status",
            isFinalAttempt ? "failed" : "pending");

        command.Parameters.AddWithValue(
            "next_attempt",
            nextAttempt);

        command.Parameters.AddWithValue(
            "error",
            error.Length > 4000 ? error[..4000] : error);

        command.Parameters.AddWithValue(
            "id",
            notification.Id);

        await command.ExecuteNonQueryAsync(cancellationToken);

        if (isFinalAttempt)
        {
            _logger.LogError(
                "Notification {NotificationId} failed after {Attempts} attempts",
                notification.Id,
                notification.Attempts);
        }
        else
        {
            _logger.LogWarning(
                "Notification {NotificationId} will be retried at {NextAttempt}",
                notification.Id,
                nextAttempt);
        }
    }

    private sealed record NotificationTask(
        long Id,
        long EventId,
        int Attempts);
}
