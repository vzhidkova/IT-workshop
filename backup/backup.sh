#!/bin/bash
# Резервная копия БД LogHive. Запускается на ВМ 3 (Алёна) по cron:
#   backup.sh hourly — каждый час, храним 3 последние часовые копии (RPO ≤ 1 часа)
#   backup.sh daily  — раз в сутки, храним 7 суточных копий
# ВМ 3 сама заходит на ВМ 1 по SSH-ключу, делает pg_dump внутри контейнера БД
# и сохраняет сжатую копию у себя. ВМ 1 не может удалить копии на ВМ 3.
set -o pipefail

KIND="${1:-hourly}"
DB_HOST="${DB_HOST:-user@192.168.71.10}"          # пользователь и IP ВМ 1 (Лера)
DB_CONTAINER="${DB_CONTAINER:-loghive-db}" # имя контейнера PostgreSQL на ВМ 1
DB_USER="${DB_USER:-loghive}"
DB_NAME="${DB_NAME:-LogHiveDB}"
BACKUP_ROOT="${BACKUP_ROOT:-/home/user/backups}"
SSH_KEY="${SSH_KEY:-$HOME/.ssh/loghive_backup}"   # ключ из инструкции (п. 2.7)
SSH_OPTS=(-o BatchMode=yes)
[ -f "$SSH_KEY" ] && SSH_OPTS+=(-i "$SSH_KEY")

case "$KIND" in
  hourly) KEEP_FIND=(-mmin +180) ;;   # старше 3 часов — удалить
  daily)  KEEP_FIND=(-mtime +7)  ;;   # старше 7 дней — удалить
  *) echo "Использование: $0 hourly|daily"; exit 2 ;;
esac

BACKUP_DIR="$BACKUP_ROOT/$KIND"
mkdir -p "$BACKUP_DIR"
BACKUP_FILE="$BACKUP_DIR/${DB_NAME}_$(date +%Y-%m-%d_%H-%M-%S).sql.gz"

echo "$(date '+%F %T') Начинаю $KIND-бэкап базы $DB_NAME с $DB_HOST"

if ssh "${SSH_OPTS[@]}" "$DB_HOST" \
     "docker exec $DB_CONTAINER pg_dump -U $DB_USER $DB_NAME" \
   | gzip > "$BACKUP_FILE" \
   && [ "$(gzip -cd "$BACKUP_FILE" | head -c 100 | wc -c)" -gt 0 ]; then
  echo "$(date '+%F %T') Успех: $BACKUP_FILE ($(du -h "$BACKUP_FILE" | cut -f1))"
else
  echo "$(date '+%F %T') Ошибка: бэкап не создан"
  rm -f "$BACKUP_FILE"
  exit 1
fi

find "$BACKUP_DIR" -name "${DB_NAME}_*.sql.gz" -type f "${KEEP_FIND[@]}" -delete
echo "$(date '+%F %T') Старые $KIND-копии удалены"
