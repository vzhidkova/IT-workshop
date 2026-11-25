
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using NpgsqlTypes;
using Microsoft.AspNetCore.Authorization;

namespace LogHive.Controllers
{
    /// <summary>
    /// Контроллер для работы с группами ошибок (Issues).
    /// </summary>
    /// <remarks>
    /// Позволяет получать список групп ошибок, просматривать информацию
    /// об отдельной группе, получать связанные с ней события
    /// и изменять статус решения ошибки.
    /// Все GET-запросы поддерживают выбор временного периода.
    /// </remarks>
    [ApiController]
    [Route("api/issues")]
    [Produces("application/json")]
    [Authorize]
    public class IssuesController : ControllerBase
    {
        private readonly NpgsqlDataSource _dataSource;

        /// <summary>
        /// Создаёт экземпляр контроллера.
        /// </summary>
        /// <param name="dataSource">
        /// Источник подключений к базе данных PostgreSQL.
        /// </param>
        public IssuesController(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        /// <summary>
        /// Преобразует значение периода в продолжительность.
        /// </summary>
        /// <param name="period">
        /// Обозначение периода: 1h, 24h или 7d.
        /// </param>
        /// <param name="duration">
        /// Продолжительность выбранного периода.
        /// </param>
        /// <returns>
        /// true, если период допустим; иначе false.
        /// </returns>
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
        /// Добавляет параметры фильтрации в SQL-запрос.
        /// </summary>
        /// <param name="command">SQL-команда.</param>
        /// <param name="since">Начало временного периода.</param>
        /// <param name="level">Уровень ошибки.</param>
        /// <param name="resolved">Статус решения ошибки.</param>
        /// <param name="search">Строка поиска.</param>
        private static void AddFilterParameters(
            NpgsqlCommand command,
            DateTime since,
            string? level,
            bool? resolved,
            string? search)
        {
            command.Parameters.AddWithValue("since", since);

            var levelParameter = command.Parameters.Add(
                "level", NpgsqlDbType.Varchar);
            levelParameter.Value = (object?)level ?? DBNull.Value;

            var resolvedParameter = command.Parameters.Add(
                "resolved", NpgsqlDbType.Boolean);
            resolvedParameter.Value = (object?)resolved ?? DBNull.Value;

            var searchParameter = command.Parameters.Add(
                "search", NpgsqlDbType.Varchar);
            searchParameter.Value = (object?)search ?? DBNull.Value;
        }

        /// <summary>
        /// Получает список групп ошибок за выбранный период.
        /// </summary>
        /// <remarks>
        /// Возвращаются только группы ошибок, у которых было хотя бы одно
        /// событие в таблице events за указанный период.
        ///
        /// Поддерживаемые периоды:
        /// - 1h — последний час;
        /// - 24h — последние 24 часа;
        /// - 7d — последние 7 дней.
        ///
        /// Доступна фильтрация по уровню ошибки, статусу решения
        /// и текстовому поиску по названию или fingerprint.
        ///
        /// Результаты сортируются по времени последнего события
        /// за выбранный период в порядке убывания.
        /// Поддерживается постраничная загрузка.
        ///
        /// Пример запроса:
        /// GET /api/issues?period=24h&amp;level=ERROR&amp;resolved=false
        /// </remarks>
        /// <param name="period">
        /// Период выборки: 1h, 24h или 7d. По умолчанию 24h.
        /// </param>
        /// <param name="level">
        /// Уровень ошибки: ERROR, WARN или INFO.
        /// Если не указан, возвращаются все уровни.
        /// </param>
        /// <param name="resolved">
        /// Статус решения: true — решённые, false — нерешённые.
        /// Если не указан, возвращаются все группы.
        /// </param>
        /// <param name="search">
        /// Поиск по названию ошибки или fingerprint.
        /// </param>
        /// <param name="limit">
        /// Максимальное количество записей в ответе (от 1 до 100).
        /// По умолчанию 20.
        /// </param>
        /// <param name="offset">
        /// Смещение для постраничной загрузки. По умолчанию 0.
        /// </param>
        /// <response code="200">
        /// Список групп ошибок успешно получен.
        /// </response>
        /// <response code="400">
        /// Передан недопустимый период или уровень ошибки.
        /// </response>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> GetIssues(
            [FromQuery] string period = "24h",
            [FromQuery] string? level = null,
            [FromQuery] bool? resolved = null,
            [FromQuery] string? search = null,
            [FromQuery] int limit = 20,
            [FromQuery] int offset = 0)
        {
            if (!TryGetPeriod(period, out TimeSpan duration))
            {
                return BadRequest(new
                {
                    Message = "Используйте period: 1h, 24h или 7d."
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

            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);

            DateTime since = DateTime.UtcNow - duration;

            const string filter = """
                i.id IN (
                    SELECT DISTINCT e.issue_id
                    FROM events e
                    WHERE e.timestamp >= @since
                )
                AND (@level::VARCHAR IS NULL OR i.level = @level)
                AND (@resolved::BOOLEAN IS NULL OR i.resolved = @resolved)
                AND (
                    @search::VARCHAR IS NULL
                    OR i.title ILIKE '%' || @search || '%'
                    OR i.fingerprint ILIKE '%' || @search || '%'
                )
                """;

            string countSql = $"""
                SELECT COUNT(*)
                FROM issues i
                WHERE {filter};
                """;

            await using var connection =
                await _dataSource.OpenConnectionAsync();

            long total;

            await using (var countCommand =
                new NpgsqlCommand(countSql, connection))
            {
                AddFilterParameters(
                    countCommand, since, level, resolved, search);

                total = (long)(await countCommand.ExecuteScalarAsync())!;
            }

            string sql = """
                SELECT
                    i.id,
                    i.fingerprint,
                    i.level,
                    i.title,
                    i.release,
                    i.resolved,
                    i.count,
                    i.first_seen,
                    i.last_seen,
                    COUNT(e.id) AS event_count,
                    MAX(e.timestamp) AS period_last_seen
                FROM issues i
                JOIN events e ON e.issue_id = i.id
                WHERE
                    e.timestamp >= @since
                    AND (@level::VARCHAR IS NULL OR i.level = @level)
                    AND (@resolved::BOOLEAN IS NULL OR i.resolved = @resolved)
                    AND (
                        @search::VARCHAR IS NULL
                        OR i.title ILIKE '%' || @search || '%'
                        OR i.fingerprint ILIKE '%' || @search || '%'
                    )
                GROUP BY i.id
                ORDER BY period_last_seen DESC
                LIMIT @limit OFFSET @offset;
                """;

            var issues = new List<object>();

            await using (var command =
                new NpgsqlCommand(sql, connection))
            {
                AddFilterParameters(
                    command, since, level, resolved, search);

                command.Parameters.AddWithValue("limit", limit);
                command.Parameters.AddWithValue("offset", offset);

                await using var reader =
                    await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    issues.Add(new
                    {
                        Id = reader.GetInt32(0),
                        Fingerprint = reader.GetString(1),
                        Level = reader.GetString(2),
                        Title = reader.GetString(3),
                        Release = reader.IsDBNull(4)
                            ? null : reader.GetString(4),
                        Resolved = reader.GetBoolean(5),
                        TotalEventCount = reader.GetInt32(6),
                        FirstSeen = reader.GetDateTime(7),
                        LastSeen = reader.GetDateTime(8),
                        EventCount = reader.GetInt64(9),
                        PeriodLastSeen = reader.GetDateTime(10)
                    });
                }
            }

            return Ok(new
            {
                Period = period,
                Total = total,
                Limit = limit,
                Offset = offset,
                Items = issues
            });
        }

        /// <summary>
        /// Получает подробную информацию об одной группе ошибок.
        /// </summary>
        /// <remarks>
        /// Возвращает основную информацию о группе, stack trace,
        /// общее количество событий и количество событий за выбранный период.
        ///
        /// Группа возвращается только в том случае, если за указанный
        /// период у неё было хотя бы одно событие.
        /// </remarks>
        /// <param name="id">Идентификатор группы ошибок.</param>
        /// <param name="period">
        /// Период выборки: 1h, 24h или 7d. По умолчанию 24h.
        /// </param>
        /// <response code="200">Информация о группе успешно получена.</response>
        /// <response code="400">Передан недопустимый период.</response>
        /// <response code="404">
        /// Группа не найдена или за выбранный период не было событий.
        /// </response>
        [HttpGet("{id:int}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetIssueById(
            int id,
            [FromQuery] string period = "24h")
        {
            if (!TryGetPeriod(period, out TimeSpan duration))
            {
                return BadRequest(new
                {
                    Message = "Используйте period: 1h, 24h или 7d."
                });
            }

            DateTime since = DateTime.UtcNow - duration;

            const string sql = """
                SELECT
                    i.id,
                    i.fingerprint,
                    i.level,
                    i.title,
                    i.stack_trace,
                    i.release,
                    i.resolved,
                    i.count,
                    i.first_seen,
                    i.last_seen,
                    COUNT(e.id) AS event_count,
                    MIN(e.timestamp) AS period_first_seen,
                    MAX(e.timestamp) AS period_last_seen
                FROM issues i
                JOIN events e ON e.issue_id = i.id
                WHERE i.id = @id
                  AND e.timestamp >= @since
                GROUP BY i.id;
                """;

            await using var connection =
                await _dataSource.OpenConnectionAsync();

            await using var command =
                new NpgsqlCommand(sql, connection);

            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("since", since);

            await using var reader =
                await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return NotFound(new
                {
                    Message = "Группа ошибок не найдена или в выбранном периоде не было событий."
                });
            }

            var issue = new
            {
                Id = reader.GetInt32(0),
                Fingerprint = reader.GetString(1),
                Level = reader.GetString(2),
                Title = reader.GetString(3),
                StackTrace = reader.IsDBNull(4)
                    ? null : reader.GetString(4),
                Release = reader.IsDBNull(5)
                    ? null : reader.GetString(5),
                Resolved = reader.GetBoolean(6),
                TotalEventCount = reader.GetInt32(7),
                FirstSeen = reader.GetDateTime(8),
                LastSeen = reader.GetDateTime(9),
                EventCount = reader.GetInt64(10),
                PeriodFirstSeen = reader.GetDateTime(11),
                PeriodLastSeen = reader.GetDateTime(12),
                Period = period
            };

            return Ok(issue);
        }

        /// <summary>
        /// Получает список событий, принадлежащих одной группе ошибок.
        /// </summary>
        /// <remarks>
        /// События выбираются из таблицы events по идентификатору группы.
        /// Возвращаются только события, созданные за указанный период.
        /// Результаты сортируются от новых к старым.
        ///
        /// Поддерживается постраничная загрузка.
        /// </remarks>
        /// <param name="id">Идентификатор группы ошибок.</param>
        /// <param name="period">
        /// Период выборки: 1h, 24h или 7d. По умолчанию 24h.
        /// </param>
        /// <param name="limit">
        /// Максимальное количество событий (от 1 до 100).
        /// По умолчанию 20.
        /// </param>
        /// <param name="offset">
        /// Смещение для постраничной загрузки. По умолчанию 0.
        /// </param>
        /// <response code="200">События успешно получены.</response>
        /// <response code="400">Передан недопустимый период.</response>
        /// <response code="404">
        /// Группа не найдена или за выбранный период нет событий.
        /// </response>
        [HttpGet("{id:int}/events")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetIssueEvents(
            int id,
            [FromQuery] string period = "24h",
            [FromQuery] int limit = 20,
            [FromQuery] int offset = 0)
        {
            if (!TryGetPeriod(period, out TimeSpan duration))
            {
                return BadRequest(new
                {
                    Message = "Используйте period: 1h, 24h или 7d."
                });
            }

            limit = Math.Clamp(limit, 1, 100);
            offset = Math.Max(offset, 0);

            DateTime since = DateTime.UtcNow - duration;

            await using var connection =
                await _dataSource.OpenConnectionAsync();

            const string countSql = """
                SELECT COUNT(*)
                FROM events
                WHERE issue_id = @id
                  AND timestamp >= @since;
                """;

            long total;

            await using (var countCommand =
                new NpgsqlCommand(countSql, connection))
            {
                countCommand.Parameters.AddWithValue("id", id);
                countCommand.Parameters.AddWithValue("since", since);

                total = (long)(await countCommand.ExecuteScalarAsync())!;
            }

            if (total == 0)
            {
                return NotFound(new
                {
                    Message = "Группа ошибок не найдена или в выбранном периоде нет событий."
                });
            }

            const string sql = """
                SELECT
                    id,
                    message,
                    stack_trace,
                    release,
                    timestamp
                FROM events
                WHERE issue_id = @id
                  AND timestamp >= @since
                ORDER BY timestamp DESC
                LIMIT @limit OFFSET @offset;
                """;

            var events = new List<object>();

            await using (var command =
                new NpgsqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("since", since);
                command.Parameters.AddWithValue("limit", limit);
                command.Parameters.AddWithValue("offset", offset);

                await using var reader =
                    await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    events.Add(new
                    {
                        Id = reader.GetInt64(0),
                        Message = reader.GetString(1),
                        StackTrace = reader.IsDBNull(2)
                            ? null : reader.GetString(2),
                        Release = reader.IsDBNull(3)
                            ? null : reader.GetString(3),
                        Timestamp = reader.GetDateTime(4)
                    });
                }
            }

            return Ok(new
            {
                Period = period,
                IssueId = id,
                Total = total,
                Limit = limit,
                Offset = offset,
                Items = events
            });
        }

        /// <summary>
        /// Отмечает группу ошибок как решённую.
        /// </summary>
        /// <remarks>
        /// Устанавливает значение resolved = true для группы
        /// с указанным идентификатором.
        ///
        /// Операция не удаляет группу и связанные с ней события.
        /// </remarks>
        /// <param name="id">Идентификатор группы ошибок.</param>
        /// <response code="200">Статус группы успешно изменён.</response>
        /// <response code="404">Группа ошибок с указанным ID не найдена.</response>
        [HttpPatch("{id:int}/resolve")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> ResolveIssue(int id)
        {
            const string sql = """
                UPDATE issues
                SET resolved = TRUE
                WHERE id = @id
                RETURNING id, resolved;
                """;

            await using var connection =
                await _dataSource.OpenConnectionAsync();

            await using var command =
                new NpgsqlCommand(sql, connection);

            command.Parameters.AddWithValue("id", id);

            await using var reader =
                await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return NotFound(new
                {
                    Message = "Группа ошибок не найдена."
                });
            }

            return Ok(new
            {
                Id = reader.GetInt32(0),
                Resolved = reader.GetBoolean(1)
            });
        }
    }
}
