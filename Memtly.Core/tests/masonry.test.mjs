import { test, beforeEach, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import GalleryMasonry, { masonryRowSpan, masonryTrackSize } from '../src/modules/gallery-masonry/masonry.mjs';

class Element {
    constructor(height = 0, width = 300, grid = false) {
        Object.assign(this, { height, width, grid, cards: [], attributes: new Map(), listeners: new Map() });
        this.style = {
            gridRowEnd: '', properties: new Map(),
            setProperty: (key, value) => this.style.properties.set(key, value),
            removeProperty: key => {
                if (key === 'grid-row-end') this.style.gridRowEnd = '';
                else this.style.properties.delete(key);
            }
        };
    }
    matches() { return this.grid; }
    querySelectorAll() { return this.cards; }
    getBoundingClientRect() { return { width: this.width, height: this.height }; }
    hasAttribute(name) { return this.attributes.has(name); }
    setAttribute(name, value) { this.attributes.set(name, value); }
    removeAttribute(name) { this.attributes.delete(name); }
    addEventListener(name, listener) { this.listeners.set(name, listener); }
    removeEventListener(name, listener) {
        if (this.listeners.get(name) === listener) this.listeners.delete(name);
    }
}

let frames, nextFrame, observers, originals, layouts;
beforeEach(() => {
    frames = new Map(); nextFrame = 0; observers = []; layouts = [];
    originals = new Map(['requestAnimationFrame', 'cancelAnimationFrame', 'ResizeObserver',
        'MutationObserver', 'getComputedStyle'].map(key => [key, globalThis[key]]));
    globalThis.requestAnimationFrame = callback => { frames.set(++nextFrame, callback); return nextFrame; };
    globalThis.cancelAnimationFrame = id => frames.delete(id);
    globalThis.getComputedStyle = () => ({ getPropertyValue: () => '8px' });
    class Observer {
        constructor(callback) { this.callback = callback; this.elements = new Set(); observers.push(this); }
        observe(element) { this.elements.add(element); }
        unobserve(element) { this.elements.delete(element); }
        disconnect() { this.elements.clear(); this.disconnected = true; }
    }
    globalThis.ResizeObserver = Observer;
    globalThis.MutationObserver = Observer;
});
afterEach(() => {
    for (const layout of layouts) layout.destroy();
    for (const [key, value] of originals) {
        if (value === undefined) delete globalThis[key]; else globalThis[key] = value;
    }
});

function setup(heights = [450, 200, 300]) {
    const root = new Element();
    const grid = new Element(450, 900, true);
    grid.cards = heights.map(height => new Element(height));
    root.cards = [grid];
    const layout = new GalleryMasonry(root);
    layouts.push(layout);
    return { root, grid, layout, resize: observers[0], mutation: observers[1] };
}
function flush() {
    const callbacks = [...frames.values()]; frames.clear();
    for (const callback of callbacks) callback();
}
function resize(observer, element, width = element.width) {
    observer.callback([{ target: element, contentRect: { width } }]);
}

test('fractional image heights round up without cropping and retain the gutter', () => {
    assert.equal(masonryRowSpan(200.25, 8), 209);
    assert.equal(masonryRowSpan(450, 8), 458);
    assert.equal(masonryRowSpan(0, 0), 1);
});

test('long scroll-loaded groups use bounded track counts without cutting off image height', () => {
    const heights = Array(200).fill(625.25);
    const trackSize = masonryTrackSize(heights, 8);
    const span = masonryRowSpan(heights[0], 8, trackSize);
    assert.ok(trackSize > 1);
    assert.ok(span * heights.length < 10000);
    assert.ok(span * trackSize >= heights[0] + 8);
    assert.ok(span * trackSize < heights[0] + 8 + trackSize);
});

test('initial measurement preserves mixed aspect ratios and original card identities/order', () => {
    const { grid } = setup();
    const originalCards = [...grid.cards];
    assert.equal(grid.hasAttribute('data-masonry-ready'), false);
    flush();
    assert.deepEqual(grid.cards, originalCards);
    assert.deepEqual(grid.cards.map(card => card.style.gridRowEnd), ['span 458', 'span 208', 'span 308']);
    assert.equal(grid.hasAttribute('data-masonry-ready'), true);
});

test('late loads and error fallbacks coalesce into one frame and remeasure', () => {
    const { root, grid, layout } = setup(); flush();
    grid.cards[0].height = 175.5;
    const event = { target: { tagName: 'IMG', closest: () => grid } };
    root.listeners.get('load')(event);
    root.listeners.get('error')(event);
    layout.schedule();
    assert.equal(frames.size, 1);
    flush();
    assert.equal(grid.cards[0].style.gridRowEnd, 'span 184');
});

test('container width and image resizes recalculate while height-only grid feedback is ignored', () => {
    const { grid, resize: observer } = setup(); flush();
    resize(observer, grid); flush();
    resize(observer, grid);
    assert.equal(frames.size, 0);
    grid.width = 600; grid.cards[0].height = 300;
    resize(observer, grid); flush();
    assert.equal(grid.cards[0].style.gridRowEnd, 'span 308');
    grid.cards[0].height = 220;
    resize(observer, grid.cards[0]); flush();
    assert.equal(grid.cards[0].style.gridRowEnd, 'span 228');
});

test('pagination can append both cards and new groups without moving existing cards', () => {
    const { root, grid, mutation, resize: observer } = setup(); flush();
    const first = grid.cards[0];
    const appended = new Element(333);
    grid.cards.push(appended);
    const newGroup = new Element(100, 900, true);
    newGroup.cards = [new Element(100)]; root.cards.push(newGroup);
    mutation.callback([]); flush();
    assert.equal(grid.cards[0], first);
    assert.equal(grid.cards.at(-1), appended);
    assert.equal(appended.style.gridRowEnd, 'span 341');
    assert.equal(newGroup.cards[0].style.gridRowEnd, 'span 108');
    assert.ok(observer.elements.has(appended));
    assert.ok(observer.elements.has(newGroup));
});

test('replacing partial HTML unobserves and resets removed cards and grids', () => {
    const { root, grid, mutation, resize: observer } = setup(); flush();
    const removed = grid.cards.shift();
    mutation.callback([]); flush();
    assert.equal(observer.elements.has(removed), false);
    assert.equal(removed.style.gridRowEnd, '');
    root.cards = []; mutation.callback([]); flush();
    assert.equal(observer.elements.size, 0);
    assert.equal(grid.hasAttribute('data-masonry-ready'), false);
    assert.equal(grid.cards[0].style.gridRowEnd, '');
});

test('hidden pending/approved sections are measured when they become visible', () => {
    const { grid, resize: observer } = setup(); grid.width = 0; flush();
    assert.equal(grid.hasAttribute('data-masonry-ready'), false);
    grid.width = 600; resize(observer, grid); flush();
    assert.equal(grid.hasAttribute('data-masonry-ready'), true);
});

test('destroy cancels work, disconnects observers and removes image listeners and inline layout', () => {
    const { root, grid, layout, resize: observer, mutation } = setup(); flush();
    layout.schedule(); layout.destroy();
    assert.equal(frames.size, 0);
    assert.equal(root.listeners.size, 0);
    assert.ok(observer.disconnected && mutation.disconnected);
    assert.equal(grid.hasAttribute('data-masonry-ready'), false);
    assert.ok(grid.cards.every(card => card.style.gridRowEnd === ''));
    layout.schedule(); assert.equal(frames.size, 0);
});
