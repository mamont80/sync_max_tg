/*
  Единственное место, где различаются MAX и Telegram.

  У обеих платформ мини-приложение получает данные запуска и управляет клиентом через
  глобальный объект-мост, и объекты эти почти совпадают: Telegram кладёт его в
  window.Telegram.WebApp, MAX — в window.WebApp. Дальше по коду платформа не видна:
  наружу отдаётся один объект `platform` с одинаковым набором методов.

  Каждый вызов моста обёрнут: набор методов у MAX может отличаться, и отсутствие,
  например, тактильной отдачи не должно ронять интерфейс.
*/

const MAX_BRIDGE_SRC = 'https://st.max.ru/js/max-web-app.js';

/** Значения по умолчанию — если платформа не отдала палитру, остаётся тема из CSS. */
const THEME_MAP = {
    '--sm-bg': ['bg_color'],
    '--sm-secondary-bg': ['secondary_bg_color', 'bg_color'],
    '--sm-card': ['section_bg_color', 'bg_color'],
    '--sm-text': ['text_color'],
    '--sm-hint': ['hint_color', 'subtitle_text_color'],
    '--sm-accent': ['button_color', 'link_color', 'accent_text_color'],
    '--sm-accent-text': ['button_text_color'],
    '--sm-separator': ['section_separator_color'],
    '--sm-danger': ['destructive_text_color']
};

/**
 * Загрузка внешнего скрипта с ограничением по времени. Ограничение обязательно:
 * запуск приложения ждёт этот вызов, и недоступный CDN (нет сети, блокировка, медленный
 * ответ) иначе подвесил бы весь интерфейс — браузер сам об ошибке может не сообщить
 * очень долго. По истечении срока просто работаем без этого моста.
 */
function loadScript(src, timeoutMs = 4000) {
    return new Promise((resolve, reject) => {
        const el = document.createElement('script');
        const timer = setTimeout(() => reject(new Error(`Долго грузится ${src}`)), timeoutMs);
        const done = (fn, arg) => { clearTimeout(timer); fn(arg); };

        el.src = src;
        el.async = true;
        el.onload = () => done(resolve);
        el.onerror = () => done(reject, new Error(`Не загружен ${src}`));
        document.head.appendChild(el);
    });
}

/**
 * Определяет, из какого мессенджера открыто приложение.
 * Сначала Telegram — его мост уже подключён в index.html и отвечает мгновенно.
 * Мост MAX грузится только если телеграмовского initData нет: иначе в Telegram
 * каждый запуск ждал бы запрос к чужому CDN.
 */
async function detect() {
    const tg = window.Telegram && window.Telegram.WebApp;
    if (tg && tg.initData) {
        return { messenger: 'tg', bridge: tg };
    }

    if (!window.WebApp) {
        try {
            await loadScript(MAX_BRIDGE_SRC);
        } catch {
            // Не в MAX либо нет сети до его CDN — работаем без моста.
        }
    }

    const mx = window.WebApp;
    if (mx && mx.initData) {
        return { messenger: 'max', bridge: mx };
    }

    // Открыто в обычном браузере: моста нет, initData нет. Запросы уйдут без заголовка
    // авторизации, и сервер примет их только при заполненном MiniApp:DevUserId.
    return { messenger: null, bridge: tg || mx || null };
}

function applyTheme(bridge) {
    const params = (bridge && bridge.themeParams) || {};
    const root = document.documentElement.style;

    for (const [cssVar, candidates] of Object.entries(THEME_MAP)) {
        const key = candidates.find((name) => typeof params[name] === 'string' && params[name]);
        if (key) {
            root.setProperty(cssVar, params[key]);
        }
    }

    if (bridge && bridge.colorScheme) {
        document.documentElement.style.colorScheme = bridge.colorScheme;
    }
}

function call(bridge, path, ...args) {
    try {
        const parts = path.split('.');
        let target = bridge;
        for (const part of parts.slice(0, -1)) {
            target = target && target[part];
        }
        const fn = target && target[parts[parts.length - 1]];
        if (typeof fn === 'function') {
            fn.apply(target, args);
            return true;
        }
    } catch {
        // Метод есть, но упал внутри клиента — интерфейс это переживёт.
    }
    return false;
}

const detected = await detect();
const bridge = detected.bridge;

applyTheme(bridge);
call(bridge, 'ready');
call(bridge, 'expand');
// Тема в клиенте может смениться на лету — перекрашиваемся вместе с ним.
call(bridge, 'onEvent', 'themeChanged', () => applyTheme(bridge));

/*
  Обработчики системной кнопки «Назад» — стеком: поверх листа может открыться диалог,
  и «Назад» должна закрывать сначала его, а потом уже лист. В мост подписывается один
  постоянный диспетчер, который зовёт верхний обработчик, — иначе подписки копились бы.
*/
const backStack = [];

function syncBackButton() {
    call(bridge, backStack.length > 0 ? 'BackButton.show' : 'BackButton.hide');
}

call(bridge, 'BackButton.onClick', () => {
    const top = backStack[backStack.length - 1];
    if (top) top();
});

export const platform = {
    /** 'tg' | 'max' | null (обычный браузер). */
    messenger: detected.messenger,

    /** Подписанная строка данных запуска; пустая, если моста нет. */
    initData: (bridge && bridge.initData) || '',

    /** Данные пользователя из моста — для приветствия до ответа сервера. */
    user: (bridge && bridge.initDataUnsafe && bridge.initDataUnsafe.user) || null,

    /** Название платформы для интерфейса. */
    title(messenger = detected.messenger) {
        return messenger === 'max' ? 'MAX' : messenger === 'tg' ? 'Telegram' : '—';
    },

    /**
     * Добавляет обработчик системной кнопки «Назад» клиента. Своей стрелки в интерфейсе
     * нет специально: пользователь ожидает ту кнопку, к которой привык в мессенджере.
     * Возвращает функцию снятия — вызывающий обязан позвать её при закрытии своего слоя.
     */
    pushBackHandler(handler) {
        backStack.push(handler);
        syncBackButton();

        return () => {
            const index = backStack.lastIndexOf(handler);
            if (index !== -1) backStack.splice(index, 1);
            syncBackButton();
        };
    },

    /** kind: 'light' | 'medium' | 'select' | 'success' | 'error'. */
    haptic(kind = 'light') {
        if (kind === 'select') {
            call(bridge, 'HapticFeedback.selectionChanged');
        } else if (kind === 'success' || kind === 'error' || kind === 'warning') {
            call(bridge, 'HapticFeedback.notificationOccurred', kind);
        } else {
            call(bridge, 'HapticFeedback.impactOccurred', kind);
        }
    },

    /**
     * Открывает ссылку средствами клиента. Для t.me в Telegram есть отдельный метод —
     * он открывает чат прямо в приложении, а не выкидывает в браузер. Если моста нет,
     * остаётся обычное открытие вкладки.
     */
    openLink(url) {
        const isTelegramLink = /^https:\/\/t\.me\//i.test(url);

        if (isTelegramLink && call(bridge, 'openTelegramLink', url)) return;
        if (call(bridge, 'openLink', url)) return;

        window.open(url, '_blank', 'noopener');
    },

    /** Копирование кода связки: буфер обмена, с откатом для старых вебвью. */
    async copy(text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    }
};
