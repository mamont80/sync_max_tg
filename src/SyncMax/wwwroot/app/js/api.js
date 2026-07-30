/*
  Обращения к /api/miniapp/*. Подпись данных запуска проверяется сервером на каждом
  запросе, поэтому она уходит заголовком в каждом вызове — своей сессии у приложения нет.
  Заголовком, а не query-параметром: строка запуска не должна оседать в логах
  reverse proxy (та же логика, что и у секрета webhook в WebhookSecret).
*/

import { platform } from './platform.js';

const BASE = '/api/miniapp';

function headers(hasBody) {
    const result = {};
    if (hasBody) {
        result['Content-Type'] = 'application/json';
    }
    // Моста нет (открыто в браузере) — идём без заголовка: сервер пропустит запрос
    // только если задан отладочный пользователь MiniApp:DevUserId.
    if (platform.messenger && platform.initData) {
        result['Authorization'] = `TmaAuth ${platform.messenger} ${platform.initData}`;
    }
    return result;
}

async function request(method, path, body) {
    let response;
    try {
        response = await fetch(BASE + path, {
            method,
            headers: headers(body !== undefined),
            body: body === undefined ? undefined : JSON.stringify(body)
        });
    } catch {
        throw new Error('Нет связи с сервером. Проверьте интернет и попробуйте ещё раз.');
    }

    if (response.status === 204) {
        return null;
    }

    let payload = null;
    try {
        payload = await response.json();
    } catch {
        payload = null;
    }

    if (!response.ok) {
        if (response.status === 401) {
            throw new Error('Не удалось подтвердить данные запуска. Откройте приложение из чата с ботом.');
        }
        throw new Error((payload && payload.error) || `Ошибка сервера (${response.status}).`);
    }

    return payload;
}

export const api = {
    profile: () => request('GET', '/me'),
    refreshLinkCode: () => request('POST', '/link-code'),
    unlink: () => request('POST', '/unlink'),
    stats: () => request('GET', '/stats'),
    chatLinks: () => request('GET', '/chat-links'),
    updateChatLink: (id, patch) => request('PATCH', `/chat-links/${id}`, patch),
    deleteChatLink: (id) => request('DELETE', `/chat-links/${id}`)
};
