const gridSelector = '.image-container-masonry';
const cardSelector = ':scope > .media-viewer-card';

/** Convert measured border-box heights to grid tracks, including the gutter. */
export function masonryRowSpan(height, gap, trackSize = 1) {
    return Math.max(1, Math.ceil((height + gap) / trackSize));
}

// Browsers may clamp very large implicit grids. Retain fine tracks for ordinary
// pages, then increase the unit for long scroll-loaded groups (including mobile).
export function masonryTrackSize(heights, gap) {
    return Math.max(1, Math.ceil(heights.reduce((sum, height) => sum + height + gap, 0) / 8000));
}

/** Owns only layout: existing cards, DOM order and all click handlers stay intact. */
export default class GalleryMasonry {
    constructor(root) {
        this.root = root;
        this.grids = new Map();
        this.observed = new Set();
        this.widths = new WeakMap();
        this.frame = null;
        this.destroyed = false;
        this.schedule = this.schedule.bind(this);
        this.onImage = event => {
            if (event.target.tagName === 'IMG' && event.target.closest(gridSelector)) this.schedule();
        };

        this.resizeObserver = new ResizeObserver(entries => {
            for (const entry of entries) {
                // Grid height changes are the output of our layout, not an input.
                if (entry.target.matches(gridSelector)) {
                    if (this.widths.get(entry.target) === entry.contentRect.width) continue;
                    this.widths.set(entry.target, entry.contentRect.width);
                }
                this.schedule();
            }
        });
        this.mutationObserver = new MutationObserver(this.schedule);
        this.mutationObserver.observe(root, { childList: true, subtree: true });
        root.addEventListener('load', this.onImage, true);
        root.addEventListener('error', this.onImage, true);
        this.schedule();
    }

    schedule() {
        if (this.destroyed || this.frame !== null) return;
        this.frame = requestAnimationFrame(() => {
            this.frame = null;
            this.layout();
        });
    }

    layout() {
        const nextGrids = new Map();
        const nextObserved = new Set();
        const measurements = [];

        // Read all sizes before changing grid spans, to avoid per-card reflows.
        for (const grid of this.root.querySelectorAll(gridSelector)) {
            const cards = Array.from(grid.querySelectorAll(cardSelector));
            nextGrids.set(grid, cards);
            nextObserved.add(grid);
            for (const card of cards) nextObserved.add(card);

            if (grid.getBoundingClientRect().width <= 0) continue;
            const gap = parseFloat(getComputedStyle(grid).getPropertyValue('--masonry-gap')) || 0;
            const heights = cards.map(card => card.getBoundingClientRect().height);
            const trackSize = masonryTrackSize(heights, gap);
            measurements.push({ grid, trackSize, spans: cards.map((card, index) => ({
                card, span: masonryRowSpan(heights[index], gap, trackSize)
            })) });
        }

        for (const element of this.observed) {
            if (!nextObserved.has(element)) this.resizeObserver.unobserve(element);
        }
        for (const element of nextObserved) {
            if (!this.observed.has(element)) this.resizeObserver.observe(element);
        }
        for (const [grid, cards] of this.grids) {
            if (!nextGrids.has(grid)) this.reset(grid, cards);
            else {
                for (const card of cards) {
                    if (!nextObserved.has(card)) card.style.removeProperty('grid-row-end');
                }
            }
        }
        this.grids = nextGrids;
        this.observed = nextObserved;

        for (const { grid, trackSize, spans } of measurements) {
            grid.style.setProperty('--masonry-track-size', `${trackSize}px`);
            for (const { card, span } of spans) {
                const value = `span ${span}`;
                if (card.style.gridRowEnd !== value) card.style.gridRowEnd = value;
            }
            if (!grid.hasAttribute('data-masonry-ready')) grid.setAttribute('data-masonry-ready', '');
        }
    }

    reset(grid, cards) {
        grid.removeAttribute('data-masonry-ready');
        grid.style.removeProperty('--masonry-track-size');
        for (const card of cards) card.style.removeProperty('grid-row-end');
    }

    destroy() {
        this.destroyed = true;
        if (this.frame !== null) cancelAnimationFrame(this.frame);
        this.frame = null;
        this.resizeObserver.disconnect();
        this.mutationObserver.disconnect();
        this.root.removeEventListener('load', this.onImage, true);
        this.root.removeEventListener('error', this.onImage, true);
        for (const [grid, cards] of this.grids) this.reset(grid, cards);
        this.grids.clear();
        this.observed.clear();
    }
}
