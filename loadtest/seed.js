// Генератор синтетических записей для учебного стенда LogHive (устав, раздел 5).
// Создаёт 20 групп (отпечатков) и TOTAL записей (по умолчанию 100 000),
// время записей случайно распределено по последним 7 дням — чтобы работали
// фильтры панели 1 ч / 24 ч / 7 дн.
//
// Почти все записи INFO: на каждую ERROR/WARN API шлёт оповещение в Mattermost,
// поэтому ERROR и WARN создаются по одной записи на группу (6 оповещений всего).
//
// Запуск на ВМ 2 (из папки с этим файлом):
//   docker run --rm --network host -v "$PWD":/scripts grafana/k6:0.54.0 run \
//     -e API=http://192.168.71.10 -e KEY=<X-Api-Key> /scripts/seed.js
import http from 'k6/http';
import { check } from 'k6';
import exec from 'k6/execution';

const API = __ENV.API || 'http://192.168.71.10';
const KEY = __ENV.KEY;
const TOTAL = parseInt(__ENV.TOTAL || '100000');

export const options = {
  scenarios: {
    seed: {
      executor: 'shared-iterations',
      vus: 10,
      iterations: TOTAL,
      maxDuration: '60m',
    },
  },
};

const INFO_GROUPS = [
  ['User logged in', 'app/user.go:120'],
  ['Post created', 'app/post.go:301'],
  ['Channel viewed', 'app/channel.go:88'],
  ['Session refreshed', 'app/session.go:54'],
  ['File uploaded', 'app/file.go:210'],
  ['Webhook received', 'app/webhook.go:77'],
  ['Job is complete', 'jobs/worker.go:45'],
  ['Cache cleared', 'app/cache.go:19'],
  ['Search executed', 'app/search.go:133'],
  ['Team member added', 'app/team.go:402'],
  ['Plugin health check ok', 'plugin/health.go:66'],
  ['Email batch sent', 'app/email.go:250'],
  ['Config reloaded', 'config/watch.go:31'],
  ['Status updated', 'app/status.go:92'],
];
const ALERT_GROUPS = [
  ['WARN', 'Slow query detected', 'store/sql.go:512'],
  ['WARN', 'Rate limit almost reached', 'app/ratelimit.go:73'],
  ['WARN', 'Disk usage above threshold', 'app/metrics.go:140'],
  ['WARN', 'Retrying webhook delivery', 'app/webhook.go:190'],
  ['ERROR', 'Failed to connect to SMTP server', 'app/email.go:301'],
  ['ERROR', 'Database query timeout', 'store/sql.go:640'],
];

// время в пределах последних 7 дней
function randomTimestamp() {
  const ms = Date.now() - Math.floor(Math.random() * 7 * 24 * 3600 * 1000);
  return new Date(ms).toISOString();
}

// примерно 2 КБ на запись (допущение A-03)
const PAD = 'x'.repeat(1800);

export default function () {
  const i = exec.scenario.iterationInTest; // общий номер записи по всем VU
  let level, msg, caller;
  if (i < ALERT_GROUPS.length) {
    [level, msg, caller] = ALERT_GROUPS[i];
  } else {
    level = 'INFO';
    [msg, caller] = INFO_GROUPS[i % INFO_GROUPS.length];
  }
  const body = JSON.stringify({
    timestamp: randomTimestamp(),
    level: level,
    msg: msg,
    caller: caller,
    request_id: `seed-${i}`,
    payload: PAD,
  });
  const res = http.post(`${API}/api/logs`, body, {
    headers: { 'Content-Type': 'application/json', 'X-Api-Key': KEY },
  });
  check(res, { 'принято (2xx)': (r) => r.status >= 200 && r.status < 300 });
}
