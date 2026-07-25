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

/**
 * Название стороны связки. У связок, созданных до миграции M005, названий по сторонам
 * нет — тогда показываем хотя бы тип чата, а не пустое место.
 */
export function sideName(title, kind) {
    if (title) return title;
    return KIND_LABEL[kind] ? `Без названия (${KIND_LABEL[kind]})` : 'Без названия';
}
