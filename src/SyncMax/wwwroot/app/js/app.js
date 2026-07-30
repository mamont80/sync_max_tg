/*
  Оболочка приложения: нижнее меню, переключение разделов и лист (bottom sheet)
  для экранов «вглубь». Роутера как такового нет — разделов четыре, состояние
  умещается в одну переменную.
*/

import { platform } from './platform.js';
import { renderLinks } from './views/links.js';
import { renderSupport } from './views/support.js';
import { renderStats } from './views/stats.js';
import { renderSoon } from './views/soon.js';

const TITLES = {
    links: 'Связки',
    subscription: 'Подписка',
    stats: 'Статистика',
    support: 'Поддержка'
};

const screen = document.getElementById('screen');
const titleEl = document.getElementById('screen-title');
const subtitleEl = document.getElementById('screen-subtitle');
const tabbar = document.getElementById('tabbar');

const sheet = document.getElementById('sheet');
const sheetTitle = document.getElementById('sheet-title');
const sheetBody = document.getElementById('sheet-body');

let currentTab = 'links';
let onSheetClosed = null;
let popSheetBack = null;

/* ---------- Лист ---------- */

function openSheet(title, content, onClosed) {
    sheetTitle.textContent = title;
    sheetBody.replaceChildren(content);
    sheet.hidden = false;
    onSheetClosed = onClosed || null;

    // Пока лист открыт, системная кнопка «Назад» закрывает именно его — так же,
    // как закрывала бы вложенный экран в самом мессенджере. Обработчик кладётся
    // в стек: поверх листа может открыться диалог, и он перехватит «Назад» первым.
    popSheetBack = platform.pushBackHandler(closeSheet);
}

function closeSheet() {
    if (sheet.hidden) return;

    sheet.hidden = true;
    sheetBody.replaceChildren();

    if (popSheetBack) {
        popSheetBack();
        popSheetBack = null;
    }

    const callback = onSheetClosed;
    onSheetClosed = null;
    if (callback) callback();
}

sheet.addEventListener('click', (event) => {
    if (event.target.hasAttribute('data-sheet-close')) {
        closeSheet();
    }
});

// Esc закрывает лист — привычно при отладке в браузере и в десктопных клиентах.
// Диалог вешает свой обработчик с перехватом, поэтому до листа Esc дойдёт только
// тогда, когда поверх ничего не открыто.
document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') closeSheet();
});

/* ---------- Контекст, который получают экраны ---------- */

const ctx = {
    openSheet,
    closeSheet,
    refresh: () => render(currentTab),
    setSubtitle: (text) => { subtitleEl.textContent = text || ''; }
};

/* ---------- Разделы ---------- */

function render(tab) {
    currentTab = tab;

    for (const item of tabbar.querySelectorAll('.tab')) {
        if (item.dataset.tab === tab) {
            item.setAttribute('aria-current', 'page');
        } else {
            item.removeAttribute('aria-current');
        }
    }

    titleEl.textContent = TITLES[tab];

    if (tab === 'links') {
        renderLinks(screen, ctx);
        return;
    }

    // Подзаголовок наполняет только экран связок — на остальных он лишний.
    subtitleEl.textContent = '';

    if (tab === 'support') {
        renderSupport(screen, ctx);
        return;
    }

    if (tab === 'stats') {
        renderStats(screen, ctx);
        return;
    }

    renderSoon(screen, tab);
}

tabbar.addEventListener('click', (event) => {
    const button = event.target.closest('.tab');
    if (!button || button.dataset.tab === currentTab) return;

    platform.haptic('select');
    closeSheet();
    render(button.dataset.tab);
});

render('links');
