import React, { useEffect, useMemo, useState } from 'react';
import {
  Activity,
  AlertCircle,
  AlertTriangle,
  ArrowLeft,
  Check,
  CheckCircle2,
  ChevronDown,
  CircleHelp,
  Clock3,
  Copy,
  LayoutDashboard,
  LogOut,
  Moon,
  RefreshCw,
  Search,
  Sun,
  Terminal,
  X
} from 'lucide-react';
import {
  Area,
  AreaChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis
} from 'recharts';
import { api } from './api.js';
import {
  bucketEventsByDay,
  formatBucket,
  formatClock,
  formatDateTime,
  formatNumber,
  formatShortDateTime,
  timeAgo
} from './format.js';
import {
  normalizeEventsResponse,
  normalizeIssue,
  normalizeIssuesResponse
} from './normalize.js';

const PERIODS = { '1 час': '1h', '24 часа': '24h', '7 дней': '7d' };
const PAGE_SIZE = 20;
const EVENTS_PAGE = 100; // максимум, который отдаёт backend за один запрос
const EVENTS_CAP = 1000; // сколько событий максимум берём для графика ошибки
const RECENT_EVENTS = 20; // сколько последних случаев показываем списком
const POLL_INTERVAL_MS = 30000;

const statusOf = issue => (issue.resolved ? 'Решена' : 'Новая');

function Badge({ level }) {
  return (
    <span className={`level level-${level.toLowerCase()}`}>
      {level === 'ERROR' ? (
        <AlertCircle size={13} />
      ) : level === 'WARN' ? (
        <AlertTriangle size={13} />
      ) : (
        <Activity size={13} />
      )}{' '}
      {level}
    </span>
  );
}

function Status({ status }) {
  return (
    <span className={`status ${status === 'Новая' ? 'status-new' : 'status-done'}`}>
      <span className="status-dot" /> {status}
    </span>
  );
}

function ChartTooltip({ active, payload, label }) {
  if (!active || !payload?.length) return null;

  return (
    <div className="chart-tooltip">
      <b>{label}</b>
      <span>{payload[0].value} событий</span>
    </div>
  );
}

function ActivityChart({ data, gradientId, tickSize = 11 }) {
  if (!data.length || data.every(point => !point.count)) {
    return <div className="chart-empty">Данных за выбранный период нет</div>;
  }

  return (
    <ResponsiveContainer width="100%" height="100%">
      <AreaChart data={data} margin={{ top: 10, right: 8, left: -18, bottom: 0 }}>
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="#7866ed" stopOpacity={0.24} />
            <stop offset="95%" stopColor="#7866ed" stopOpacity={0.01} />
          </linearGradient>
        </defs>
        <CartesianGrid strokeDasharray="3 5" vertical={false} stroke="var(--grid)" />
        <XAxis
          dataKey="time"
          axisLine={false}
          tickLine={false}
          tick={{ fill: 'var(--muted)', fontSize: tickSize }}
          dy={9}
        />
        <YAxis
          axisLine={false}
          tickLine={false}
          allowDecimals={false}
          tick={{ fill: 'var(--muted)', fontSize: 11 }}
        />
        <Tooltip content={<ChartTooltip />} />
        <Area
          type="monotone"
          dataKey="count"
          stroke="#7866ed"
          strokeWidth={2.5}
          fill={`url(#${gradientId})`}
          activeDot={{ r: 5, strokeWidth: 0 }}
        />
      </AreaChart>
    </ResponsiveContainer>
  );
}

// Загружает события ошибки за 7 дней страницами (но не больше EVENTS_CAP).
// Размер страницы берём из ответа сервера: он может быть меньше запрошенного.
async function loadIssueEvents(base) {
  const params = offset => ({ period: '7d', limit: EVENTS_PAGE, offset });

  const first = normalizeEventsResponse(
    await api.get(`${base}/events`, { params: params(0) })
  );

  const total = first.total ?? first.items.length;
  const step = first.limit || first.items.length || EVENTS_PAGE;

  const offsets = [];
  for (let offset = step; step > 0 && offset < Math.min(total, EVENTS_CAP); offset += step) {
    offsets.push(offset);
  }

  const rest = await Promise.all(
    offsets.map(async offset =>
      normalizeEventsResponse(await api.get(`${base}/events`, { params: params(offset) }))
    )
  );

  // События могли прийти во время загрузки и сдвинуть страницы, поэтому убираем дубли.
  const byId = new Map();
  [first, ...rest].forEach(page => page.items.forEach(item => byId.set(item.id, item)));

  return { items: [...byId.values()], total };
}

