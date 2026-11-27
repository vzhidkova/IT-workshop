// Нагрузочный тест LogHive по плану устава (раздел 5, требование N-04).
//   0–2 мин   прогрев:          1 запись/с + 2 запроса чтения/с
//   2–12 мин  обычный режим:    1 запись/с + 2 запроса чтения/с   (измерение)
//   12–17 мин пик:              50 записей/с + 2 запроса чтения/с (измерение)
// Критерии успеха: p95 приёма ≤ 300 мс, p95 списка групп ≤ 500 мс, ошибок ≤ 1%.
// Записи отправляются с уровнем INFO, чтобы не заваливать канал errors оповещениями.
//
// Запуск на ВМ 2 (из папки с этим файлом), занимает ~17 минут:
//   docker run --rm --network host -v "$PWD":/scripts grafana/k6:0.54.0 run \
//     -e API=http://192.168.71.10 -e KEY=<X-Api-Key> \
//     -e USER=root -e PASS=<пароль root панели> \
//     --summary-export /scripts/result.json /scripts/loadtest.js | tee result.txt
// Во время теста смотрите CPU/RAM ВМ 1 в Grafana (N-08).
import http from 'k6/http';
import { check, fail } from 'k6';

const API = __ENV.API || 'http://192.168.71.10';
const KEY = __ENV.KEY;
const PAD = 'x'.repeat(1800); // ~2 КБ на запись (A-03)

export const options = {
  scenarios: {
    warmup_write: {
      executor: 'constant-arrival-rate', exec: 'write',
      rate: 1, timeUnit: '1s', duration: '2m',
      preAllocatedVUs: 2, maxVUs: 10, tags: { phase: 'warmup' },
    },
    warmup_read: {
      executor: 'constant-arrival-rate', exec: 'read',
      rate: 2, timeUnit: '1s', duration: '2m',
      preAllocatedVUs: 2, maxVUs: 10, tags: { phase: 'warmup' },
    },
    normal_write: {
      executor: 'constant-arrival-rate', exec: 'write', startTime: '2m',
      rate: 1, timeUnit: '1s', duration: '10m',
      preAllocatedVUs: 2, maxVUs: 10, tags: { phase: 'normal' },
    },
    normal_read: {
      executor: 'constant-arrival-rate', exec: 'read', startTime: '2m',
      rate: 2, timeUnit: '1s', duration: '10m',
      preAllocatedVUs: 2, maxVUs: 10, tags: { phase: 'normal' },
    },
    peak_write: {
      executor: 'constant-arrival-rate', exec: 'write', startTime: '12m',
      rate: 50, timeUnit: '1s', duration: '5m',
      preAllocatedVUs: 20, maxVUs: 100, tags: { phase: 'peak' },
    },
    peak_read: {
      executor: 'constant-arrival-rate', exec: 'read', startTime: '12m',
      rate: 2, timeUnit: '1s', duration: '5m',
      preAllocatedVUs: 2, maxVUs: 10, tags: { phase: 'peak' },
    },
  },
  // Критерии считаются только по измеряемым фазам (без прогрева)
  thresholds: {
    'http_req_duration{op:write,phase:normal}': ['p(95)<300'],
    'http_req_duration{op:write,phase:peak}': ['p(95)<300'],
    'http_req_duration{op:list,phase:normal}': ['p(95)<500'],
    'http_req_duration{op:list,phase:peak}': ['p(95)<500'],
    'http_req_failed{phase:normal}': ['rate<0.01'],
    'http_req_failed{phase:peak}': ['rate<0.01'],
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'max'],
};

// один раз входим в панель и получаем токен для запросов чтения
export function setup() {
  const res = http.post(`${API}/api/auth/login`,
    JSON.stringify({ username: __ENV.USER || 'root', password: __ENV.PASS }),
    { headers: { 'Content-Type': 'application/json' } });
  if (res.status !== 200) fail(`Вход не удался: ${res.status} ${res.body}`);
  const token = res.json('accessToken');
  // номера существующих групп — для запросов карточки
  const list = http.get(`${API}/api/issues?period=7d&limit=50`,
    { headers: { Authorization: `Bearer ${token}` } });
  let ids = [];
  try { ids = list.json('items.#.id') || []; } catch (e) { ids = []; }
  if (ids.length === 0) fail('Список групп пуст: сначала запустите seed.js');
  return { token: token, ids: ids };
}

// приём записи; при пике — несколько повторяющихся ошибок «массового сбоя»
export function write() {
  const n = Math.floor(Math.random() * 5);
  const body = JSON.stringify({
    timestamp: new Date().toISOString(),
    level: 'INFO',
    msg: `Load test message ${n}`,
    caller: `loadtest/app.go:${100 + n}`,
    payload: PAD,
  });
  const res = http.post(`${API}/api/logs`, body, {
    headers: { 'Content-Type': 'application/json', 'X-Api-Key': KEY },
    tags: { op: 'write' },
  });
  check(res, { 'запись принята': (r) => r.status >= 200 && r.status < 300 });
}

// чтение панели по профилю устава: список групп 60%, карточка 30%, график 10%
export function read(data) {
  const params = { headers: { Authorization: `Bearer ${data.token}` } };
  const r = Math.random();
  if (r < 0.6) {
    const res = http.get(`${API}/api/issues?period=24h&limit=20`,
      Object.assign({ tags: { op: 'list' } }, params));
    check(res, { 'список групп 200': (x) => x.status === 200 });
  } else if (r < 0.9) {
    const id = data.ids[Math.floor(Math.random() * data.ids.length)];
    const res = http.get(`${API}/api/issues/${id}`,
      Object.assign({ tags: { op: 'detail' } }, params));
    check(res, { 'карточка 200': (x) => x.status === 200 });
  } else {
    const res = http.get(`${API}/api/analytics/timeline?period=24h`,
      Object.assign({ tags: { op: 'chart' } }, params));
    check(res, { 'график 200': (x) => x.status === 200 });
  }
}
