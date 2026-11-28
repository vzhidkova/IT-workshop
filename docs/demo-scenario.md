# Сценарий демонстрации LogHive

Длительность: 5–7 минут. Перед началом подключиться к VPN BASIS.
Заранее открыть вкладки:
- панель LogHive — http://192.168.71.10;
- Grafana — http://192.168.71.7:3000;
- Mattermost, канал `#errors` — http://192.168.71.13:8065;
- терминал с SSH на ВМ 1.

Пароли и ключи на экран не выводить: не открывать `.env` и не показывать `cat .env`.

| № | Кто | Что показываем | Что делаем | Что должны увидеть | Требование |
|---|---|---|---|---|---|
| 1 | Валерия | Стенд работает | На ВМ 1: `cd /home/user/LogHive && docker compose ps` | 3 контейнера `Up` (backend, postgres, frontend), у БД `healthy` | Задание 3 |
| 2 | Шоди | Повторный запуск не ломает стенд | На ВМ 1 ещё раз: `docker compose up -d` | Все контейнеры `Running` / `up-to-date`, ничего не пересоздано | Задание 5 |
| 3 | Валерия | Приём и группировка | Отправить 2 одинаковые записи и 1 другую (команда ниже) | В панели 2 группы, у одной счётчик 2 | F-01 |
| 4 | Валерия | Оповещение в чат | Отправить запись ERROR | В течение минуты сообщение в `#errors` | N-01 |
| 5 | Алёна | Мониторинг ресурсов | Grafana → Node Exporter Full → ВМ 1 | CPU, RAM, диск в норме | N-08 |
| 6 | Алёна | Доступность и время отклика | Grafana → Explore: `probe_success`, `probe_duration_seconds` | `probe_success = 1` для API и Mattermost, время ответа в миллисекундах | Задание 7 |
| 7 | Алёна | Правила оповещений | Grafana → Alerting → Alert rules | Правила «API VM1 Health» и «Disk Usage» в состоянии Normal | N-05 |
| 8 | Алёна | Копии | На ВМ 3: `ls -lh /home/user/backups/hourly` | Свежие копии, последняя не старше часа | N-03 |
| 9 | Шоди | БД закрыта снаружи | С ПК: `Test-NetConnection 192.168.71.10 -Port 5432` | `TcpTestSucceeded : False` | N-07 |

## Команды для шага 3–4 (ВМ 1)

Ключ берётся из `.env` в переменную, на экран не выводится:

```bash
cd /home/user/LogHive && set -a && . ./.env && set +a
send() {  # $1 — уровень, $2 — текст
  curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost/api/logs \
    -H "X-Api-Key: $INGESTION_API_KEY" -H "Content-Type: application/json" \
    -d "{\"timestamp\":\"$(date -u +%FT%TZ)\",\"level\":\"$1\",\"msg\":\"$2\",\"caller\":\"demo/defense.go:1\"}"
}
# шаг 3: две одинаковые и одна другая -> 2 группы, у одной счётчик 2
send info "demo: disk almost full"
send info "demo: disk almost full"
send info "demo: cache miss"
# шаг 4: ERROR -> сообщение в #errors
send error "demo: payment service timeout"
```

Каждая команда должна напечатать код `2xx`. Если вернулся `401`, значит, не подхватился ключ из `.env`.
Поля запроса такие же, как в `LogEventRequest` в коде API: `timestamp`, `level`, `msg`, `caller`.

## Если что-то пошло не так

- Панель не открывается: проверить VPN, затем `docker compose ps` на ВМ 1.
- Сообщение не пришло в чат: подождать минуту (очередь outbox), проверить логи API: `docker compose logs --tail 20 backend`.
- Не показывать живое удаление БД: это не делали на рабочем стенде (см. `restore-report.md`).
