/*
  Раздел «Поддержка»: связь с разработчиком и редкие действия над аккаунтом.
  Отвязка аккаунтов живёт здесь, а не на экране связок: делают её раз в жизни, а рядом
  со списком она только мозолила бы глаза и её легко было бы нажать по ошибке.
*/

import { api } from '../api.js';
import { platform } from '../platform.js';
import { el, clear, emptyState, button } from '../dom.js';
import { confirmDialog, alertDialog } from '../dialog.js';

const DEVELOPER_LINK = 'https://t.me/shumilovmikhail';
const DEVELOPER_HANDLE = '@shumilovmikhail';

const ICON_WARN = ['M12 8.5v4.2', 'M12 16.2h0', 'M12 3.6 21 19.2H3Z'];

function contactCard() {
    return el('section', { className: 'card' }, [
        el('p', { className: 'card__title', text: 'Связь с разработчиком' }),
        el('p', {
            className: 'card__note',
            text: `Вопросы, ошибки и пожелания — напишите напрямую в Telegram: ${DEVELOPER_HANDLE}`
        }),
        el('div', { className: 'card__actions' }, [
            button(`Написать ${DEVELOPER_HANDLE}`, {
                variant: 'btn--secondary',
                onClick: () => {
                    platform.haptic('light');
                    platform.openLink(DEVELOPER_LINK);
                }
            })
        ])
    ]);
}

function botCard() {
    return el('section', { className: 'card' }, [
        el('p', { className: 'card__title', text: 'Помощь по боту' }),
        el('p', {
            className: 'card__note',
            text: 'Написать можно и прямо в чат с ботом — сообщения читают. '
                + 'Команда /link в группе выбирает чат для связки, /app открывает это приложение.'
        })
    ]);
}

/** Показывается только связанному аккаунту: отвязывать нечего, пока связки нет. */
function unlinkCard(ctx) {
    const unlinkBtn = button('Отвязать аккаунт', {
        variant: 'btn--danger',
        onClick: async () => {
            const ok = await confirmDialog({
                title: 'Отвязать аккаунт?',
                text: 'Связь между аккаунтами MAX и Telegram будет разорвана, а все связки чатов '
                    + 'между ними удалены. Связать заново можно в любой момент.',
                confirmText: 'Отвязать',
                destructive: true
            });
            if (!ok) return;

            unlinkBtn.disabled = true;
            try {
                await api.unlink();
                platform.haptic('success');
                ctx.refresh();
            } catch (error) {
                alertDialog({ title: 'Не удалось отвязать', text: error.message });
                unlinkBtn.disabled = false;
            }
        }
    });

    return el('section', { className: 'card' }, [
        el('p', { className: 'card__title', text: 'Аккаунт' }),
        el('p', {
            className: 'card__note',
            text: 'Разорвать связь между аккаунтами MAX и Telegram. Все связки чатов между '
                + 'ними будут удалены, а пересылка прекратится. Связать заново можно в любой момент.'
        }),
        el('div', { className: 'card__actions' }, [unlinkBtn])
    ]);
}

export async function renderSupport(container, ctx) {
    clear(container).append(el('div', { className: 'skeleton' }));

    let profile;
    try {
        profile = await api.profile();
    } catch (error) {
        clear(container).append(emptyState({
            paths: ICON_WARN,
            title: 'Не удалось загрузить',
            text: error.message,
            action: button('Повторить', { onClick: () => ctx.refresh() })
        }));
        return;
    }

    const cards = [contactCard(), botCard()];
    if (profile.linked) cards.push(unlinkCard(ctx));

    clear(container).append(...cards);
}
