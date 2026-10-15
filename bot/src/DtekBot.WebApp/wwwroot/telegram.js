window.dtekTg = (function () {
    const webApp = () => (window.Telegram && window.Telegram.WebApp) || null;
    let backRef = null;
    const onBack = () => { if (backRef) backRef.invokeMethodAsync('OnBackPressed'); };

    return {
        getInitData: () => { const w = webApp(); return (w && w.initData) || ''; },
        ready: () => { const w = webApp(); if (w) w.ready(); },
        expand: () => {
            const w = webApp();
            if (!w) return;
            w.expand();
            if (typeof w.disableVerticalSwipes === 'function') w.disableVerticalSwipes();
        },
        close: () => { const w = webApp(); if (w) w.close(); },
        haptic: (kind) => {
            const h = webApp() && webApp().HapticFeedback;
            if (!h) return;
            if (kind === 'success' || kind === 'error' || kind === 'warning') h.notificationOccurred(kind);
            else h.impactOccurred(kind || 'light');
        },
        showBackButton: (ref) => {
            const b = webApp() && webApp().BackButton;
            if (!b) return;
            backRef = ref;
            b.offClick(onBack);
            b.onClick(onBack);
            b.show();
        },
        hideBackButton: () => {
            const b = webApp() && webApp().BackButton;
            if (!b) return;
            b.offClick(onBack);
            b.hide();
            backRef = null;
        },
        // Inserts text at the caret/selection; returns the new value, or null if it would exceed maxLength.
        insertAtCaret: (id, text, maxLength) => {
            const el = document.getElementById(id);
            if (!el) return null;
            const start = el.selectionStart ?? el.value.length;
            const end = el.selectionEnd ?? start;
            const next = el.value.slice(0, start) + text + el.value.slice(end);
            if (maxLength > 0 && next.length > maxLength) return null;
            el.value = next;
            const caret = start + text.length;
            el.focus();
            el.setSelectionRange(caret, caret);
            return next;
        },
    };
})();
