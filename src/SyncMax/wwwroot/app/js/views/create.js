/*
  Инструкция «как создать связку». Само создание остаётся в чате: чат выбирается тем,
  что бота добавляют в группу либо пишут там /link, а из веб-приложения список чатов
  пользователя не виден — ни MAX, ни Telegram его боту не отдают.
*/

import { platform } from '../platform.js';
import { el, button } from '../dom.js';

function step(number, node) {
    return el('li', { className: 'step' }, [
        el('span', { className: 'step__num', text: String(number) }),
        el('p', { className: 'step__text' }, node)
    ]);
}

function text(parts) {
    return parts.map((part) => (typeof part === 'string' ? part : el('b', { text: part.b })));
}

export function openCreateSheet(ctx) {
    const other = platform.title(platform.messenger === 'max' ? 'tg' : 'max');

    const body = el('div', {}, [
        el('section', { className: 'card' }, [
            el('ol', { className: 'steps' }, [
                step(1, text([
                    'Добавьте бота в нужную группу или канал и назначьте его ',
                    { b: 'администратором' },
                    ' — без прав администратора он не сможет писать.'
                ])),
                step(2, text([
                    'Отправьте в этой группе команду ',
                    { b: '/link' },
                    '. Если бот только что добавлен, шаг можно пропустить — чат уже выбран.'
                ])),
                step(3, text([
                    'Повторите то же самое в ',
                    { b: other },
                    ': добавьте бота во второй чат и отправьте там ',
                    { b: '/link' },
                    '.'
                ])),
                step(4, text([
                    'Как только оба чата выбраны, связка создаётся автоматически и появляется в этом списке.'
                ]))
            ])
        ]),
        button('Понятно', { onClick: () => ctx.closeSheet() })
    ]);

    ctx.openSheet('Как создать связку', body);
}
