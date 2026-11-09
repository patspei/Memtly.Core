import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/modules/upload-box/index.js', import.meta.url), 'utf8')
    .replace(/^import .*;\r?\n/gm, '').replace('export default galleryUpload;', 'globalThis.UploadBox = UploadBox;');

function setup(files) {
    let progress, hidden = false;
    const requests = [], loaders = [], callbacks = [];
    const $ = selector => selector === 'span#file-upload-progress'
        ? { length: 1, text(value) { progress = value; } }
        : { val: () => 'csrf-token', attr: () => 'false' };
    $.ajax = options => {
        const xhr = options.xhr();
        requests.push({ options, emit: (name, event = {}) => xhr.handlers[name](event) });
    };
    let uuid = 0;
    const context = vm.createContext({ $, crypto: { randomUUID: () => `id-${++uuid}` },
        FormData: class { append() {} },
        window: { XMLHttpRequest: class {
            constructor() { this.handlers = {}; this.upload = { addEventListener: (name, handler) => { this.handlers[name] = handler; } }; }
        } },
        getFileChecksum: async () => 'checksum', localization: { translate: value => value },
        displayLoader: text => { loaders.push(text); progress = '0%'; }, hideLoader: () => { hidden = true; },
        setTimeout: callback => callbacks.push(callback)
    });
    vm.runInContext(source, context);
    const viewer = new context.UploadBox();
    viewer.isIdentityRequired = () => false;
    viewer.isCollection = () => false;
    const data = { files: files.map(file => ({ size: 40, type: '', slice() { return {}; }, ...file })),
        input: { getAttribute: key => key === 'data-post-gallery-id' ? '1' : '' } };
    return { viewer, requests, loaders, callbacks, start: () => viewer.imageUpload(data),
        progress: () => progress, hidden: () => hidden };
}

test('a fully transferred HEIC photo shows processing while the server response is pending', async () => {
    const state = setup([{ name: 'photo.HEIC' }]);
    await state.start();
    const request = state.requests[0];
    request.emit('progress', { lengthComputable: true, loaded: 1, total: 2 });
    assert.equal(state.progress(), '(50%)');
    request.emit('progress', { lengthComputable: true, loaded: 2, total: 2 });
    assert.equal(state.progress(), 'Foto wird verarbeitet …');
    request.emit('load');
    assert.equal(state.progress(), 'Foto wird verarbeitet …');
    assert.equal(state.hidden(), false);
    assert.equal(state.requests.length, 1);
    assert.equal(state.callbacks.length, 0);
});

test('only the final chunk shows processing, including transfers without a computable length', async () => {
    const state = setup([{ name: 'photo.jpg' }]);
    state.viewer.chunk_size = 20;
    await state.start();
    state.requests[0].emit('progress', { lengthComputable: true, loaded: 1, total: 1 });
    state.requests[0].emit('load');
    assert.equal(state.progress(), '(50%)');
    state.requests[0].options.success({ success: true, complete: false });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(state.requests.length, 2);
    state.requests[1].emit('progress', { lengthComputable: false });
    state.requests[1].emit('load');
    assert.equal(state.progress(), 'Foto wird verarbeitet …');
    assert.equal(state.hidden(), false);
});

test('the next file resets progress and video processing uses the generic file label', async () => {
    const state = setup([{ name: 'photo.webp' }, { name: 'video.mov', type: 'video/quicktime' }]);
    await state.start();
    state.requests[0].emit('load');
    assert.equal(state.progress(), 'Foto wird verarbeitet …');
    state.requests[0].options.success({ success: true, complete: true });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(state.progress(), '0%');
    assert.equal(state.loaders.length, 2);
    state.requests[1].emit('load');
    assert.equal(state.progress(), 'Datei wird verarbeitet …');
});
