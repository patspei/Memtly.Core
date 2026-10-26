import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/modules/wedding/uploader-identity.js', import.meta.url), 'utf8')
    .replace('export function ensureWeddingIdentity', 'function ensureWeddingIdentity');

function setup({ stored, ready = false, storageDisabled = false } = {}) {
    const requests = [];
    const storage = new Map(stored ? [['memtly.wedding.uploaderName', stored]] : []);
    const named = {}, anonymous = {}, input = { value: '', reportValidity: () => true }, remember = { checked: false }, error = {};
    const fields = { 'input[name="uploaderName"]': input, 'input[name="rememberName"]': remember,
        '[role="alert"]': error, '[data-identity-named]': named, '[data-identity-anonymous]': anonymous };
    const dialog = { open: false, querySelector: key => fields[key], querySelectorAll: () => [named, anonymous],
        showModal() { this.open = true; }, close() { this.open = false; } };
    const form = { dataset: { weddingUpload: 'true', identityReady: String(ready) }, querySelector: () => ({ value: 'csrf-token' }) };
    let succeeds = true;
    const context = vm.createContext({ URLSearchParams, document: { getElementById: () => dialog },
        localStorage: {
            getItem: key => { if (storageDisabled) throw Error(); return storage.get(key); },
            setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key)
        },
        fetch: async (url, options) => { requests.push({ url, options }); return { ok: succeeds, json: async () => ({ success: succeeds }) }; }
    });
    vm.runInContext(source, context);
    return { context, form, dialog, named, anonymous, input, remember, error, requests, storage,
        start: () => context.ensureWeddingIdentity(form), fail: value => { succeeds = !value; } };
}

test('first upload waits for server identity and remembers only with consent', async () => {
    const state = setup();
    const pending = state.start();
    assert.equal(state.dialog.open, true);
    assert.equal(state.requests.length, 0);
    state.input.value = 'Julia & Patrick';
    state.remember.checked = true;
    state.named.onclick();
    assert.equal(await pending, true);
    assert.equal(state.requests[0].options.body.get('name'), 'Julia & Patrick');
    assert.equal(state.requests[0].options.body.get('__RequestVerificationToken'), 'csrf-token');
    assert.equal(state.storage.get('memtly.wedding.uploaderName'), 'Julia & Patrick');
    assert.equal(await state.start(), true);
    assert.equal(state.requests.length, 1);
});

test('anonymous and unremembered names stay out of persistent storage', async () => {
    for (const anonymous of [true, false]) {
        const state = setup();
        const pending = state.start();
        state.input.value = 'Guest';
        (anonymous ? state.anonymous : state.named).onclick();
        assert.equal(await pending, true);
        assert.equal(state.requests[0].options.body.get('name'), anonymous ? '' : 'Guest');
        assert.equal(state.storage.size, 0);
    }
});

test('remembered name is restored through the server before uploading', async () => {
    const state = setup({ stored: 'Remembered guest' });
    assert.equal(await state.start(), true);
    assert.equal(state.requests[0].options.body.get('name'), 'Remembered guest');
    assert.equal(state.dialog.open, false);
});

test('disabled storage still permits anonymous uploads', async () => {
    const state = setup({ storageDisabled: true });
    const pending = state.start();
    state.anonymous.onclick();
    assert.equal(await pending, true);
});

test('server failure keeps the upload waiting and allows an anonymous retry', async () => {
    const state = setup({ stored: 'Old guest' });
    state.fail(true);
    const pending = state.start();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(state.dialog.open, true);
    assert.equal(state.form.dataset.identityReady, 'false');
    state.fail(false);
    state.anonymous.onclick();
    assert.equal(await pending, true);
    assert.equal(state.storage.size, 0);
});
