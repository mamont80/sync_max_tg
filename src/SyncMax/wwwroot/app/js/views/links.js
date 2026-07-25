/* Экран «Связки» — единственный работающий раздел первой версии. */

import { api } from '../api.js';
import { platform } from '../platform.js';
import { el, clear, emptyState, button } from '../dom.js';
import { alertDialog, toast } from '../dialog.js';
import { DIRECTION_LABEL, sideName } from '../format.js';
import { openLinkSheet } from './link.js';
import { openCreateSheet } from './create.js';

const ICON_LINK = [
    'M9.5 14.5 14.5 9.5',
    'M11 6.5 12.8 4.7a4 4 0 0 1 5.7 5.7L16.6 12.3',
    'M13 17.5 11.2 19.3a4 4 0 0 1-5.7-5.7L7.4 11.7'
];
const ICON_WARN = ['M12 8.5v4.2', 'M12 16.2h0', 'M12 3.6 21 19.2H3Z'];

function sideRow(code, title, kind) {
    return el('div', { className: 'side' }, [
        el('span', { className: `badge badge--${code}`, text: code === 'max' ? 'MAX' : 'TG' }),
        el('span', { className: 'side__name', text: sideName(title, kind) })
    ]);
}

/**
 * Заголовок карточки. У связок с известными сторонами он не нужен: две строки со
 * значками MAX и TG говорят то же самое, но ещё и показывают, где какая сторона.
 * Общее «чат1 <=> чат2» остаётся только для связок, созданных до миграции M005.
 */
function cardHead(link) {
    const hasSides = Boolean(link.maxTitle || link.tgTitle);
    return hasSides
        ? [sideRow('max', link.maxTitle, link.maxKind), sideRow('tg', link.tgTitle, link.tgKind)]
        : [
            el('p', { className: 'card__title', text: link.title }),
            sideRow('max', null, link.maxKind),
            sideRow('tg', null, link.tgKind)
        ];
}

function linkCard(link, ctx) {
    return el('button', {
        className: 'card card--tap',
        attrs: { type: 'button' },
        on: {
            click: () => {
                platform.haptic('light');
                openLinkSheet(link, ctx);
            }
        }
    }, [
        ...cardHead(link),
        el('div', { className: 'status' }, [
            el('span', { className: `dot ${link.active ? 'dot--on' : 'dot--off'}` }),
            el('span', { text: link.active ? 'Пересылка включена' : 'Пересылка выключена' }),
            el('span', { className: 'chip', text: DIRECTION_LABEL[link.direction] || link.direction })
        ])
    ]);
}

/** Экран, пока аккаунты не связаны: код и что с ним делать. */
function notLinkedView(profile, ctx) {
    const other = platform.title(profile.messenger === 'max' ? 'tg' : 'max');
    const code = profile.linkCode || '——————';

    const copyBtn = button('Скопировать код', {
        variant: 'btn--secondary',
        onClick: async () => {
            platform.haptic('light');
            if (await platform.copy(code)) {
                toast('Код скопирован');
            } else {
                // Буфер обмена бывает недоступен в вебвью — тогда код нужно переписать
                // руками, поэтому показываем его окном, а не исчезающей подсказкой.
                alertDialog({ title: 'Скопируйте код вручную', text: code });
            }
        }
    });

    const refreshBtn = button('Обновить код', {
        variant: 'btn--quiet',
        onClick: async () => {
            refreshBtn.disabled = true;
            try {
                await api.refreshLinkCode();
                platform.haptic('success');
                ctx.refresh();
            } catch (error) {
                alertDialog({ title: 'Не удалось обновить код', text: error.message });
                refreshBtn.disabled = false;
            }
        }
    });

    return [
        el('section', { className: 'card' }, [
            el('p', { className: 'card__title', text: 'Аккаунты ещё не связаны' }),
            el('p', {
                className: 'card__note',
                text: `Свяжите этот аккаунт с ${other}, чтобы пересылать сообщения между чатами.`
            }),
            el('code', { className: 'code', text: code }),
            copyBtn
        ]),
        el('section', { className: 'card' }, [
            el('ol', { className: 'steps' }, [
                step(1, `Откройте бота SyncMax в ${other}.`),
                step(2, 'Отправьте ему этот код одним сообщением.'),
                step(3, 'Вернитесь сюда — аккаунты будут связаны.')
            ])
        ]),
        refreshBtn
    ];
}

function step(number, text) {
    return el('li', { className: 'step' }, [
        el('span', { className: 'step__num', text: String(number) }),
        el('p', { className: 'step__text', text })
    ]);
}

/** Экран связанного аккаунта: список связок и действия над ними. */
async function linkedView(profile, ctx) {
    const links = await api.chatLinks();

    const createBtn = button('Создать связку', {
        onClick: () => {
            platform.haptic('light');
            openCreateSheet(ctx);
        }
    });

    if (links.length === 0) {
        return [
            emptyState({
                paths: ICON_LINK,
                title: 'Пока нет ни одной связки',
                text: 'Свяжите чат в MAX с чатом в Telegram — и сообщения начнут пересылаться между ними.'
            }),
            createBtn
        ];
    }

    return [
        ...links.map((link) => linkCard(link, ctx)),
        createBtn
    ];
}

export async function renderLinks(container, ctx) {
    clear(container);
    container.append(
        el('div', { className: 'skeleton' }),
        el('div', { className: 'skeleton' })
    );

    let profile;
    try {
        profile = await api.profile();
    } catch (error) {
        clear(container).append(errorState(error, ctx));
        return;
    }

    ctx.setSubtitle(profile.linked
        ? `${platform.title(profile.messenger)} ↔ ${platform.title(profile.linkedMessenger)}`
        : `Вы в ${platform.title(profile.messenger)}`);

    try {
        const content = profile.linked ? await linkedView(profile, ctx) : notLinkedView(profile, ctx);
        clear(container).append(...content);
    } catch (error) {
        clear(container).append(errorState(error, ctx));
    }
}

function errorState(error, ctx) {
    return emptyState({
        paths: ICON_WARN,
        title: 'Не удалось загрузить',
        text: error.message,
        action: button('Повторить', { onClick: () => ctx.refresh() })
    });
}
