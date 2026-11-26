#!/bin/bash
# Проверка, что резервная копия БД LogHive действительно восстанавливается.
# Запускается на ВМ 3 раз в неделю (вручную или по cron). Боевую БД на ВМ 1 НЕ трогает:
# поднимает временный Postgres в Docker, заливает в него последнюю копию,
# считает записи в таблицах и удаляет временный контейнер.
set -euo pipefail

DIR="${DIR:-/home/user/backups/hourly}"   # где лежат копии (backup.sh)
IMAGE="${IMAGE:-postgres:18-alpine}"      # та же версия, что у БД на ВМ 1
DB_NAME="${DB_NAME:-LogHiveDB}"

FILE=$(ls -1t "$DIR"/*.sql.gz | head -n1)
echo "Проверяем копию: $FILE"

docker rm -f pg-restore-test >/dev/null 2>&1 || true
docker run -d --name pg-restore-test -e POSTGRES_PASSWORD=test "$IMAGE" >/dev/null

# ждём, пока Postgres запустится
for i in $(seq 1 30); do
  docker exec pg-restore-test pg_isready -U postgres >/dev/null 2>&1 && break
  sleep 1
done

docker exec pg-restore-test createdb -U postgres "$DB_NAME"
docker exec pg-restore-test psql -U postgres -c 'CREATE ROLE loghive' >/dev/null
gunzip -c "$FILE" | docker exec -i pg-restore-test psql -U postgres -d "$DB_NAME" -q >/dev/null

for t in users issues events notification_outbox; do
  echo -n "$t: "
  docker exec pg-restore-test psql -U postgres -d "$DB_NAME" -tAc "SELECT count(*) FROM $t"
done

docker rm -f pg-restore-test >/dev/null
echo "OK: копия восстанавливается"
