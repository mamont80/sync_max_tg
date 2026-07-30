/*
  Экран «Статистика» — сколько сообщений и какой объём перенесён между мессенджерами.

  Данные приходят одним запросом (/api/miniapp/stats): за всё время, по дням, по месяцам
  и по связкам. Графиков здесь намеренно нет — вопросы у экрана счётные («сколько всего»,
  «что занимает объём», «какая связка активнее»), и на них отвечают числа и доли, а не
  кривая. Доли рисуются одним цветом: тип трафика называет подпись рядом, цвет ничего
  не кодирует.
*/

import { api } from '../api.js';
import { el, clear, emptyState, button } from '../dom.js';
import { formatBytes, formatCount, formatDay, formatMonth, plural } from '../format.js';

const ICON_STATS = ['M5 19V11', 'M12 19V5', 'M19 19v-5', 'M3.5 21.5h17'];
const ICON_WARN = ['M12 8.5v4.2', 'M12 16.2h0', 'M12 3.6 21 19.2H3Z'];

/** Виды вложений в порядке показа: объём чаще всего съедают видео, поэтому они первыми. */
const TRAFFIC_KINDS = [
    { key: 'video', label: 'Видео и кружки' },
    { key: 'photo', label: 'Фото' },
    { key: 'audio', label: 'Аудио и голосовые' },
    { key: 'file', label: 'Файлы' }
];

/** Крупная плитка с одним показателем. */
function statTile(value, label) {
    return el('div', { className: 'stat' }, [
        el('p', { className: 'stat__value', text: value }),
        el('p', { className: 'stat__label', text: label })
    ]);
}

/** Строка «подпись — значение» с полоской доли. Полоска одноцветная: доля — это величина. */
function meterRow(label, value, share) {
    return el('div', { className: 'meter' }, [
        el('div', { className: 'meter__head' }, [
            el('span', { className: 'meter__label', text: label }),
            el('span', { className: 'meter__value', text: value })
        ]),
        el('div', { className: 'meter__track' }, [
            el('div', { className: 'meter__fill', attrs: { style: `width: ${Math.round(share * 100)}%` } })
        ])
    ]);
}

function totalsCard(total) {
    const directions = el('div', { className: 'status' }, [
        el('span', { className: 'badge badge--max', text: 'MAX' }),
        el('span', { text: `→ ${formatCount(total.maxToTg)}` }),
        el('span', { className: 'badge badge--tg', text: 'TG' }),
        el('span', { text: `→ ${formatCount(total.tgToMax)}` })
    ]);

    return el('section', { className: 'card' }, [
        el('p', { className: 'card__title', text: 'За всё время' }),
        el('div', { className: 'stat-grid' }, [
            statTile(formatCount(total.messages), plural(total.messages, 'сообщение', 'сообщения', 'сообщений')),
            statTile(formatBytes(total.bytes), 'перенесено')
        ]),
        directions
    ]);
}

/** Из чего складывается объём. Текст показываем отдельной строкой — он не вложение. */
function trafficCard(total) {
    const rows = TRAFFIC_KINDS
        .map((kind) => ({
            label: kind.label,
            count: total[`${kind.key}Count`],
            bytes: total[`${kind.key}Bytes`]
        }))
        .filter((row) => row.count > 0);

    if (rows.length === 0) {
        return null;
    }

    const max = Math.max(...rows.map((row) => row.bytes), 1);

    return el('section', { className: 'card' }, [
        el('p', { className: 'card__title', text: 'Из чего складывается объём' }),
        ...rows.map((row) => meterRow(
            `${row.label} · ${formatCount(row.count)}`,
            formatBytes(row.bytes),
            row.bytes / max
        )),
        el('p', { className: 'card__note', text: `Текст сообщений: ${formatBytes(total.textBytes)}.` })
    ]);
}

/** Периоды с переключателем «Дни / Месяцы». Состояние — локальное, экран не перезагружается. */
function periodsCard(stats) {
    const card = el('section', { className: 'card' });
    let mode = 'days';

    function rows() {
        const list = mode === 'days' ? stats.days : stats.months;
        const format = mode === 'days' ? formatDay : formatMonth;

        if (list.length === 0) {
            return [el('p', { className: 'card__note', text: 'За этот период пересылок не было.' })];
        }

        return list.map((item) => el('div', { className: 'row' }, [
            el('div', { className: 'row__text' }, [
                el('p', { className: 'row__label', text: format(item.period) }),
                el('p', {
                    className: 'row__hint',
                    text: `MAX → TG: ${formatCount(item.maxToTg)} · TG → MAX: ${formatCount(item.tgToMax)}`
                })
            ]),
            el('div', { className: 'row__side' }, [
                el('p', { className: 'row__label', text: formatCount(item.messages) }),
                el('p', { className: 'row__hint', text: formatBytes(item.bytes) })
            ])
        ]));
    }

    function switcher() {
        return el('div', { className: 'segmented segmented--pair' }, [
            ['days', 'Дни'],
            ['months', 'Месяцы']
        ].map(([code, label]) => el('button', {
            className: 'segmented__item',
            text: label,
            attrs: { type: 'button', 'aria-pressed': String(mode === code) },
            on: {
                click: () => {
                    if (mode === code) return;
                    mode = code;
                    render();
                }
            }
        })));
    }

    function render() {
        clear(card).append(
            el('p', { className: 'card__title', text: 'По периодам' }),
            switcher(),
            ...rows()
        );
    }

    render();
    return card;
}

function linksCard(links) {
    if (links.length === 0) {
        return null;
    }

    return el('section', { className: 'card' }, [
        el('p', { className: 'card__title', text: 'По связкам' }),
        ...links.map((link) => el('div', { className: 'row' }, [
            el('div', { className: 'row__text' }, [
                el('p', { className: 'row__label', text: link.deleted ? 'Удалённая связка' : link.title }),
                el('p', { className: 'row__hint', text: formatBytes(link.bytes) })
            ]),
            el('span', { className: 'chip', text: formatCount(link.messages) })
        ]))
    ]);
}

export async function renderStats(container, ctx) {
    clear(container).append(
        el('div', { className: 'skeleton' }),
        el('div', { className: 'skeleton' })
    );

    let stats;
    try {
        stats = await api.stats();
    } catch (error) {
        clear(container).append(emptyState({
            paths: ICON_WARN,
            title: 'Не удалось загрузить',
            text: error.message,
            action: button('Повторить', { onClick: () => ctx.refresh() })
        }));
        return;
    }

    if (!stats.linked) {
        clear(container).append(emptyState({
            paths: ICON_STATS,
            title: 'Статистики пока нет',
            text: 'Она ведётся по паре связанных аккаунтов. Свяжите аккаунты на вкладке «Связки» — '
                + 'и здесь появятся сообщения и объём.'
        }));
        return;
    }

    if (stats.total.messages === 0) {
        clear(container).append(emptyState({
            paths: ICON_STATS,
            title: 'Пока нечего показать',
            text: 'Как только между связанными чатами начнут ходить сообщения, здесь появятся '
                + 'счётчики за день, месяц и всё время.'
        }));
        return;
    }

    const cards = [
        totalsCard(stats.total),
        trafficCard(stats.total),
        periodsCard(stats),
        linksCard(stats.links)
    ];

    // Карточки без данных возвращают null (нет вложений, нет связок) — их просто нет на экране.
    clear(container).append(...cards.filter(Boolean));
}
