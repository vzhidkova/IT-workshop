import React, { useCallback, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { RefreshCw } from 'lucide-react';
import App from './App.jsx';
import Login from './Login.jsx';
import { setUnauthorizedHandler } from './api.js';
import { getCurrentUser, logout } from './auth.js';
import './styles.css';

function Root() {
  // undefined — проверяем сессию, null — не авторизован.
  const [user, setUser] = useState(undefined);
  const [startupError, setStartupError] = useState('');
  const [notice, setNotice] = useState('');

  const checkSession = useCallback(() => {
    setStartupError('');
    setUser(undefined);

    getCurrentUser()
      .then(setUser)
      .catch(error => {
        setStartupError(error.message || 'Не удалось связаться с сервером');
      });
  }, []);

  useEffect(() => {
    checkSession();
  }, [checkSession]);

  // Сервер ответил 401 во время работы: токен недействителен, возвращаем на вход.
  useEffect(() => {
    setUnauthorizedHandler(() => {
      setNotice('Сессия истекла. Войдите снова');
      setUser(null);
    });
    return () => setUnauthorizedHandler(null);
  }, []);

  const handleLogin = loggedIn => {
    setNotice('');
    setUser(loggedIn);
  };

  const handleLogout = () => {
    logout();
    setNotice('');
    setUser(null);
  };

  if (startupError) {
    return (
      <div className="app login-page">
        <div className="panel login-card">
          <h1>Сервер недоступен</h1>
          <p className="login-subtitle">{startupError}</p>
          <button className="button button-primary login-submit" onClick={checkSession}>
            Повторить
          </button>
        </div>
      </div>
    );
  }

  if (user === undefined) {
    return (
      <div className="app login-page">
        <RefreshCw className="spin" size={22} />
      </div>
    );
  }

  if (!user) return <Login onSuccess={handleLogin} notice={notice} />;

  return <App user={user} onLogout={handleLogout} />;
}

createRoot(document.getElementById('root')).render(
  <React.StrictMode><Root /></React.StrictMode>
);
