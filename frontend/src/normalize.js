// Приведение ответов backend к единому виду.
//
// В документации API список ошибок описан массивом с полем count, а в Swagger/коде
// backend он может приходить как { total, items } с полями eventCount и periodLastSeen.
// Принимаем оба варианта, чтобы интерфейс не зависел от версии backend.

export function normalizeIssue(raw) {
  if (!raw) return null;

  const eventCount = raw.eventCount ?? raw.count ?? 0;

  return {
    id: raw.id,
    fingerprint: raw.fingerprint,
    level: raw.level,
    title: raw.title,
    release: raw.release ?? null,
    stackTrace: raw.stackTrace ?? null,
    resolved: Boolean(raw.resolved),
    firstSeen: raw.firstSeen ?? null,
    lastSeen: raw.lastSeen ?? null,
    // События за выбранный период (или общее количество, если backend другого не отдаёт).
    eventCount,
    // События за всё время.
    totalEventCount: raw.totalEventCount ?? raw.count ?? eventCount,
    // Последнее появление за выбранный период.
    periodLastSeen: raw.periodLastSeen ?? raw.lastSeen ?? null
  };
}

// total === null означает, что backend не сообщил общее количество.
export function normalizeIssuesResponse(data) {
  if (Array.isArray(data)) {
    return { items: data.map(normalizeIssue), total: null };
  }

  return {
    items: (data?.items ?? []).map(normalizeIssue),
    total: typeof data?.total === 'number' ? data.total : null
  };
}

export function normalizeEventsResponse(data) {
  const items = Array.isArray(data) ? data : data?.items ?? [];

  return {
    items: items.map(event => ({
      id: event.id,
      message: event.message ?? '',
      stackTrace: event.stackTrace ?? null,
      release: event.release ?? null,
      timestamp: event.timestamp
    })),
    total: !Array.isArray(data) && typeof data?.total === 'number' ? data.total : null,
    limit: !Array.isArray(data) && typeof data?.limit === 'number' ? data.limit : null
  };
}
