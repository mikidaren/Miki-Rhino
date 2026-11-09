            (() => {
                const key = '__rhinoMagpieNativeEntry';
                const url = __ENTRY_URL__;
                const previous = window[key];
                if (previous && previous.url === url) {
                    previous.refresh(__ENABLED__);
                    return;
                }
                if (previous) previous.dispose();
                let enabled = __ENABLED__;
                const owned = new Set();
                const busy = () => !!document.querySelector('.send.stop, .agent-chip .dot.busy');
                const create = (menu) => {
                    const button = document.createElement('button');
                    button.type = 'button';
                    button.dataset.rhinoMagpieEntry = menu ? 'menu' : 'header';
                    button.className = menu ? 'menu-item' : 'icon-btn';
                    button.title = 'Magpie-Rhino';
                    button.setAttribute('aria-label', 'Magpie');
                    if (menu) {
                        const dot = document.createElement('span');
                        dot.className = 'dot ready';
                        const body = document.createElement('span');
                        body.className = 'body';
                        const label = document.createElement('b');
                        label.textContent = 'Magpie';
                        const detail = document.createElement('span');
                        detail.textContent = 'Magpie-Rhino';
                        body.append(label, detail);
                        button.append(dot, body);
                    } else {
                        button.textContent = 'Magpie';
                    }
                    button.addEventListener('click', (event) => {
                        event.preventDefault();
                        event.stopPropagation();
                        if (enabled && !busy()) window.open(url, '_blank');
                    });
                    owned.add(button);
                    return button;
                };
                const refresh = (value = enabled) => {
                    enabled = value;
                    for (const button of owned) {
                        if (!button.isConnected) owned.delete(button);
                    }
                    let knownMenu = false;
                    for (const menu of document.querySelectorAll('.popover[role="menu"]')) {
                        const separator = menu.querySelector(':scope > .menu-sep');
                        if (!separator) continue;
                        knownMenu = true;
                        if (!menu.querySelector('[data-rhino-magpie-entry="menu"]')) {
                            menu.insertBefore(create(true), separator);
                        }
                    }
                    const header = document.querySelector('header.header');
                    if (header) {
                        const chip = header.querySelector('.agent-chip');
                        const fallback = !chip || (chip.getAttribute('aria-expanded') === 'true' && !knownMenu);
                        const button = header.querySelector('[data-rhino-magpie-entry="header"]');
                        if (fallback && !button) header.append(create(false));
                        if (!fallback && button) {
                            owned.delete(button);
                            button.remove();
                        }
                    }
                    const disabled = !enabled || busy();
                    for (const button of owned) {
                        if (button.disabled !== disabled) button.disabled = disabled;
                    }
                };
                const observer = new MutationObserver(() => refresh());
                window[key] = {
                    url,
                    refresh,
                    dispose() {
                        observer.disconnect();
                        for (const button of owned) button.remove();
                        owned.clear();
                        delete window[key];
                    }
                };
                observer.observe(document.documentElement, {
                    childList: true, subtree: true, attributes: true,
                    attributeFilter: ['class', 'disabled']
                });
                refresh();
            })();