using Npgsql;

namespace LogHive.Services;

public class DatabaseCleanupService : BackgroundService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<DatabaseCleanupService> _logger;

    public DatabaseCleanupService(
        NpgsqlDataSource dataSource,
        ILogger<DatabaseCleanupService> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        // Первая очистка сразу после запуска Backend
        await CleanupAsync(stoppingToken);

        // Затем очистка каждые 24 часа
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CleanupAsync(stoppingToken);
        }
    }

    private async Task CleanupAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var connection =
                await _dataSource.OpenConnectionAsync(stoppingToken);

            await using var transaction =
                await connection.BeginTransactionAsync(stoppingToken);

            // 1. Удаляем события старше 14 дней
            await using var deleteEvents = new NpgsqlCommand(
                """
                DELETE FROM events
                WHERE timestamp < NOW() - INTERVAL '14 days';
                """,
                connection,
                transaction);

            int deletedEvents =
                await deleteEvents.ExecuteNonQueryAsync(stoppingToken);

            // 2. Удаляем ошибки, у которых не осталось событий
            await using var deleteIssues = new NpgsqlCommand(
                """
                DELETE FROM issues i
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM events e
                    WHERE e.issue_id = i.id
                );
                """,
                connection,
                transaction);

            int deletedIssues =
                await deleteIssues.ExecuteNonQueryAsync(stoppingToken);

            // 3. Пересчитываем статистику оставшихся ошибок
            await using var updateIssues = new NpgsqlCommand(
                """
                UPDATE issues i
                SET
                    count = e.event_count,
                    first_seen = e.first_seen,
                    last_seen = e.last_seen
                FROM (
                    SELECT
                        issue_id,
                        COUNT(*)::integer AS event_count,
                        MIN(timestamp) AS first_seen,
                        MAX(timestamp) AS last_seen
                    FROM events
                    GROUP BY issue_id
                ) e
                WHERE i.id = e.issue_id;
                """,
                connection,
                transaction);

            await updateIssues.ExecuteNonQueryAsync(stoppingToken);

            await transaction.CommitAsync(stoppingToken);

            _logger.LogInformation(
                "Очистка БД завершена. Удалено событий: {Events}, ошибок: {Issues}",
                deletedEvents,
                deletedIssues);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            // Завершение приложения
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при очистке базы данных");
        }
    }
}