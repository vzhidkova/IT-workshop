using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Microsoft.AspNetCore.Authorization;

namespace LogHive.Controllers
{
    [ApiController]
    [Route("api/analytics")]
    [Authorize]
    public class AnalyticsController : ControllerBase
    {
        private readonly NpgsqlDataSource _dataSource;

        public AnalyticsController(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        private static bool TryGetPeriod(
        string period,
        out TimeSpan duration)
        {
            duration = period switch
            {
                "1h" => TimeSpan.FromHours(1),
                "24h" => TimeSpan.FromHours(24),
                "7d" => TimeSpan.FromDays(7),
                _ => TimeSpan.Zero
            };

            return duration != TimeSpan.Zero;
        }

        /// <summary>
        /// Общая статистика за период.
        /// </summary>
        /// <remarks>
        /// Возвращает общее кол-во ошибок, кол-во групп ошибок: решенные, актульные, всего.
        ///
        /// Поддерживаемые периоды:
        /// - 1h — последний час;
        /// - 24h — последние сутки;
        /// - 7d — последняя неделя.
        ///
        /// Доступна фильтрация по уровню ошибки и статусу решения.
        /// </remarks>
        /// <param name="period">Период: 1h, 24h или 7d.</param>
        /// <param name="level">Уровень: ERROR, WARN или INFO.</param>
        /// <param name="resolved">Статус решения: true или false.</param>
        /// <response code="200">Статистика успешно получена.</response>
        /// <response code="400">Переданы некорректные параметры.</response>
        [HttpGet("summary")]
        public async Task<IActionResult> GetSummary(
        [FromQuery] string period = "24h",
        [FromQuery] string? level = null,
        [FromQuery] bool? resolved = null)
        {
            if (!TryGetPeriod(period, out TimeSpan duration))
            {
                return BadRequest(new
                {
                    Message = "Недопустимый период. Используйте: 1h, 24h или 7d."
                });
            }

            if (level != null)
            {
                level = level.ToUpperInvariant();

                if (level != "ERROR" &&
                    level != "WARN" &&
                    level != "INFO")
                {
                    return BadRequest(new
                    {
                        Message = "Недопустимый уровень. Используйте ERROR, WARN или INFO."
                    });
                }
            }

            const string sql = """
        
        WITH filtered_events AS (
            SELECT
                e.id,
                e.issue_id,
                i.level
            FROM events e
            JOIN issues i ON i.id = e.issue_id
            WHERE e.timestamp >= @since
              AND (@level::VARCHAR IS NULL OR i.level = @level)
              AND (@resolved::BOOLEAN IS NULL OR i.resolved = @resolved)
        ),
        active_issues AS (
            SELECT DISTINCT issue_id
            FROM filtered_events
        )
        SELECT
            -- Общее количество событий за период
            (SELECT COUNT(*)
             FROM filtered_events) AS total_events,

            -- Количество уникальных групп ошибок за период
            (SELECT COUNT(*)
             FROM active_issues) AS total_issues,

            -- Количество нерешённых групп
            (SELECT COUNT(*)
             FROM active_issues ai
             JOIN issues i ON i.id = ai.issue_id
             WHERE i.resolved = FALSE) AS unresolved_issues,

            -- Количество решённых групп
            (SELECT COUNT(*)
             FROM active_issues ai
             JOIN issues i ON i.id = ai.issue_id
             WHERE i.resolved = TRUE) AS resolved_issues,

            -- Количество ERROR-событий
            (SELECT COUNT(*)
             FROM filtered_events
             WHERE level = 'ERROR') AS error_count,

            -- Количество WARN-событий
            (SELECT COUNT(*)
             FROM filtered_events
             WHERE level = 'WARN') AS warning_count,

            -- Количество INFO-событий
            (SELECT COUNT(*)
             FROM filtered_events
             WHERE level = 'INFO') AS info_count;
        
        """;

            await using var connection =
                await _dataSource.OpenConnectionAsync();

            await using var command =
                new NpgsqlCommand(sql, connection);

            command.Parameters.Add(new NpgsqlParameter("level", NpgsqlTypes.NpgsqlDbType.Varchar)
            {
                Value = (object?)level ?? DBNull.Value
            });
            command.Parameters.Add(new NpgsqlParameter("resolved", NpgsqlTypes.NpgsqlDbType.Boolean)
            {
                Value = (object?)resolved ?? DBNull.Value
            });
            command.Parameters.Add(new NpgsqlParameter("since", NpgsqlTypes.NpgsqlDbType.TimestampTz)
            {
                Value = DateTime.UtcNow - duration
            });

            await using var reader =
                await command.ExecuteReaderAsync();

            await reader.ReadAsync();

            return Ok(new
            {
                Period = period,
                TotalEvents = reader.GetInt64(0),
                TotalIssues = reader.GetInt64(1),
                UnresolvedIssues = reader.GetInt64(2),
                ResolvedIssues = reader.GetInt64(3),
                ErrorCount = reader.GetInt64(4),
                WarningCount = reader.GetInt64(5),
                InfoCount = reader.GetInt64(6)
            });
        }

        /// <summary>
        /// Данные для графика за выбранный период.
        /// </summary>
        /// <remarks>
        /// Возвращает общее кол-во логов за шаг времни в выбранном врменном интревале.
        /// Час - каждые 10 мин. Сутки - каждый час. Неделя - каждый день.
        ///
        /// Поддерживаемые периоды:
        /// - 1h — последний час;
        /// - 24h — последние сутки;
        /// - 7d — последняя неделя.
        ///
        /// Доступна фильтрация по уровню ошибки и статусу решения.
        /// </remarks>
        /// <param name="period">Период: 1h, 24h или 7d.</param>
        /// <param name="level">Уровень: ERROR, WARN или INFO.</param>
        /// <param name="resolved">Статус решения: true или false.</param>
        /// <response code="200">Статистика успешно получена.</response>
        /// <response code="400">Переданы некорректные параметры.</response>
        [HttpGet("timeline")]
        public async Task<IActionResult> GetTimeline(
        [FromQuery] string period = "24h",
        [FromQuery] string? level = null,
        [FromQuery] bool? resolved = null)
        {
            if (!TryGetPeriod(period, out TimeSpan duration))
            {
                return BadRequest(new
                {
                    Message = "Недопустимый период. Используйте: 1h, 24h или 7d."
                });
            }

            if (level != null)
            {
                level = level.ToUpperInvariant();

                if (level != "ERROR" &&
                    level != "WARN" &&
                    level != "INFO")
                {
                    return BadRequest(new
                    {
                        Message = "Недопустимый уровень. Используйте ERROR, WARN или INFO."
                    });
                }
            }

            TimeSpan interval = period switch
            {
                "1h" => TimeSpan.FromMinutes(10),
                "24h" => TimeSpan.FromHours(1),
                "7d" => TimeSpan.FromDays(1),
                _ => TimeSpan.Zero
            };

            const string sql = """
        
        WITH time_intervals AS (
            SELECT generate_series(
                date_bin(
                    @interval,
                    @since,
                    TIMESTAMPTZ '2000-01-01 00:00:00+00'
                ),
                date_bin(
                    @interval,
                    now(),
                    TIMESTAMPTZ '2000-01-01 00:00:00+00'
                ),
                @interval
            ) AS time_period
        ),
        event_counts AS (
            SELECT
                date_bin(
                    @interval,
                    e.timestamp,
                    TIMESTAMPTZ '2000-01-01 00:00:00+00'
                ) AS time_period,
                COUNT(*) AS event_count
            FROM events e
            JOIN issues i ON i.id = e.issue_id
            WHERE e.timestamp >= @since
              AND (@level::VARCHAR IS NULL OR i.level = @level)
              AND (@resolved::BOOLEAN IS NULL OR i.resolved = @resolved)
            GROUP BY time_period
        )
        SELECT
            ti.time_period,
            COALESCE(ec.event_count, 0) AS event_count
        FROM time_intervals ti
        LEFT JOIN event_counts ec
            ON ti.time_period = ec.time_period
        ORDER BY ti.time_period;
        
        """;

            await using var connection =
                await _dataSource.OpenConnectionAsync();

            await using var command =
                new NpgsqlCommand(sql, connection);

            command.Parameters.AddWithValue(
                "since",
                DateTime.UtcNow - duration);

            command.Parameters.Add(new NpgsqlParameter("interval", NpgsqlTypes.NpgsqlDbType.Interval)
            {
                Value = interval
            });
            command.Parameters.Add(new NpgsqlParameter("level", NpgsqlTypes.NpgsqlDbType.Varchar)
            {
                Value = (object?)level ?? DBNull.Value
            });
            command.Parameters.Add(new NpgsqlParameter("resolved", NpgsqlTypes.NpgsqlDbType.Boolean)
            {
                Value = (object?)resolved ?? DBNull.Value
            });

            var timeline = new List<object>();

            await using var reader =
                await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                timeline.Add(new
                {
                    Time = reader.GetDateTime(0),
                    EventCount = reader.GetInt64(1)
                });
            }

            return Ok(new
            {
                Period = period,
                Interval = period switch
                {
                    "1h" => "10m",
                    "24h" => "1h",
                    "7d" => "1d",
                    _ => ""
                },
                Data = timeline
            });
        }


    }

}
