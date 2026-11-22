import React, { useState } from 'react';
import { Activity, AlertCircle, Eye, EyeOff, RefreshCw } from 'lucide-react';
import { login } from './auth.js';

export default function Login({ onSuccess, notice }) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);

  const dark = localStorage.getItem('loghive.dark') === '1';

  const submit = async event => {
    event.preventDefault();
    if (loading) return;

    setError('');
    setLoading(true);

    try {
      const user = await login(username, password);
      onSuccess(user);
    } catch (e) {
      setError(e.message || 'Не удалось выполнить вход');
      setPassword('');
      setLoading(false);
    }
  };

  return (
    <div className={`app login-page ${dark ? 'dark' : ''}`}>
      <form className="panel login-card" onSubmit={submit}>
        <div className="brand login-brand">
          <div className="brand-mark">
            <Activity size={21} />
          </div>
          <span>LogHive</span>
          <span className="brand-beta">BETA</span>
        </div>

        <h1>Вход в систему</h1>
        <p className="login-subtitle">Введите логин и пароль, чтобы продолжить</p>

        {notice && !error && (
          <div className="login-notice" role="status">
            <AlertCircle size={16} /> {notice}
          </div>
        )}

        {error && (
          <div className="login-error" role="alert">
            <AlertCircle size={16} /> {error}
          </div>
        )}

        <label className="login-field">
          <span>Логин</span>
          <input
            value={username}
            onChange={e => setUsername(e.target.value)}
            autoComplete="username"
            autoFocus
            required
          />
        </label>

        <label className="login-field">
          <span>Пароль</span>
          <div className="login-password">
            <input
              type={showPassword ? 'text' : 'password'}
              value={password}
              onChange={e => setPassword(e.target.value)}
              autoComplete="current-password"
              required
            />
            <button
              type="button"
              className="login-eye"
              title={showPassword ? 'Скрыть пароль' : 'Показать пароль'}
              onClick={() => setShowPassword(v => !v)}
            >
              {showPassword ? <EyeOff size={17} /> : <Eye size={17} />}
            </button>
          </div>
        </label>

        <button
          className="button button-primary login-submit"
          type="submit"
          disabled={loading}
        >
          {loading && <RefreshCw size={15} className="spin" />}
          {loading ? 'Входим…' : 'Войти'}
        </button>

      </form>
    </div>
  );
}
