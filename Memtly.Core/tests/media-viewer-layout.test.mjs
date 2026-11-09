import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../src/modules/media-viewer/index.js', import.meta.url), 'utf8')
    .replace(/^import .*;\r?\n/gm, '').replace('export default MediaViewer;', 'globalThis.MediaViewer = MediaViewer;');

test('closing a video cancels its pending sizing timer before another image opens', () => {
    const cancelled = [];
    const context = vm.createContext({ clearTimeout: timer => cancelled.push(timer),
        $: () => ({ hide() {}, remove() {} }) });
    vm.runInContext(source, context);
    const viewer = new context.MediaViewer();
    viewer.resizePopupTimeout = 42;
    viewer.hideMediaViewer();
    assert.deepEqual(cancelled, [42]);
    assert.equal(viewer.resizePopupTimeout, null);
});

test('already loaded previews open immediately and a later load event cannot initialize twice', () => {
    const image = { complete: true, naturalWidth: 1920, dataset: {} };
    let loaded, opened = 0;
    const window = {};
    const viewerData = { data: key => key === 'type' ? 'image' : '/Media/Preview/42' };
    const images = { off() { return this; }, on(_, callback) { loaded = callback; return this; },
        each(callback) { callback(0, image); return this; } };
    const events = { off() { return this; }, on() { return this; } };
    const $ = value => value === window ? events : value === image ? { closest: () => viewerData } : images;
    const context = vm.createContext({ $, window });
    vm.runInContext(source, context);
    const viewer = new context.MediaViewer();
    viewer.initMediaViewImage = (type, url) => { opened++; assert.equal(type, 'image'); assert.equal(url, '/Media/Preview/42'); };
    viewer.bindLoadEvent();
    assert.equal(opened, 1);
    loaded({ currentTarget: image });
    assert.equal(opened, 1);
});

for (const [width, height, viewportWidth, viewportHeight] of [
    [1920, 1280, 1200, 800], [1280, 1920, 1200, 800], [1920, 100, 1200, 800], [1280, 1920, 400, 600]
]) {
    test(`preview ${width}x${height} fits ${viewportWidth}x${viewportHeight} without timers or fading`, () => {
        let imageWidth = 1, containerWidth = 11;
        const styles = {};
        const image = { naturalWidth: width, naturalHeight: height };
        const media = { 0: image, width(value) { imageWidth = value; }, outerHeight: () => imageWidth * height / width };
        const frame = { outerWidth: () => containerWidth, width: () => containerWidth - 10 };
        const rows = { css() {} };
        const container = {
            find: selector => selector === '.media-viewer' ? frame : selector === '.media-viewer-image' ? media : rows,
            width(value) { containerWidth = value; }, outerWidth: () => containerWidth,
            outerHeight: () => media.outerHeight() + 60,
            css(value) { Object.assign(styles, value); }
        };
        const popup = { find: () => container, innerWidth: () => viewportWidth, innerHeight: () => viewportHeight,
            css(name, value) { styles[name] = value; } };
        const context = vm.createContext({ window: { innerWidth: viewportWidth },
            setTimeout() { assert.fail('Image layout must not wait for a timer'); } });
        vm.runInContext(source, context);
        const viewer = new context.MediaViewer();
        viewer.resizeMediaViewer(1, popup, 'image', '/Media/Preview/42');
        assert.equal(styles.opacity, 1);
        assert.ok(containerWidth <= viewportWidth - 40);
        assert.ok(container.outerHeight() <= viewportHeight - 40);
        assert.ok(parseFloat(styles.top) >= 20 && parseFloat(styles.left) >= 20);
        assert.ok(imageWidth > 0);
    });
}
