# LogHive: Инструкция администратора

## 0. Что нужно знать заранее

| ВМ   | Что на ней                                                      | Адрес для людей (через VPN)          | Владелец      |
| ---- | --------------------------------------------------------------- | ------------------------------------ | ------------- |
| ВМ 1 | nginx с веб-панелью, API LogHive, PostgreSQL, node-exporter     | `http://192.168.71.10` (порт 80)     | Жидкова В.    |
| ВМ 2 | Mattermost и его PostgreSQL, агент сбора логов, node-exporter   | `http://192.168.71.13:8065`          | Икромжонов Ш. |
| ВМ 3 | Prometheus, Grafana, blackbox-exporter, node-exporter, копии БД | `http://192.168.71.7:3000` (Grafana) | Иванова А.    |

Каждая ВМ: Ubuntu Server 24.04, 1 vCPU, 4 ГБ RAM, 20 ГБ диска. Всё приложение работает в Docker. Доступ к стенду только через VPN BASIS.

## 1. Подключиться к стенду

1. Включите профиль OpenVPN из BASIS. Проверка: `ping 192.168.71.10` должен отвечать.
2. Зайдите на нужную ВМ: `ssh user@192.168.71.10` (для ВМ 2 и ВМ 3 так же со своим IP).
3. Убедитесь, что попали на ту машину: `hostname`.
4. Если SSH не пускает, а VPN работает, откройте консоль ВМ в веб-интерфейсе BASIS: она работает без сети.
5. Быстрая проверка, что стенд жив, с вашего компьютера через VPN:

```
curl -i http://192.168.71.10/api/health
curl -i http://192.168.71.13:8065/api/v4/system/ping
```

Оба запроса должны вернуть код 200. Веб-панель LogHive: `http://192.168.71.10`, вход как `root`. Grafana: `http://192.168.71.7:3000`.

## 2. Развернуть всё с нуля на чистых ВМ

Цель по Уставу: уложиться в 30 минут без ручных доработок. Порядок: ВМ 1, затем ВМ 2, затем ВМ 3, чтобы мониторингу было что опрашивать.

### 2.1 Проверка перед началом (на каждой ВМ)

```
free -h
df -h /
ping -c 3 <IP_другой_ВМ>
```

Ожидается около 4 ГБ RAM и около 20 ГБ диска. Если памяти 512 МБ вместо 4 ГБ (так уже бывало), установка оборвётся: остановитесь и сообщите преподавателю. ВМ должны видеть друг друга по внутренней сети.

### 2.2 Пакеты и репозиторий (на каждой ВМ)

```
sudo apt update && sudo apt install -y git docker.io docker-compose-v2 ufw
sudo systemctl enable --now docker
sudo usermod -aG docker $USER
```

Выйдите и зайдите по SSH снова (чтобы группа docker применилась), затем:

```
git clone https://github.com/vzhidkova/IT-workshop.git ~/IT-workshop
```

### 2.3 Межсетевой экран ufw

Сначала всегда разрешайте 22, иначе потеряете SSH. ВМ 1:

```
sudo ufw allow 22/tcp
sudo ufw allow 80/tcp
sudo ufw allow from <IP_ВМ3> to any port 9100 proto tcp
sudo ufw enable
```

ВМ 2:

```
sudo ufw allow 22/tcp
sudo ufw allow 8065/tcp
sudo ufw allow from <IP_ВМ3> to any port 9100 proto tcp
sudo ufw enable
```

ВМ 3:

```
sudo ufw allow 22/tcp
sudo ufw allow 3000/tcp
sudo ufw enable
```

Docker публикует порты в обход ufw. Поэтому в compose-файлах наружу публикуются только порты из Устава; проверьте командой `docker ps`, что у PostgreSQL, API и Prometheus нет публикации наружу.

### 2.4 ВМ 1: API, база, сайт

```
cd ~/IT-workshop/<подкаталог_ВМ1>
cp .env.example .env
chmod 600 .env
nano .env
```

```
docker compose up -d --build
docker compose ps
curl -i http://localhost/api/health
```

Таблицы и начальные данные создаются сами файлом `init.sql` при первом запуске базы. Сборка образа API на 4 ГБ RAM занимает до 1,5 ГБ, не запускайте в это время ничего тяжёлого. Откройте `http://192.168.71.10` и войдите как `root`.

### 2.5 ВМ 2: Mattermost и агент логов

```
cd ~/IT-workshop/<подкаталог_ВМ2>
cp .env.example .env
nano .env
docker compose up -d
docker compose ps
```

Откройте `http://192.168.71.13:8065`, создайте администратора, команду и канал `errors`. Затем: Интеграции, Входящие вебхуки, Добавить, выбрать канал `errors`, скопировать адрес вебхука. Он понадобится в двух местах: в `.env` на ВМ 1 и в Grafana (п. 2.6).

Вернитесь на ВМ 1, впишите адрес вебхука в `.env` и примените:

