// Единый HTTP-клиент для backend LogHive.
//
// Токен доступа (JWT) добавляется в заголовок Authorization автоматически.
// Адрес API: по умолчанию запросы идут на относительные адреса /api/... (в разработке их
// проксирует Vite, см. vite.config.js). Чтобы обращаться к backend напрямую, задайте
// VITE_API_URL (например, https://loghive.example.com): тогда на backend нужен CORS.

const API_URL = (import.meta.env.VITE_API_URL || '').replace(/\/+$/, '');

const TOKEN_KEY = 'loghive.token';
const EXPIRES_KEY = 'loghive.tokenExpiresAt';

export class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

// ---------- Хранение токена ----------

export function getToken() {
  try {
    const token = localStorage.getItem(TOKEN_KEY);
    if (!token) return null;

    const expiresAt = Number(localStorage.getItem(EXPIRES_KEY));
    if (expiresAt && Date.now() >= expiresAt) {
      clearToken();
      return null;
    }

    return token;
  } catch {
    return null;
  }
}

export function saveToken(token, expiresInSeconds) {
  try {
    localStorage.setItem(TOKEN_KEY, token);

    if (Number.isFinite(expiresInSeconds) && expiresInSeconds > 0) {
      localStorage.setItem(EXPIRES_KEY, String(Date.now() + expiresInSeconds * 1000));
    } else {
      localStorage.removeItem(EXPIRES_KEY);
    }
  } catch {
    // Хранилище недоступно: войти не получится, но приложение не должно падать.
  }
}

export function clearToken() {
  try {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(EXPIRES_KEY);
  } catch {
    // ignore
  }
}

// ---------- Обработка 401 ----------

let unauthorizedHandler = null;

// Вызывается, когда сервер отвечает 401 на защищённый запрос: токен удалён,
// нужно вернуть пользователя на страницу входа.
export function setUnauthorizedHandler(handler) {
  unauthorizedHandler = handler;
}

// ---------- Запросы ----------

function buildQuery(params) {
  const search = new URLSearchParams();

  Object.entries(params || {}).forEach(([key, value]) => {
    if (value !== undefined && value !== null && value !== '') {
      search.append(key, String(value));
    }
  });

  const query = search.toString();
  return query ? `?${query}` : '';
}

function errorMessage(status, data, hasBody) {
  const serverMessage = data?.message || data?.error || data?.title;

  if (status === 400) {
    return serverMessage
      ? `Некорректный запрос: ${serverMessage}`
      : 'Некорректные параметры запроса';
  }
  if (status === 401) return 'Сессия истекла. Войдите снова';
  if (status === 403) return 'Недостаточно прав для выполнения этого действия';
  if (status === 404) return serverMessage || 'Ничего не найдено';

  if (status >= 500) {
    // Если Vite не смог достучаться до backend, он отвечает 5xx с пустым телом.
    return hasBody
      ? 'Ошибка сервера. Попробуйте позже'
      : 'Сервер не отвечает. Проверьте, что backend запущен';
  }

  return serverMessage || `Ошибка запроса (${status})`;
}

async function request(method, path, { params, body, signal, auth = true, skipAuthHandler } = {}) {
  const headers = {};
  if (body) headers['Content-Type'] = 'application/json';

  const token = auth ? getToken() : null;
  if (token) headers.Authorization = `Bearer ${token}`;

  let response;

  try {
    response = await fetch(`${API_URL}${path}${buildQuery(params)}`, {
      method,
      headers,
      body: body ? JSON.stringify(body) : undefined,
      signal
    });
  } catch (error) {
    if (error.name === 'AbortError') throw error;
    throw new ApiError('Не удалось связаться с сервером', 0);
  }

  if (response.status === 401 && auth && !skipAuthHandler) {
    clearToken();
    if (unauthorizedHandler) unauthorizedHandler();
  }

  if (response.status === 204) return null;

  const text = await response.text();

  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    // Тело ответа не JSON.
  }

  if (!response.ok) {
    throw new ApiError(errorMessage(response.status, data, Boolean(text)), response.status);
  }

  return data;
}

export const api = {
  get: (path, options) => request('GET', path, options),
  post: (path, body, options) => request('POST', path, { ...options, body }),
  patch: (path, options) => request('PATCH', path, options)
};
