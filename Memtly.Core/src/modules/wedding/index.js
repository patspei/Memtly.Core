export default function initWedding() {
    const header = document.querySelector('.wedding-gallery .wedding-nav');
    if (header) {
        const updateHeight = () => document.body.style.setProperty('--wedding-header-height', `${header.getBoundingClientRect().height}px`);
        updateHeight();
        new ResizeObserver(updateHeight).observe(header);
    }
    const dialog = document.getElementById('wedding-share-dialog');
    if (!dialog) return;
    document.querySelectorAll('[data-wedding-share]').forEach(button => {
        button.addEventListener('click', () => dialog.showModal());
    });
    dialog.querySelectorAll('[data-close-dialog]').forEach(button => {
        button.addEventListener('click', () => dialog.close());
    });
}
