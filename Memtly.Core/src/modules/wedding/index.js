export default function initWedding() {
    const dialog = document.getElementById('wedding-share-dialog');
    if (!dialog) return;
    document.querySelectorAll('[data-wedding-share]').forEach(button => {
        button.addEventListener('click', () => dialog.showModal());
    });
    dialog.querySelectorAll('[data-close-dialog]').forEach(button => {
        button.addEventListener('click', () => dialog.close());
    });
}
