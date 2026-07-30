/*
  Заглушка для разделов, которых ещё нет. Вкладки заведены сразу, чтобы позже добавлять
  только содержимое экрана: навигация, оформление и переходы уже на месте.
*/

import { clear, emptyState } from '../dom.js';

const SOON = {
    subscription: {
        paths: ['M12 3.8 14.4 8.7l5.4.8-3.9 3.8.9 5.4-4.8-2.6-4.8 2.6.9-5.4-3.9-3.8 5.4-.8Z'],
        heading: 'Тарифы скоро появятся',
        text: 'Здесь будут текущий тариф, срок действия и лимиты. Сейчас все возможности доступны без ограничений.'
    }
};

export function renderSoon(container, tab) {
    const config = SOON[tab];
    clear(container).append(emptyState({
        paths: config.paths,
        title: config.heading,
        text: config.text
    }));
}
