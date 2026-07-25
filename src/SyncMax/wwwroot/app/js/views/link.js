/* Карточка одной связки: включение пересылки, направление, удаление. */

import { api } from '../api.js';
import { platform } from '../platform.js';
import { el, button } from '../dom.js';
import { confirmDialog, alertDialog } from '../dialog.js';
import { DIRECTION_LABEL, DIRECTION_HINT } from '../format.js';

const DIRECTIONS = ['both', 'max_to_tg', 'tg_to_max'];

export function openLinkSheet(link, ctx) {
    // Локальная копия: пока лист открыт, интерфейс живёт своим состоянием, а список
    // на экране перечитывается один раз при закрытии — так нажатия не «моргают».
    let current = { ...link };
    let dirty = false;

    const body = el('div');

    const rerender = () => {
        body.replaceChildren(
            activeCard(),
            directionCard(),
            deleteButton()
        );
    };

    /** Общая обёртка вокруг PATCH: откат значения и понятное сообщение при ошибке. */
    const patch = async (changes, previous) => {
        try {
            current = await api.updateChatLink(current.id, changes);
            dirty = true;
            platform.haptic('select');
        } catch (error) {
            current = previous;
            platform.haptic('error');
            alertDialog({ title: 'Не удалось сохранить', text: error.message });
        }
        rerender();
    };

    function activeCard() {
        const toggle = el('button', {
            className: 'switch',
            attrs: { type: 'button', role: 'switch', 'aria-checked': String(current.active) },
            on: {
                click: () => {
                    const previous = { ...current };
                    current = { ...current, active: !current.active };
                    rerender();
                    patch({ active: current.active }, previous);
                }
            }
        });

        return el('section', { className: 'card' }, [
            el('div', { className: 'row' }, [
                el('div', { className: 'row__text' }, [
                    el('p', { className: 'row__label', text: 'Пересылка' }),
                    el('p', {
                        className: 'row__hint',
                        text: current.active
                            ? 'Связка активна'
                            : 'Выключено: не переносятся ни сообщения, ни правки'
                    })
                ]),
                toggle
            ])
        ]);
    }

    function directionCard() {
        const items = DIRECTIONS.map((code) => el('button', {
            className: 'segmented__item',
            text: DIRECTION_LABEL[code],
            attrs: { type: 'button', 'aria-pressed': String(current.direction === code) },
            on: {
                click: () => {
                    if (current.direction === code) return;
                    const previous = { ...current };
                    current = { ...current, direction: code };
                    rerender();
                    patch({ direction: code }, previous);
                }
            }
        }));

        return el('section', { className: 'card' }, [
            el('p', { className: 'row__label', text: 'Направление' }),
            el('p', { className: 'row__hint', text: DIRECTION_HINT[current.direction] || '' }),
            el('div', { className: 'segmented' }, items)
        ]);
    }

    function deleteButton() {
        return button('Удалить связку', {
            variant: 'btn--danger',
            onClick: async () => {
                const ok = await confirmDialog({
                    title: 'Удалить связку?',
                    text: `«${current.title}» — сообщения между этими чатами пересылаться перестанут. `
                        + 'Уже пересланные сообщения останутся на месте.',
                    confirmText: 'Удалить',
                    destructive: true
                });
                if (!ok) return;

                try {
                    await api.deleteChatLink(current.id);
                    platform.haptic('success');
                    ctx.closeSheet();
                    ctx.refresh();
                } catch (error) {
                    platform.haptic('error');
                    alertDialog({ title: 'Не удалось удалить', text: error.message });
                }
            }
        });
    }

    rerender();

    ctx.openSheet(link.title, body, () => {
        // Перечитываем список, только если что-то действительно поменялось.
        if (dirty) ctx.refresh();
    });
}
