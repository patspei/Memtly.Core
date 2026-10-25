import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/modules/media-viewer/index.js', import.meta.url), 'utf8')
    .replace(/^import .*;\r?\n/gm, '').replace('export default MediaViewer;', 'globalThis.MediaViewer = MediaViewer;');

function setup({ mobile = false, wedding = true } = {}) {
    const handlers = new Map();
    const downloads = [];
    const requests = [];
    const confirmations = [];
    const bus = {
        off() { return this; },
        on(event, selector, callback) { handlers.set(`${event}:${selector}`, callback); return this; }
    };
    const document = { body: { classList: { contains: () => wedding } } };
    const $ = element => element === document ? bus : element;
    $.getJSON = url => {
        requests.push(url);
        return {
            done(callback) { callback({ count: 1, bytes: 1234, filename: 'photo.jpg' }); return this; },
            fail(callback) { this.error = callback; return this; }
        };
    };
    const context = vm.createContext({ $, document, window: { matchMedia: () => ({ matches: mobile }) },
        confirmDownload: (info, proceed) => confirmations.push({ info, proceed }), showDownloadError() {},
        preventDefaults: event => { event.preventDefault(); event.stopPropagation(); } });
    vm.runInContext(source, context);
    const viewer = new context.MediaViewer();
    viewer.download = url => downloads.push(url);
    viewer.bindOpenPopup();
    viewer.bindDownloadButton();
    return { viewer, handlers, downloads, requests, confirmations };
}

function event(target) {
    return { currentTarget: target, prevented: false, stopped: false,
        preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; } };
}

test('phone and coarse-touch galleries suppress lightbox entry and link navigation', () => {
    const { viewer, handlers } = setup({ mobile: true });
    viewer.displayMediaViewer = () => assert.fail('Lightbox opened on mobile');
    const e = event({});
    handlers.get('click:.media-viewer-item')(e);
    viewer.openMediaViewer({});
    assert.ok(e.prevented && e.stopped);
});

test('desktop image clicks still open the lightbox and find the separate selection button', () => {
    const { viewer, handlers } = setup();
    const checkbox = {};
    const item = { closest: () => ({ find: () => checkbox }) };
    let opened;
    viewer.openMediaViewer = element => { opened = element; };
    handlers.get('click:.media-viewer-item')(event(item));
    assert.equal(opened, item);
    viewer.toggleMultiSelectOption = element => assert.equal(element, checkbox);
    handlers.get('click:.media-viewer-item')({ ...event(item), ctrlKey: true });
});

test('other Memtly pages retain their existing mobile viewer', () => {
    assert.equal(setup({ mobile: true, wedding: false }).viewer.mobileLightboxDisabled(), false);
});

test('individual download waits for confirmation and uses the authorized original endpoint', () => {
    const { handlers, requests, confirmations, downloads } = setup({ mobile: true });
    const e = event({ attr: () => '42' });
    handlers.get('click:.btn-download-photo')(e);
    assert.ok(e.prevented && e.stopped);
    assert.deepEqual(requests, ['/Media/DownloadInfo/42']);
    assert.equal(confirmations[0].info.filename, 'photo.jpg');
    assert.equal(confirmations[0].info.bytes, 1234);
    assert.deepEqual(downloads, []);
    confirmations[0].proceed();
    assert.deepEqual(downloads, ['/Media/Original/42?download=true']);
});

test('delegated downloads also handle newly appended and replaced gallery cards', () => {
    const { handlers, requests, confirmations, downloads } = setup({ mobile: true });
    for (const id of ['7', '8']) {
        handlers.get('click:.btn-download-photo')(event({ attr: () => id }));
        confirmations.at(-1).proceed();
    }
    assert.deepEqual(requests, ['/Media/DownloadInfo/7', '/Media/DownloadInfo/8']);
    assert.deepEqual(downloads, ['/Media/Original/7?download=true', '/Media/Original/8?download=true']);
});

test('checkbox interaction remains separate from image and download actions', () => {
    const { viewer, handlers, requests } = setup({ mobile: true });
    viewer.bindMultiSelectButtons();
    const checkbox = {};
    viewer.toggleMultiSelectOption = element => assert.equal(element, checkbox);
    const e = event(checkbox);
    handlers.get('click:.btn-multi-select')(e);
    assert.ok(e.prevented && e.stopped);
    assert.deepEqual(requests, []);
});

test('desktop lightbox downloads share the same confirmation and original download', () => {
    const { handlers, requests, confirmations, downloads } = setup();
    handlers.get('click:.media-viewer-download')(event({ closest: () => ({ data: () => '/Media/Original/9' }) }));
    assert.deepEqual(requests, ['/Media/DownloadInfo/9']);
    confirmations[0].proceed();
    assert.deepEqual(downloads, ['/Media/Original/9?download=true']);
});

test('upload card opens the existing share dialog and its cancel action closes it', () => {
    const weddingSource = readFileSync(new URL('../src/modules/wedding/index.js', import.meta.url), 'utf8')
        .replace('export default function initWedding()', 'function initWedding()');
    const events = new Map();
    let opened = false;
    const dialog = { showModal() { opened = true; }, close() { opened = false; },
        querySelectorAll: () => [{ addEventListener: (_, action) => events.set('close', action) }] };
    const context = vm.createContext({ document: { getElementById: id => id === 'wedding-share-dialog' ? dialog : null,
        querySelectorAll: () => [{ addEventListener: (_, action) => events.set('open', action) }] } });
    vm.runInContext(weddingSource + '\ninitWedding();', context);
    events.get('open')();
    assert.equal(opened, true);
    events.get('close')();
    assert.equal(opened, false);
});