```
cd ~/IT-workshop/<подкаталог_ВМ1>
docker compose up -d
```

Проверка агента: `docker compose logs --tail=50` на ВМ 2 не должен показывать ошибок отправки, а в панели LogHive должны появиться записи Mattermost.

### 2.6 ВМ 3: мониторинг

```
cd ~/IT-workshop/<подкаталог_ВМ3>
docker compose up -d
docker compose ps
```

Prometheus каждые 15 секунд опрашивает node-exporter на трёх ВМ и через blackbox-exporter проверяет `http://192.168.71.10/api/health` и `http://192.168.71.13:8065/api/v4/system/ping`. Срок хранения метрик 15 дней. Проверка в Grafana (Explore, источник Prometheus): запрос `up` даёт 1 по всем ВМ, запрос `probe_success` даёт 1 по API и Mattermost.

Оповещения: в Grafana откройте Alerting, Contact points, добавьте точку с адресом вебхука Mattermost, нажмите Test. Должны быть правила: диск больше 80% и `probe_success` для API равен 0 дольше 1 минуты.

### 2.7 ВМ 3: резервное копирование

ВМ 3 заходит на ВМ 1 по SSH-ключу. Создайте ключ и проверьте вход:

```
ssh-keygen -t ed25519 -f ~/.ssh/loghive_backup -N ''
ssh-copy-id -i ~/.ssh/loghive_backup.pub <пользователь>@<IP_ВМ1>
ssh -i ~/.ssh/loghive_backup <пользователь>@<IP_ВМ1> hostname
```

Скрипт копирования лежит в репозитории: `~/IT-workshop/backup/backup.sh`. Он делает `pg_dump` в контейнере БД на ВМ 1, сжимает и сохраняет файл на ВМ 3, оставляя 3 часовые и 7 суточных копий. Запустите его вручную и проверьте результат:

```
~/IT-workshop/backup/backup.sh
ls -lh <КАТАЛОГ_КОПИЙ>
```

Новый файл должен быть не пустым (порядка десятков мегабайт на учебном наборе). Расписание: `crontab -e`, строка для часовой копии:

```
0 * * * * ~/IT-workshop/backup/backup.sh >> ~/backup.log 2>&1
```

Как скрипт различает часовые и суточные копии, смотрите в самом скрипте; если суточная копия там не предусмотрена, добавьте вторую строку cron для неё.

## 3. Запустить, остановить, перезапустить сервисы

В каталоге нужной ВМ (`cd ~/IT-workshop/<подкаталог_ВМ>`):

```
docker compose ps                              # что запущено
docker compose config --services               # имена сервисов
docker compose up -d                           # запустить всё
docker compose stop                            # остановить всё, данные остаются
docker compose restart <сервис>                # перезапустить один сервис
docker compose logs --tail=100 -f <сервис>     # смотреть логи, выход Ctrl+C
```

Не используйте `docker compose down -v`: ключ `-v` удаляет тома, то есть базу данных. Исключение только учебный отказ.

Перезагрузка всей ВМ: `sudo reboot`. После старта подождите минуту, зайдите снова и проверьте `docker compose ps`. Порядок при ручной остановке на ВМ 1: сначала API, потом база. Запускать можно командой `docker compose up -d`, порядок она соблюдает сама.

## 4. Восстановить базу из копии

Цель: простой не более 30 минут (RTO), потеря данных не более 1 часа (RPO). Запишите время начала.

На ВМ 3 выберите копию (самая свежая сверху) и отправьте её на ВМ 1:

```
ls -lt <КАТАЛОГ_КОПИЙ> | head
scp -i ~/.ssh/loghive_backup <КАТАЛОГ_КОПИЙ>/<файл> <пользователь>@<IP_ВМ1>:/tmp/restore.sql.gz
```

На ВМ 1:

```
cd ~/IT-workshop/<подкаталог_ВМ1>
docker compose stop <сервис_API>
gunzip -c /tmp/restore.sql.gz | head -n 5
```

В первых строках должен быть текст `PostgreSQL database dump`. Если вместо текста мусор, копия сделана в другом формате: используйте `pg_restore`. Если всё в порядке, пересоздайте пустую базу и загрузите копию:

```
docker exec $DB dropdb -U loghive --force --if-exists LogHiveDB
docker exec $DB createdb -U loghive LogHiveDB
gunzip -c /tmp/restore.sql.gz | docker exec -i $DB psql -U loghive -d LogHiveDB -q
```

Проверьте, что данные на месте, и запустите API:

```
for t in users issues events notification_outbox; do echo -n "$t: "; docker exec $DB psql -U loghive -d LogHiveDB -tAc "SELECT count(*) FROM $t"; done
docker compose start <сервис_API>
curl -i http://localhost/api/health
rm /tmp/restore.sql.gz
```

Откройте панель, войдите, откройте любую группу. Запишите время окончания (это RTO) и время копии (потеря данных не больше разницы между сбоем и временем копии). Если копия не загружается, повторите с предыдущей.

