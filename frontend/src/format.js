// Backend отдаёт даты в UTC. Если строка пришла без указания часового пояса,
// считаем её UTC, иначе браузер прочитает её как местное время.
export function parseDate(value) {
  if (!value) return null;
  const text = String(value);
  const hasZone = /(Z|[+-]\d{2}:?\d{2})$/.test(text);
  const date = new Date(hasZone ? text : `${text}Z`);
  return Number.isNaN(date.getTime()) ? null : date;
}

export function formatDateTime(value) {
  const date = parseDate(value);
  if (!date) return '—';

  return date.toLocaleString('ru-RU', {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  });
}

export function formatShortDateTime(value) {
  const date = parseDate(value);
  if (!date) return '—';

  return date.toLocaleString('ru-RU', {
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit'
  });
}

export function formatClock(date) {
  return date.toLocaleTimeString('ru-RU', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit'
  });
}

export function timeAgo(value) {
  const date = parseDate(value);
  if (!date) return '—';

  const seconds = Math.max(0, Math.floor((Date.now() - date.getTime()) / 1000));

  if (seconds < 45) return 'только что';

  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} мин назад`;

  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} ч назад`;

  const days = Math.round(hours / 24);
  return `${days} дн. назад`;
}

// Подпись на оси графика: для часовых интервалов — время, для дневных — дата.
export function formatBucket(value, interval) {
  const date = parseDate(value);
  if (!date) return '';

  // Дневные интервалы backend считает по UTC, поэтому и подписываем по UTC,
  // иначе в часовых поясах западнее Гринвича дата сдвинется на день назад.
  if (interval === '1d') {
    return date.toLocaleDateString('ru-RU', {
      day: '2-digit',
      month: '2-digit',
      timeZone: 'UTC'
    });
  }

  return date.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}

export function formatNumber(value) {
  return typeof value === 'number' ? value.toLocaleString('ru-RU') : '—';
}

// Backend не отдаёт график по одной ошибке, поэтому строим его из списка событий:
// считаем события по суткам (UTC) за последние 7 дней, как это делает /api/analytics/timeline.
export function bucketEventsByDay(events, now = Date.now()) {
  const DAY = 24 * 60 * 60 * 1000;
  const first = Math.floor((now - 7 * DAY) / DAY) * DAY;
  const last = Math.floor(now / DAY) * DAY;
  const counts = new Map();

  events.forEach(event => {
    const date = parseDate(event.timestamp);
    if (!date) return;

    const day = Math.floor(date.getTime() / DAY) * DAY;
    counts.set(day, (counts.get(day) || 0) + 1);
  });

  const data = [];
  for (let day = first; day <= last; day += DAY) {
    data.push({
      time: formatBucket(new Date(day).toISOString(), '1d'),
      count: counts.get(day) || 0
    });
  }

  return data;
}