const toChartData = timeline =>
  (timeline?.data || []).map(point => ({
    time: formatBucket(point.time, timeline.interval),
    count: point.eventCount
  }));

export default function App({ user, onLogout }) {
  const [page, setPage] = useState('errors');
  const [selectedErrorId, setSelectedErrorId] = useState(null);
  const [selectedIssue, setSelectedIssue] = useState(null);
  const [visibleEvents, setVisibleEvents] = useState(RECENT_EVENTS);
  const [loadingEvents, setLoadingEvents] = useState(false);

  const [levelFilter, setLevelFilter] = useState('Все уровни');
  const [statusFilter, setStatusFilter] = useState('Все статусы');
  const [period, setPeriod] = useState('24 часа');
  const [search, setSearch] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');

  const [dark, setDark] = useState(() => localStorage.getItem('loghive.dark') === '1');
  const [notice, setNotice] = useState('');

  // Данные списка ошибок
  const [issues, setIssues] = useState({ items: [], total: 0, hasMore: false });
  const [summary, setSummary] = useState(null);
  const [timeline, setTimeline] = useState(null);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [loadError, setLoadError] = useState('');
  const [lastUpdated, setLastUpdated] = useState(null);
  const [reload, setReload] = useState({ n: 0, silent: false });

  // Данные карточки ошибки
  const [detail, setDetail] = useState({
    loading: false,
    error: '',
    issue: null,
    events: [],
    eventsTotal: 0,
    chart: [],
    chartEvents: 0
  });
  const [toggling, setToggling] = useState(false);

  // Поиск отправляется на сервер с небольшой задержкой.
  useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedSearch(search), 350);
    return () => window.clearTimeout(timer);
  }, [search]);

  const filters = useMemo(
    () => ({
      period: PERIODS[period],
      level: levelFilter === 'Все уровни' ? undefined : levelFilter,
      resolved:
        statusFilter === 'Новая' ? false : statusFilter === 'Решена' ? true : undefined,
      search: debouncedSearch.trim() || undefined
    }),
    [period, levelFilter, statusFilter, debouncedSearch]
  );

  // Загрузка списка, сводки и графика
  useEffect(() => {
    if (page !== 'errors') return undefined;

    let cancelled = false;
    if (!reload.silent) setLoading(true);

    Promise.all([
      api.get('/api/issues', { params: { ...filters, limit: PAGE_SIZE, offset: 0 } }),
      api.get('/api/analytics/summary', {
        params: { period: filters.period, level: filters.level }
      }),
      api.get('/api/analytics/timeline', {
        params: { period: filters.period, level: filters.level }
      })
    ])
      .then(([list, sum, tl]) => {
        if (cancelled) return;
        const page = normalizeIssuesResponse(list);
        setIssues({
          items: page.items,
          total: page.total ?? page.items.length,
          hasMore:
            page.total !== null
              ? page.items.length < page.total
              : page.items.length >= PAGE_SIZE
        });
        setSummary(sum);
        setTimeline(tl);
        setLoadError('');
        setLastUpdated(new Date());
      })
      .catch(error => {
        if (!cancelled) setLoadError(error.message);
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [page, filters, reload]);

  // Автообновление списка
  useEffect(() => {
    if (page !== 'errors') return undefined;

    const timer = window.setInterval(() => {
      if (!document.hidden) setReload(r => ({ n: r.n + 1, silent: true }));
    }, POLL_INTERVAL_MS);

    return () => window.clearInterval(timer);
  }, [page]);

  // Загрузка карточки ошибки
  useEffect(() => {
    if (page !== 'detail' || selectedErrorId == null) return undefined;

    let cancelled = false;

    setDetail({
      loading: true,
      error: '',
      issue: null,
      events: [],
      eventsTotal: 0,
      chart: [],
      chartEvents: 0
    });

    setVisibleEvents(RECENT_EVENTS);

    const base = `/api/issues/${selectedErrorId}`;

    // Данные об ошибке берём из строки списка. GET /api/issues/{id} в документации
    // не описан, поэтому он необязателен: если его нет, карточка строится по списку.
    const issueRequest = api
      .get(base, { params: { period: '7d' } })
      .then(normalizeIssue)
      .catch(error => {
        if (error.status === 401) throw error;
        return null;
      });

    Promise.all([issueRequest, loadIssueEvents(base)])
      .then(([fetched, events]) => {
        if (cancelled) return;
        setDetail({
          loading: false,
          error: '',
          issue: { ...selectedIssue, ...(fetched || {}) },
          events: events.items,
          eventsTotal: events.total,
          chart: bucketEventsByDay(events.items),
          chartEvents: events.items.length
        });
      })
      .catch(error => {
        if (cancelled) return;
        setDetail(d => ({ ...d, loading: false, error: error.message }));
      });

    return () => {
      cancelled = true;
    };
  }, [page, selectedErrorId]); // eslint-disable-line react-hooks/exhaustive-deps

  const toggleTheme = () => {
    const next = !dark;
    setDark(next);
    localStorage.setItem('loghive.dark', next ? '1' : '0');
  };

  const showNotice = text => {
    setNotice(text);
    window.setTimeout(() => setNotice(''), 2400);
  };

  const refresh = () => setReload(r => ({ n: r.n + 1, silent: false }));

  const loadMore = async () => {
    setLoadingMore(true);

    try {
      const page = normalizeIssuesResponse(
        await api.get('/api/issues', {
          params: { ...filters, limit: PAGE_SIZE, offset: issues.items.length }
        })
      );

      setIssues(prev => {
        const items = [
          ...prev.items,
          ...page.items.filter(item => !prev.items.some(p => p.id === item.id))
        ];

        return {
          items,
          total: page.total ?? items.length,
          hasMore:
            page.total !== null ? items.length < page.total : page.items.length >= PAGE_SIZE
        };
      });
    } catch (error) {
      showNotice(error.message);
    } finally {
      setLoadingMore(false);
    }
  };

  const openError = row => {
    setSelectedIssue(row);
    setSelectedErrorId(row.id);
    setPage('detail');
  };

  // В backend есть только PATCH /resolve, обратной операции нет.
  const resolveIssue = async () => {
    const issue = detail.issue;
    if (!issue || issue.resolved || toggling) return;

    setToggling(true);

    try {
      await api.patch(`/api/issues/${issue.id}/resolve`);
      setDetail(d => ({ ...d, issue: { ...d.issue, resolved: true } }));
      showNotice('Ошибка отмечена решённой');
    } catch (error) {
      showNotice(error.message);
    } finally {
      setToggling(false);
    }
  };

  const loadMoreEvents = async () => {
    const next = visibleEvents + RECENT_EVENTS;

    // Часть событий уже загружена, ходить на сервер не нужно.
    if (next <= detail.events.length || detail.events.length >= detail.eventsTotal) {
      setVisibleEvents(next);
      return;
    }

    setLoadingEvents(true);

    try {
      const page = normalizeEventsResponse(
        await api.get(`/api/issues/${selectedErrorId}/events`, {
          params: { period: '7d', limit: EVENTS_PAGE, offset: detail.events.length }
        })
      );

      setDetail(d => ({
        ...d,
        events: [...d.events, ...page.items.filter(e => !d.events.some(x => x.id === e.id))]
      }));
      setVisibleEvents(next);
    } catch (error) {
      showNotice(error.message);
    } finally {
      setLoadingEvents(false);
    }
  };

  const copyText = async text => {
    try {
      await navigator.clipboard.writeText(text);
      showNotice('Скопировано');
    } catch {
      showNotice('Не удалось скопировать');
    }
  };

  const issue = detail.issue;

  // Стектрейс может быть у самой ошибки или только у её событий.
  const stackText = useMemo(
    () => issue?.stackTrace || detail.events.find(e => e.stackTrace)?.stackTrace || '',
    [issue, detail.events]
  );

  const stackLines = useMemo(
    () => (stackText ? stackText.split(/\r?\n/).filter(line => line.trim()) : []),
    [stackText]
  );

  const hasFilters =
    levelFilter !== 'Все уровни' || statusFilter !== 'Все статусы' || debouncedSearch.trim() !== '';

  const chartIsPartial = detail.chartEvents < detail.eventsTotal;

  return (
    <div className={`app ${dark ? 'dark' : ''}`}>
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">
            <Activity size={21} />
          </div>
          <span>LogHive</span>
          <span className="brand-beta">BETA</span>
        </div>

        <div className="workspace-label">РАБОЧЕЕ ПРОСТРАНСТВО</div>

        <nav>
          <button className="nav-item active" onClick={() => setPage('errors')}>
            <LayoutDashboard size={18} /> Ошибки
            {summary && <span className="nav-count">{summary.unresolvedIssues}</span>}
          </button>
        </nav>

        <div className="sidebar-bottom">
          <div className="status-card">
            <span className="pulse" />
            <div>
              <b>{loadError ? 'Нет связи с сервером' : 'Сервер подключён'}</b>
              <small>
                {lastUpdated ? `Обновлено · ${formatClock(lastUpdated)}` : 'Загрузка данных…'}
              </small>
            </div>
          </div>

          <button className="nav-item theme-toggle" onClick={toggleTheme}>
            {dark ? <Sun size={18} /> : <Moon size={18} />}{' '}
            {dark ? 'Светлая тема' : 'Тёмная тема'}
          </button>

          <div className="user-block">
            <div className="avatar">
              {(user.username || '?').slice(0, 2).toUpperCase()}
            </div>
            <div>
              <b>{user.username}</b>
              <small>Вы вошли в систему</small>
            </div>
          </div>

          <button className="nav-item logout-button" onClick={onLogout}>
            <LogOut size={18} /> Выйти
          </button>
        </div>
      </aside>

      <main className="main">
        <header className="topbar">
          <div className="breadcrumbs">
            <span>Рабочее пространство</span>
            <span className="crumb-sep">/</span>
            <b>{page === 'detail' ? 'Карточка ошибки' : 'Ошибки'}</b>
          </div>
          <div className="topbar-right">
            <span className="live-indicator">
              <span /> Live
            </span>
            <button className="icon-button" title="Переключить тему" onClick={toggleTheme}>
              {dark ? <Sun size={18} /> : <Moon size={18} />}
            </button>
          </div>
        </header>

        {page === 'errors' && (
          <section className="content">
            <div className="page-heading">
              <div>
                <div className="eyebrow">МОНИТОРИНГ</div>
                <h1>Ошибки</h1>
                <p>Следите за событиями и состоянием приложений</p>
              </div>
              <button className="button button-secondary" onClick={refresh}>
                <RefreshCw size={15} className={loading ? 'spin' : ''} /> Обновить
              </button>
            </div>

            {loadError && (
              <div className="load-error" role="alert">
                <AlertCircle size={16} />
                <span>{loadError}</span>
                <button className="button button-quiet button-small" onClick={refresh}>
                  Повторить
                </button>
              </div>
            )}

            <div className="filters panel">
              <div className="filter-group">
                <span>Уровень</span>
                <div className="segmented">
                  {['Все уровни', 'ERROR', 'WARN', 'INFO'].map(l => (
                    <button
                      key={l}
                      onClick={() => setLevelFilter(l)}
                      className={levelFilter === l ? 'selected' : ''}
                    >
                      {l}
                    </button>
                  ))}
                </div>
              </div>

              <div className="filter-group">
                <span>Статус</span>
                <div className="segmented">
                  {['Все статусы', 'Новая', 'Решена'].map(s => (
                    <button
                      key={s}
                      onClick={() => setStatusFilter(s)}
                      className={statusFilter === s ? 'selected' : ''}
                    >
                      {s}
                    </button>
                  ))}
                </div>
              </div>

              <label className="filter-period">
                <span>Период</span>
                <div className="select-wrap">
                  <select value={period} onChange={e => setPeriod(e.target.value)}>
                    {Object.keys(PERIODS).map(p => (
                      <option key={p}>{p}</option>
                    ))}
                  </select>
                  <ChevronDown size={15} />
                </div>
              </label>

              <label className="search-field">
                <Search size={16} />
                <input
                  value={search}
                  onChange={e => setSearch(e.target.value)}
                  placeholder="Поиск ошибок..."
                />
                {search && (
                  <button onClick={() => setSearch('')} aria-label="Очистить поиск">
                    <X size={14} />
                  </button>
                )}
              </label>
            </div>

            <div className="metrics-grid">
              <div className="metric panel">
                <div className="metric-top">
                  <span>Всего событий</span>
                  <span className="metric-icon purple">
                    <Activity size={17} />
                  </span>
                </div>
                <div className="metric-value">{formatNumber(summary?.totalEvents)}</div>
                <div className="metric-foot">За выбранный период</div>
              </div>

              <div className="metric panel">
                <div className="metric-top">
                  <span>Всего Issues</span>
                  <span className="metric-icon amber">
                    <CircleHelp size={17} />
                  </span>
                </div>
                <div className="metric-value">{formatNumber(summary?.totalIssues)}</div>
                <div className="metric-foot">Групп ошибок за период</div>
              </div>

              <div className="metric panel">
                <div className="metric-top">
                  <span>Нерешённые</span>
                  <span className="metric-icon red">
                    <AlertCircle size={17} />
                  </span>
                </div>
                <div className="metric-value">{formatNumber(summary?.unresolvedIssues)}</div>
                <div className="metric-foot">Требуют внимания</div>
              </div>

              <div className="metric panel">
                <div className="metric-top">
                  <span>Решённые</span>
                  <span className="metric-icon green">
                    <CheckCircle2 size={17} />
                  </span>
                </div>
                <div className="metric-value">{formatNumber(summary?.resolvedIssues)}</div>
                <div className="metric-foot">
                  Из {formatNumber(summary?.totalIssues)} групп ошибок
                </div>
              </div>
            </div>

            <div className="panel chart-panel">
              <div className="section-heading">
                <div>
                  <h2>Частота событий</h2>
                  <p>Количество логов за {period.toLowerCase()}</p>
                </div>
                <div className="chart-legend">
                  <span /> События
                </div>
              </div>
              <div className="chart-area">
                <ActivityChart data={toChartData(timeline)} gradientId="areaFill" />
              </div>
            </div>

            <div className={`table-panel panel ${loading ? 'is-loading' : ''}`}>
              <div className="table-heading">
                <div>
                  <h2>Группы ошибок</h2>
                  <p>
                    Найдено: {formatNumber(issues.total)}
                    {issues.hasMore && issues.total === issues.items.length ? '+' : ''}
                  </p>
                </div>
                <button
                  className="button button-quiet"
                  onClick={() => {
                    setLevelFilter('Все уровни');
                    setStatusFilter('Все статусы');
                    setSearch('');
                  }}
                >
                  Сбросить фильтры
                </button>
              </div>

              <div className="table-scroll">
                <table>
                  <thead>
                    <tr>
                      <th>ID</th>
                      <th>УРОВЕНЬ</th>
                      <th>ОШИБКА</th>
                      <th>СОБЫТИЯ</th>
                      <th>ПЕРВОЕ ПОЯВЛЕНИЕ</th>
                      <th>ПОСЛЕДНЕЕ ПОЯВЛЕНИЕ</th>
                      <th>СТАТУС</th>
                    </tr>
                  </thead>
                  <tbody>
                    {issues.items.map(e => (
                      <tr
                        key={e.id}
                        onClick={() => openError(e)}
                        tabIndex="0"
                        onKeyDown={ev => ev.key === 'Enter' && openError(e)}
                      >
                        <td>
                          <span className="muted">#{e.id}</span>
                        </td>
                        <td>
                          <Badge level={e.level} />
                        </td>
                        <td>
                          <div className="error-cell">
                            <b>{e.title}</b>
                            {e.release && <small>Версия {e.release}</small>}
                          </div>
                        </td>
                        <td>
                          <span className="count-pill">{formatNumber(e.eventCount)}</span>
                        </td>
                        <td>
                          <span className="last-seen">{formatShortDateTime(e.firstSeen)}</span>
                        </td>
                        <td>
                          <span className="last-seen" title={formatDateTime(e.periodLastSeen)}>
                            <Clock3 size={14} />
                            {timeAgo(e.periodLastSeen)}
                          </span>
                        </td>
                        <td>
                          <Status status={statusOf(e)} />
                        </td>
                      </tr>
                    ))}
                    {issues.items.length === 0 && !loading && (
                      <tr>
                        <td colSpan="7">
                          <div className="empty-state">
                            <Search size={24} />
                            {hasFilters ? (
                              <>
                                <b>Ничего не найдено</b>
                                <span>Измените фильтры или поисковый запрос</span>
                              </>
                            ) : (
                              <b>Данных за выбранный период нет</b>
                            )}
                          </div>
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </div>

              {issues.hasMore && (
                <div className="load-more">
                  <button
                    className="button button-secondary"
                    onClick={loadMore}
                    disabled={loadingMore}
                  >
                    {loadingMore && <RefreshCw size={15} className="spin" />}
                    Показать ещё
                  </button>
                </div>
              )}

              <div className="table-footer">
                <span>
                  Показано {issues.items.length}
                  {issues.hasMore && issues.total === issues.items.length
                    ? ''
                    : ` из ${issues.total}`}{' '}
                  групп
                </span>
                <span>
                  {lastUpdated ? `Обновлено ${formatClock(lastUpdated)}` : 'Загрузка…'}{' '}
                  <span className="footer-dot" />
                </span>
              </div>
            </div>
          </section>
        )}

        {page === 'detail' && (
          <section className="content">
            <button className="back-link" onClick={() => setPage('errors')}>
              <ArrowLeft size={16} /> Назад к ошибкам
            </button>

            {detail.loading && (
              <div className="panel detail-state">
                <RefreshCw size={20} className="spin" />
                <span>Загрузка…</span>
              </div>
            )}

            {!detail.loading && detail.error && (
              <div className="panel detail-state">
                <AlertCircle size={22} />
                <b>Не удалось открыть ошибку</b>
                <span>{detail.error}</span>
              </div>
            )}

            {issue && (
              <>
                <div className="detail-title-row">
                  <div>
                    <div className="eyebrow">ДЕТАЛИ СОБЫТИЯ</div>
                    <h1>{issue.title}</h1>
                    <div className="detail-subline">
                      <Badge level={issue.level} />
                      <Status status={statusOf(issue)} />
                      <span className="muted">ID #{issue.id}</span>
                      {issue.release && <span className="muted">Версия {issue.release}</span>}
                    </div>
                  </div>
                  <button
                    className={`button ${
                      issue.resolved ? 'button-secondary' : 'button-primary'
                    }`}
                    onClick={resolveIssue}
                    disabled={issue.resolved || toggling}
                  >
                    {toggling ? (
                      <RefreshCw size={15} className="spin" />
                    ) : (
                      <Check size={16} />
                    )}{' '}
                    {issue.resolved ? 'Решена' : 'Решить'}
                  </button>
                </div>

                <div className="metrics-grid detail-metrics">
                  <div className="metric panel">
                    <div className="metric-top">
                      <span>Всего событий</span>
                      <Activity size={16} className="muted" />
                    </div>
                    <div className="metric-value">{formatNumber(issue.totalEventCount)}</div>
                    <div className="metric-foot">За всё время</div>
                  </div>

                  <div className="metric panel">
                    <div className="metric-top">
                      <span>Впервые</span>
                      <Clock3 size={16} className="muted" />
                    </div>
                    <div className="detail-date">{formatDateTime(issue.firstSeen)}</div>
                    <div className="metric-foot">Первое появление</div>
                  </div>

                  <div className="metric panel">
                    <div className="metric-top">
                      <span>Последний раз</span>
                      <Clock3 size={16} className="muted" />
                    </div>
                    <div className="detail-date">{formatDateTime(issue.lastSeen)}</div>
                    <div className="metric-foot">{timeAgo(issue.lastSeen)}</div>
                  </div>
                </div>

                <div className="panel chart-panel">
                  <div className="section-heading">
                    <div>
                      <h2>Активность за неделю</h2>
                      <p>
                        {chartIsPartial
                          ? `Число повторений по дням, по последним ${formatNumber(
                              detail.chartEvents
                            )} событиям`
                          : 'Число повторений по дням'}
                      </p>
                    </div>
                    <span className="week-total">
                      {formatNumber(detail.eventsTotal)} событий
                    </span>
                  </div>
                  <div className="chart-area detail-chart">
                    <ActivityChart data={detail.chart} gradientId="weekFill" tickSize={12} />
                  </div>
                </div>

                {stackLines.length > 0 && (
                <div className="panel stack-panel">
                  <div className="section-heading">
                    <div>
                      <h2>
                        <Terminal size={17} /> Стектрейс
                      </h2>
                      <p>Место возникновения ошибки в коде</p>
                    </div>
                    {stackLines.length > 0 && (
                      <button
                        className="button button-secondary button-small"
                        onClick={() => copyText(stackLines.join('\n'))}
                      >
                        <Copy size={14} /> Копировать
                      </button>
                    )}
                  </div>

                  <div className="stack-error">
                    <AlertCircle size={16} />
                    <div>
                      <b>{issue.title}</b>
                      {detail.events[0]?.message && <span>{detail.events[0].message}</span>}
                    </div>
                  </div>

                  <div className="code-list">
                    {stackLines.map((line, i) => (
                      <div className="code-line" key={i}>
                        <span className="line-number">{String(i + 1).padStart(2, '0')}</span>
                        <code>{line}</code>
                        <button title="Копировать строку" onClick={() => copyText(line)}>
                          <Copy size={13} />
                        </button>
                      </div>
                    ))}
                  </div>
                </div>
                )}

                <div className="panel occurrences-panel">
                  <div className="section-heading">
                    <div>
                      <h2>История событий</h2>
                      <p>
                        От новых к старым. Показано {Math.min(visibleEvents, detail.events.length)}{' '}
                        из {formatNumber(detail.eventsTotal)} за 7 дней
                      </p>
                    </div>
                    <span className="count-pill">{formatNumber(detail.eventsTotal)}</span>
                  </div>

                  <div className="occurrence-list">
                    {detail.events.slice(0, visibleEvents).map(event => (
                      <div className="occurrence" key={event.id}>
                        <span className="occurrence-icon">
                          <AlertCircle size={15} />
                        </span>
                        <div>
                          <b title={event.message}>{event.message}</b>
                          {event.release && <small>Версия {event.release}</small>}
                          {event.stackTrace && (
                            <details className="event-stack">
                              <summary>Стектрейс</summary>
                              <pre>{event.stackTrace}</pre>
                            </details>
                          )}
                        </div>
                        <time>{formatDateTime(event.timestamp)}</time>
                      </div>
                    ))}

                    {detail.events.length === 0 && (
                      <div className="empty-state">
                        <span>Данных за выбранный период нет</span>
                      </div>
                    )}
                  </div>

                  {(visibleEvents < detail.events.length ||
                    detail.events.length < detail.eventsTotal) && (
                    <div className="load-more">
                      <button
                        className="button button-secondary"
                        onClick={loadMoreEvents}
                        disabled={loadingEvents}
                      >
                        {loadingEvents && <RefreshCw size={15} className="spin" />}
                        Показать ещё
                      </button>
                    </div>
                  )}
                </div>
              </>
            )}
          </section>
        )}
      </main>

      {notice && (
        <div className="toast">
          <CheckCircle2 size={17} />
          {notice}
        </div>
      )}
    </div>
  );
}
