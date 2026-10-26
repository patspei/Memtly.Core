let pending;
const storageKey = 'memtly.wedding.uploaderName';

export function ensureWeddingIdentity(form) {
    if (form?.dataset.weddingUpload !== 'true' || form.dataset.identityReady === 'true') return Promise.resolve(true);
    if (pending) return pending;
    pending = new Promise(resolve => {
        const dialog = document.getElementById('wedding-identity-dialog');
        const input = dialog.querySelector('input[name="uploaderName"]');
        const remember = dialog.querySelector('input[name="rememberName"]');
        const error = dialog.querySelector('[role="alert"]');
        const buttons = [...dialog.querySelectorAll('button')];
        let stored = '';
        try { stored = localStorage.getItem(storageKey) || ''; } catch { /* Storage can be disabled. */ }
        input.value = stored;
        const finish = result => { pending = undefined; resolve(result); };
        const save = async (name, persist) => {
            buttons.forEach(button => { button.disabled = true; });
            error.textContent = '';
            try {
                const body = new URLSearchParams({ name, weddingUpload: 'true',
                    __RequestVerificationToken: form.querySelector('[name="__RequestVerificationToken"]').value });
                const response = await fetch('/Home/SetIdentity', { method: 'POST', body, credentials: 'same-origin' });
                const result = await response.json();
                if (!response.ok || !result.success) throw new Error();
                try {
                    if (persist && name) localStorage.setItem(storageKey, name);
                    else localStorage.removeItem(storageKey);
                } catch { /* Consent still applies when storage is unavailable. */ }
                form.dataset.identityReady = 'true';
                dialog.close();
                finish(true);
            } catch {
                error.textContent = 'Der Name konnte nicht übernommen werden. Bitte verwende bis zu 80 Zeichen oder fahre anonym fort.';
                if (!dialog.open) dialog.showModal();
            } finally { buttons.forEach(button => { button.disabled = false; }); }
        };
        dialog.querySelector('[data-identity-named]').onclick = () => {
            if (!input.reportValidity()) return;
            save(input.value.trim(), remember.checked);
        };
        dialog.querySelector('[data-identity-anonymous]').onclick = () => save('', false);
        dialog.oncancel = event => { event.preventDefault(); dialog.close(); finish(false); };
        if (stored && stored.length <= 80) save(stored, true);
        else dialog.showModal();
    });
    return pending;
}
