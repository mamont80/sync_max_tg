/*
  Мелкие помощники для сборки разметки. Своего «фреймворка» тут нет и не нужно:
  экранов немного, а createElement оставляет текст пользователя текстом — подстановка
  названий чатов через innerHTML была бы дырой (название чата пишет кто угодно).
*/

export function el(tag, options = {}, children = []) {
    const node = document.createElement(tag);

    if (options.className) node.className = options.className;
    if (options.text !== undefined) node.textContent = options.text;
    if (options.html !== undefined) node.innerHTML = options.html;

    for (const [name, value] of Object.entries(options.attrs || {})) {
        if (value === null || value === undefined || value === false) continue;
        node.setAttribute(name, value === true ? '' : String(value));
    }

    for (const [event, handler] of Object.entries(options.on || {})) {
        node.addEventListener(event, handler);
    }

    for (const child of [].concat(children)) {
        if (child === null || child === undefined || child === false) continue;
        node.append(child);
    }

    return node;
}

/** Значок svg по набору путей — иконки рисуются одним контуром, без внешних файлов. */
export function icon(paths, className = '') {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('fill', 'none');
    svg.setAttribute('stroke', 'currentColor');
    svg.setAttribute('stroke-width', '1.6');
    svg.setAttribute('stroke-linecap', 'round');
    svg.setAttribute('stroke-linejoin', 'round');
    if (className) svg.setAttribute('class', className);

    for (const d of [].concat(paths)) {
        const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', d);
        svg.append(path);
    }
    return svg;
}

export function clear(node) {
    node.replaceChildren();
    return node;
}

/** Пустое состояние/ошибка: значок, заголовок, пояснение и (необязательно) кнопка. */
export function emptyState({ paths, title, text, action }) {
    return el('div', { className: 'empty' }, [
        icon(paths, 'empty__icon'),
        el('p', { className: 'empty__title', text: title }),
        text ? el('p', { className: 'empty__text', text }) : null,
        action || null
    ]);
}

export function button(text, { variant = '', onClick, disabled = false } = {}) {
    return el('button', {
        className: `btn ${variant}`.trim(),
        text,
        attrs: { type: 'button', disabled },
        on: onClick ? { click: onClick } : {}
    });
}
