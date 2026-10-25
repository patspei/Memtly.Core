export function formatSize(bytes) {
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let value = Math.max(0, Number(bytes));
    let unit = 0;
    while (value >= 1000 && unit < units.length - 1) { value /= 1000; unit++; }
    return `${value.toLocaleString('de-AT', { maximumFractionDigits: 1 })} ${units[unit]}`;
}

export function confirmDownload(info, proceed) {
    document.getElementById('download-confirm')?.remove();
    const dialog = document.createElement('dialog');
    dialog.id = 'download-confirm';
    dialog.className = 'wedding-dialog';
    dialog.setAttribute('aria-labelledby', 'download-confirm-title');
    dialog.innerHTML = `<p class="wedding-eyebrow">Erinnerungen mitnehmen</p>
        <h2 id="download-confirm-title">Download vorbereiten</h2>
        <p>${Number(info.count)} ${info.count === 1 ? 'Datei' : 'Dateien'} · Originalqualität</p>
        <p class="download-total ${info.bytes >= 500000000 ? 'download-total-large' : ''}">${formatSize(info.bytes)}</p>
        <p>${info.bytes >= 500000000 ? 'Großer Download: Am besten über WLAN herunterladen. ' : ''}Bei ZIP-Archiven kann die Größe etwas abweichen. Die Vorbereitung kann einen Moment dauern.</p>
        <div class="wedding-dialog-actions"><button type="button" class="wedding-button wedding-button-outline" data-cancel>Abbrechen</button><button type="button" class="wedding-button" data-confirm>Herunterladen</button></div>`;
    document.body.append(dialog);
    dialog.querySelector('[data-cancel]').onclick = () => dialog.close();
    dialog.querySelector('[data-confirm]').onclick = () => { dialog.close(); proceed(); };
    dialog.addEventListener('close', () => dialog.remove(), { once: true });
    dialog.showModal();
    dialog.querySelector('[data-cancel]').focus();
}

export async function downloadArchive(data) {
    try {
        const info = await $.ajax({ url: '/Gallery/DownloadInfo', method: 'POST', data });
        confirmDownload(info, () => {
            // Native streaming download avoids holding a large archive in the browser's memory.
            const form = document.createElement('form');
            form.method = 'POST'; form.action = '/Gallery/DownloadGallery'; form.hidden = true;
            for (const [key, value] of Object.entries(data)) {
                for (const item of Array.isArray(value) ? value : [value]) {
                    if (item == null) continue;
                    const input = document.createElement('input');
                    input.name = key; input.value = item; form.append(input);
                }
            }
            document.body.append(form); form.submit(); form.remove();
        });
    } catch {
        showDownloadError();
    }
}

export function showDownloadError() {
    import('@modules/message-box').then(({ displayMessage }) => displayMessage('Download', 'Der Download ist nicht verfügbar. Bitte prüft euren Galeriezugang und versucht es erneut.'));
}
