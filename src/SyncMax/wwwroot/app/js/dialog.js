/*
  Свои диалоги вместо системных.

  У моста каждой платформы есть showConfirm/showAlert, но окна у MAX и Telegram выглядят
  по-разному, а в обычном браузере (отладка) вместо них появляется штатный confirm — то
  есть на трёх площадках три разных вида. Свой диалог даёт один и тот же вид везде,
  как и весь остальной интерфейс.

  Поведение — как принято в мобильных клиентах: затемнение фона, окно по центру,
  разрушающее действие красным, отмена по фону, Esc и системной кнопке «Назад»,
  фокус на безопасной кнопке.
*/

import { platform } from './platform.js';
import { el } from './dom.js';

const layer = el('div', { className: 'dialog', attrs: { hidden: true } });
document.body.append(layer);

/**
 * Показывает окно и обещает результат. Возвращает то, что вернул нажатый вариант:
 * true для подтверждения, false для отмены.
 */
function show({ title, text, actions, dismissValue }) {
    return new Promise((resolve) => {
        const previouslyFocused = document.activeElement;
        let popBack = null;

        const close = (value) => {
            document.removeEventListener('keydown', onKeyDown, true);
            if (popBack) popBack();

            layer.hidden = true;
            layer.replaceChildren();

            // Возвращаем фокус туда, откуда пришли, — иначе он «повисает» на body
            // и следующее нажатие клавиши уходит в никуда.
            if (previouslyFocused && previouslyFocused.focus) previouslyFocused.focus();

            resolve(value);
        };

        const onKeyDown = (event) => {
            if (event.key === 'Escape') {
                event.stopPropagation();
                close(dismissValue);
            }
        };

        const buttons = actions.map((action) => el('button', {
            className: `dialog__action ${action.className || ''}`.trim(),
            text: action.text,
            attrs: { type: 'button' },
            on: {
                click: () => {
                    if (action.haptic) platform.haptic(action.haptic);
                    close(action.value);
                }
            }
        }));

        const panel = el('div', {
            className: 'dialog__panel',
            attrs: { role: 'alertdialog', 'aria-modal': 'true', 'aria-labelledby': 'dialog-title' }
        }, [
            el('p', { className: 'dialog__title', text: title, attrs: { id: 'dialog-title' } }),
            text ? el('p', { className: 'dialog__text', text }) : null,
            el('div', {
                // Две кнопки помещаются в ряд, три и больше — только столбиком.
                className: `dialog__actions ${buttons.length > 2 ? 'dialog__actions--column' : ''}`.trim()
            }, buttons)
        ]);

        layer.replaceChildren(
            el('div', { className: 'dialog__backdrop', on: { click: () => close(dismissValue) } }),
            panel
        );
        layer.hidden = false;

        document.addEventListener('keydown', onKeyDown, true);
        popBack = platform.pushBackHandler(() => close(dismissValue));

        // Фокус на последней кнопке: в паре «Отмена / Удалить» это подтверждение,
        // но перед ним пользователь всё равно читает текст, а Esc и «Назад» отменяют.
        buttons[buttons.length - 1].focus();
    });
}

/**
 * Подтверждение действия. destructive — оформить кнопку подтверждения как опасную
 * (удаление, разрыв связи).
 */
export function confirmDialog({ title, text, confirmText = 'Подтвердить', cancelText = 'Отмена', destructive = false }) {
    return show({
        title,
        text,
        dismissValue: false,
        actions: [
            { text: cancelText, value: false, className: 'dialog__action--quiet' },
            {
                text: confirmText,
                value: true,
                className: destructive ? 'dialog__action--danger' : 'dialog__action--primary',
                haptic: destructive ? 'warning' : 'light'
            }
        ]
    });
}

/** Сообщение с единственной кнопкой — для ошибок и того, что нужно дочитать. */
export function alertDialog({ title, text, buttonText = 'Понятно' }) {
    return show({
        title,
        text,
        dismissValue: true,
        actions: [{ text: buttonText, value: true, className: 'dialog__action--primary' }]
    });
}

/*
  Всплывающая подсказка для мелких удач («Код скопирован»): целое окно с кнопкой ради
  такого сообщения — перебор, его пришлось бы закрывать руками.
*/
const toastNode = el('div', { className: 'toast', attrs: { hidden: true, role: 'status' } });
document.body.append(toastNode);

let toastTimer = null;

export function toast(text) {
    clearTimeout(toastTimer);

    toastNode.textContent = text;
    toastNode.hidden = false;
    // Перезапуск анимации появления, если подсказка показывается повторно подряд.
    toastNode.classList.remove('toast--visible');
    void toastNode.offsetWidth;
    toastNode.classList.add('toast--visible');

    toastTimer = setTimeout(() => {
        toastNode.classList.remove('toast--visible');
        setTimeout(() => { toastNode.hidden = true; }, 200);
    }, 2000);
}
