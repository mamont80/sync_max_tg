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
    },
    stats: {
        paths: ['M5 19V11', 'M12 19V5', 'M19 19v-5', 'M3.5 21.5h17'],
        heading: 'Статистика в разработке',
        text: 'Покажем, сколько сообщений перенесено по каждой связке и когда пересылка была в последний раз.'
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
