import { ApiError, api, clearToken, getToken, saveToken } from './api.js';

// Вход: POST /api/auth/login -> { accessToken, tokenType, expiresIn, user }.
// Пароль нигде не сохраняется, токен хранится только в localStorage.
export async function login(username, password) {
  let data;

  try {
    data = await api.post(
      '/api/auth/login',
      { username: username.trim(), password },
      { auth: false }
    );
  } catch (error) {
    if (error instanceof ApiError) {
      // Не уточняем, что именно неверно: логин или пароль.
      if (error.status === 401) throw new Error('Неверный логин или пароль');
      if (error.status === 404) {
        throw new Error('Вход не поддерживается этим сервером. Проверьте адрес и версию backend');
      }
    }
    throw error;
  }

  if (!data?.accessToken) throw new Error('Сервер не вернул токен доступа');

  saveToken(data.accessToken, data.expiresIn);

  return data.user ?? { username: username.trim() };
}

// Текущий пользователь (GET /api/auth/me) или null, если токена нет или он недействителен.
export async function getCurrentUser() {
  if (!getToken()) return null;

  try {
    return await api.get('/api/auth/me', { skipAuthHandler: true });
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      clearToken();
      return null;
    }
    throw error;
  }
}

// Отдельного endpoint выхода нет: достаточно удалить токен.
export function logout() {
  clearToken();
}
