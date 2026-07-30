/* Общие подписи для интерфейса — отдельным модулем, чтобы экраны не зависели друг от друга. */

/** Направление пересылки, как оно хранится в chat_links.repost_type. */
export const DIRECTION_LABEL = {
    both: 'MAX ⇄ TG',
    max_to_tg: 'MAX → TG',
    tg_to_max: 'TG → MAX'
};

export const DIRECTION_HINT = {
    both: 'Сообщения переносятся в обе стороны.',
    max_to_tg: 'Сообщения переносятся только из MAX в Telegram.',
    tg_to_max: 'Сообщения переносятся только из Telegram в MAX.'
};

const KIND_LABEL = { chat: 'группа', channel: 'канал' };

const BYTE_UNITS = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];

/*
  Названия месяцев заданы списком, а не через toLocaleDateString: интерфейс всё равно
  только на русском, а полагаться на наличие русской локали в вебвью мессенджера нельзя —
  где её нет, даты молча стали бы английскими.
*/
const MONTHS_GENITIVE = [
    'января', 'февраля', 'марта', 'апреля', 'мая', 'июня',
    'июля', 'августа', 'сентября', 'октября', 'ноября', 'декабря'
];

const MONTHS_NOMINATIVE = [
    'январь', 'февраль', 'март', 'апрель', 'май', 'июнь',
    'июль', 'август', 'сентябрь', 'октябрь', 'ноябрь', 'декабрь'
];

/** Объём с единицей: 1536 -> «1,5 КБ». Дробная часть — только у мелких значений. */
export function formatBytes(bytes) {
    let value = Number(bytes) || 0;
    let unit = 0;

    while (value >= 1024 && unit < BYTE_UNITS.length - 1) {
        value /= 1024;
        unit++;
    }

    const digits = unit > 0 && value < 10 ? 1 : 0;
    return `${value.toFixed(digits).replace('.', ',')} ${BYTE_UNITS[unit]}`;
}

/** Число с разделением разрядов неразрывным пробелом: 12345 -> «12 345». */
export function formatCount(value) {
    return String(Number(value) || 0).replace(/\B(?=(\d{3})+(?!\d))/g, ' ');
}

/** «2026-07-29» -> «29 июля» (или «29 июля 2025», если год не текущий). */
export function formatDay(day) {
    const [year, month, date] = day.split('-').map(Number);
    const name = MONTHS_GENITIVE[month - 1] || day;
    const suffix = year === new Date().getFullYear() ? '' : ` ${year}`;
    return `${date} ${name}${suffix}`;
}

/** «2026-07» -> «июль 2026». */
export function formatMonth(period) {
    const [year, month] = period.split('-').map(Number);
    return `${MONTHS_NOMINATIVE[month - 1] || period} ${year}`;
}

/** Правильная форма слова при числе: 1 сообщение, 2 сообщения, 5 сообщений. */
export function plural(count, one, few, many) {
    const n = Math.abs(Number(count) || 0) % 100;
    const last = n % 10;

    if (n > 10 && n < 20) return many;
    if (last > 1 && last < 5) return few;
    if (last === 1) return one;
    return many;
}

/**
 * Название стороны связки. У связок, созданных до миграции M005, названий по сторонам
 * нет — тогда показываем хотя бы тип чата, а не пустое место.
 */
export function sideName(title, kind) {
    if (title) return title;
    return KIND_LABEL[kind] ? `Без названия (${KIND_LABEL[kind]})` : 'Без названия';
}