### Еженедельная проверка восстановления (ВМ 3)

Сохраните как `~/restore-test.sh`. Версию образа Postgres возьмите такую же, как у БД на ВМ 1 (`docker exec $DB postgres --version`).

```
#!/bin/bash
set -euo pipefail
DIR=<КАТАЛОГ_КОПИЙ>
IMAGE=postgres:16-alpine
FILE=$(ls -1t "$DIR"/*.gz | head -n1)
echo "Проверяем: $FILE"
docker rm -f pg-restore-test >/dev/null 2>&1 || true
docker run -d --name pg-restore-test -e POSTGRES_PASSWORD=test "$IMAGE" >/dev/null
sleep 10
docker exec pg-restore-test createdb -U postgres LogHiveDB
docker exec pg-restore-test psql -U postgres -c 'CREATE ROLE loghive' >/dev/null
gunzip -c "$FILE" | docker exec -i pg-restore-test psql -U postgres -d LogHiveDB -q >/dev/null
for t in users issues events notification_outbox; do
  echo -n "$t: "
  docker exec pg-restore-test psql -U postgres -d LogHiveDB -tAc "SELECT count(*) FROM $t"
done
docker rm -f pg-restore-test >/dev/null
echo "OK: копия восстанавливается"
```

Запуск: `chmod +x ~/restore-test.sh && ~/restore-test.sh`. Успех: скрипт дошёл до строки OK, во всех четырёх таблицах есть записи (в users как минимум root). Результат внесите в журнал работ регламента.

## 5. Поменять пароль или ключ

Все значения лежат в `.env` на соответствующей ВМ. После правки `.env` всегда выполняйте `docker compose up -d` в том же каталоге: изменённый сервис пересоздастся.

**Ключ подписи входа (JWT), ВМ 1.** Получите новый: `openssl rand -base64 48`, впишите в `.env`, примените. Все пользователи будут разлогинены, это ожидаемо.

**Пароль root для панели, ВМ 1.** Пользователь root создаётся при запуске только если его ещё нет, поэтому смена пароля в `.env` сама ничего не меняет. Удалите старую запись и перезапустите API:

```
docker exec $DB psql -U loghive -d LogHiveDB -c "DELETE FROM users WHERE username='root'"
nano .env
docker compose up -d
```

Если команда вернула ошибку про внешние ключи, остановитесь и позовите владельца ВМ 1.

**Пароль БД LogHive, ВМ 1.**

```
docker exec $DB psql -U loghive -d postgres -c "ALTER USER loghive PASSWORD '<новый>'"
nano .env
docker compose up -d
```

Впишите новый пароль и в строку подключения, если она в `.env` отдельная. Затем с ВМ 3 запустите `backup/backup.sh` и убедитесь, что копия не пустая.

**Вебхук Mattermost.** В Mattermost создайте новый входящий вебхук на канал `errors`, старый удалите. Новый адрес впишите в двух местах: `.env` на ВМ 1 (затем `docker compose up -d`) и в Grafana (Alerting, Contact points, затем Test). Проверка:

```
curl -X POST -H 'Content-Type: application/json' -d '{"text":"проверка вебхука"}' '<адрес_вебхука>'
```

В канале `errors` должно появиться сообщение.

**Ключ приёма записей (X-Api-Key).** Замените его в `.env` на ВМ 1 и в настройках агента на ВМ 2, перезапустите оба сервиса, а также обновите ключ во всех приложениях, которые отправляют логи. Проверка: в панели появляется новая запись.

**Пароль Grafana, ВМ 3.** В каталоге мониторинга: `docker compose exec grafana grafana cli admin reset-admin-password '<новый>'`.

**SSH-ключ копирования, ВМ 3.** Создайте новую пару (п. 2.7), затем на ВМ 1 удалите старый ключ из `~/.ssh/authorized_keys` и проверьте вход новым.

## 6. Добавить новую ВМ в мониторинг

На новой ВМ запустите node-exporter и откройте порт только для ВМ 3:

```
docker run -d --name node-exporter --restart unless-stopped --network host --pid host -v /:/host:ro,rslave prom/node-exporter --path.rootfs=/host
sudo ufw allow from <IP_ВМ3> to any port 9100 proto tcp
curl -s localhost:9100/metrics | head -n 3
```

На ВМ 3 в каталоге мониторинга откройте `prometheus.yml`, добавьте `'<IP_новой_ВМ>:9100'` в список целей задания с node-exporter и перезапустите Prometheus:

```
docker compose restart prometheus
```

Если на новой ВМ есть сервис с адресом проверки (как `/api/health`), добавьте его адрес в цели blackbox-exporter тем же способом. Проверка в Grafana (Explore): запрос `up{instance="<IP_новой_ВМ>:9100"}` должен вернуть 1. Выберите новую ВМ на панели, убедитесь, что правило оповещения о диске её охватывает. В конце впишите ВМ и её владельца в таблицу регламента и в раздел 0 этого документа.
