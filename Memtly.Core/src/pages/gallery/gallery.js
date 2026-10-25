import { downloadArchive } from '@modules/download-confirm';
import { displayMessage } from '@modules/message-box';
import { displayPopup } from '@modules/popups';
import { displayLoader, hideLoader } from '@modules/loader';
import { default as galleryUpload } from '@modules/upload-box';
import MediaViewer from '@modules/media-viewer';
import Slideshow from '@modules/slideshow';
import { default as initSettings } from '@pages/account/partials/settings';
import { bindCollectionSettingsButton, bindGallerySettingsButton } from '@pages/account/partials/gallery'
import { getQueryParam } from '@utilities/urls';
import GalleryMasonry from '@modules/gallery-masonry';

let resizeTimeout = null;
let idleTimeout = null;
let searchTimeout = null;

let mediaViewer = null;
let slideshow = null;
let masonry = null;

let isPageLoading = false;

function init() {
    const slideshowSlideInterval = $('input#slideshowSlideInterval').val();
    const slideshowFadeInterval = $('input#slideshowFadeInterval').val();

    galleryUpload.init();

    slideshow = new Slideshow('#gallery-slideshow', slideshowSlideInterval, slideshowFadeInterval);
    slideshow.init();

    mediaViewer = new MediaViewer();
    mediaViewer.init();

    startMasonry();
    window.removeEventListener('pagehide', stopMasonry);
    window.removeEventListener('pageshow', startMasonry);
    window.addEventListener('pagehide', stopMasonry);
    window.addEventListener('pageshow', startMasonry);

    initSettings();
    bindEventHandlers();
}

function startMasonry() {
    stopMasonry();
    const root = document.getElementById('main-gallery');
    if (root) masonry = new GalleryMasonry(root);
}

function stopMasonry() {
    masonry?.destroy();
    masonry = null;
}

function bindEventHandlers() {
    bindSearchBox();
    bindShareButton();
    bindQRCodeSave();
    bindDownloadGroup();
    bindDownloadGallery();
    bindDeletePhoto();
    bindIdleRefresh();
    bindPageResizeEvent();
    bindCollectionSettingsButton();
    bindGallerySettingsButton();
}

function bindSearchBox() {
    $(document).off('keyup', 'input#gallery-item-search-term').on('keyup', 'input#gallery-item-search-term', function (e) {
        clearTimeout(searchTimeout);
        searchTimeout = setTimeout(() => {
            const term = $('input#gallery-item-search-term').val();
            const url = new URL(window.location.href);
            url.searchParams.set('term', term);
            url.searchParams.set('page', '1');

            history.pushState({}, '', url);

            loadGalleryPage(1, false);
        }, 500);
    });
}

function bindPageResizeEvent() {
    $(window).on('resize', () => {
        clearTimeout(resizeTimeout);
        resizeTimeout = setTimeout(() => {
            slideshow.init();
        }, 200);
    });
}

function bindShareButton() {
    $(document).off('click', 'button.btnCopyShareLink').on('click', 'button.btnCopyShareLink', (e) => {
        preventDefaults(e);

        if ($(e.currentTarget).attr('disabled') === 'disabled') {
            return;
        }

        const link = $(e.currentTarget).data('share-link');
        navigator.clipboard.writeText(link)
            .then(() => displayMessage(
                localization.translate('Share'),
                localization.translate('Share_Link_Copied')
            ));
    });
}

function bindQRCodeSave() {
    $(document).off('click', 'button.btnSaveQRCode').on('click', 'button.btnSaveQRCode', (e) => {
        preventDefaults(e);

        if ($(e.currentTarget).attr('disabled') === 'disabled') {
            return;
        }

        const galleryName = $(e.currentTarget).data('gallery-name');
        const canvas = $('.qrcode-download canvas')[0];

        const link = document.createElement('a');
        link.download = `${galleryName}-qrcode.png`;
        link.href = canvas.toDataURL('image/png', 1.0).replace('image/png', 'image/octet-stream');
        link.click();
    });
}

function bindDownloadGroup() {
    $(document).off('click', '.btnDownloadGroup').on('click', '.btnDownloadGroup', (e) => {
        preventDefaults(e);
        downloadArchive({ Id: $(e.currentTarget).data('gallery-id'), Group: $(e.currentTarget).data('group-name') });
    });
}

function bindDownloadGallery() {
    $(document).off('click', 'button.btnDownloadGallery, button.btnDownloadSelected').on('click', 'button.btnDownloadGallery, button.btnDownloadSelected', (e) => {
        preventDefaults(e);
        const selected = $(e.currentTarget).hasClass('btnDownloadSelected');
        const ids = $('#main-gallery .btn-multi-select.fa-square-check').map(function () { return $(this).data('id'); }).get();
        if (selected && ids.length === 0) {
            displayMessage('Download', 'Bitte wählt zuerst mindestens ein Foto über die Checkbox aus.');
            return;
        }
        downloadArchive({ Id: $(e.currentTarget).data('gallery-id'), FileFilter: selected ? ids : [] });
    });
}

function bindDeletePhoto() {
    $(document).off('click', '.btnDeletePhoto').on('click', '.btnDeletePhoto', (e) => {
        preventDefaults(e);

        if ($(e.currentTarget).attr('disabled') === 'disabled') {
            return;
        }

        const id = $(e.currentTarget).data('photo-id');
        const name = $(e.currentTarget).data('photo-name');
        const tile = $(e.currentTarget).closest('.image-tile');

        displayPopup({
            Title: localization.translate('Delete_Item'),
            Message: localization.translate('Delete_Are_You_Sure'),
            Fields: [{
                Id: 'photo-id',
                Value: id,
                Type: 'hidden'
            }],
            Buttons: [
                {
                    Text: localization.translate('Delete'),
                    Class: 'btn-danger',
                    Callback: () => {
                        displayLoader(localization.translate('Loading'));

                        const photoId = $('#popup-modal-field-photo-id').val();
                        if (!photoId || photoId.length === 0) {
                            displayMessage(
                                localization.translate('Delete_Item'),
                                localization.translate('Delete_Item_Id_Missing')
                            );
                            return;
                        }

                        $.ajax({
                            url: '/Account/DeletePhoto',
                            method: 'DELETE',
                            data: { id: photoId }
                        })
                            .done((data) => {
                                if (data.success === true) {
                                    tile.remove();
                                    displayMessage(
                                        localization.translate('Delete_Item'),
                                        localization.translate('Delete_Item_Success'),
                                        null,
                                        () => refreshGalleryPage()
                                    );
                                } else if (data.message) {
                                    displayMessage(
                                        localization.translate('Delete_Item'),
                                        localization.translate('Delete_Item_Failed'),
                                        [data.message]
                                    );
                                } else {
                                    displayMessage(
                                        localization.translate('Delete_Item'),
                                        localization.translate('Delete_Item_Failed')
                                    );
                                }
                            })
                            .fail((xhr, error) => {
                                displayMessage(
                                    localization.translate('Delete_Item'),
                                    localization.translate('Delete_Item_Failed'),
                                    [error]
                                );
                            });
                    }
                },
                {
                    Text: localization.translate('Close')
                }
            ]
        });
    });
}

function bindIdleRefresh() {
    const duration = $('input#galleryIdleRefreshInterval').val();
    if (duration > 0) {
        $(document).on('mousemove keydown scroll click', () => {
            setIdleRefresh(duration);
        });
        setIdleRefresh(duration);
    }
}

function setIdleRefresh(duration) {
    clearTimeout(idleTimeout);
    idleTimeout = setTimeout(() => {
        refreshGalleryPage(bindIdleRefresh);
    }, duration);
}

export function loadGalleryPage(page, append, callback) {
    if (isPageLoading) {
        return;
    }

    isPageLoading = true;

    page = page !== undefined ? page : 1;
    append = append !== undefined ? append : false;

    const url = new URL(window.location.href);
    url.searchParams.delete('partial');
    url.searchParams.delete('pagination');
    if (append) {
        url.searchParams.delete('page');
    } else {
        url.searchParams.set('page', page);
    }

    $.ajax({
        type: 'GET',
        url: `${url.pathname}${url.search}&page=${page}&partial=true&pagination=${append}`,
        success: (data) => {
            data = data.trim();

            if (data !== undefined && data.length > 0) {
                history.pushState({}, '', url);

                if (append) {
                    $('.gallery-container-wrapper').append(data);

                    ['pending', 'approved'].forEach((type) => {
                        $(`.gallery-container-wrapper .gallery-container-${type}:gt(0)`).addClass('d-none');
                        $(`.gallery-container-wrapper .gallery-container-${type}`).each(function (galleryContainerIndex, galleryContainer) {
                            if (galleryContainerIndex > 0) {
                                $(galleryContainer).find('.image-group').each(function (imageGroupIndex, imageGroup) {
                                    const key = $(imageGroup).data('key');
                                    const originalGalleryContainer = $(`.gallery-container-wrapper .gallery-container-${type}:first`);
                                    const originalImageGroup = originalGalleryContainer.find(`.image-group-${key}`);
                                    if (originalImageGroup.length === 0) {
                                        $(imageGroup).appendTo(originalGalleryContainer);
                                    } else {
                                        const originalImageGroupContainer = originalImageGroup.find(`.image-container`);
                                        $(imageGroup).find('.image-tile').each(function (imageTileIndex, imageTile) {
                                            $(imageTile).appendTo(originalImageGroupContainer);
                                        });
                                    }
                                });
                            }
                        });

                        const itemCount = $(`.gallery-container-wrapper .gallery-container-${type}:first .image-tile`).length;
                        if (itemCount > 0) {
                            $(`.gallery-container-wrapper .gallery-container-${type}:first`).removeClass('d-none');
                        } else {
                            $(`.gallery-container-wrapper .gallery-container-${type}:first`).addClass('d-none');
                        }

                        $(`.gallery-container-wrapper .gallery-container-${type}:gt(0)`).remove();
                    });

                    $(`.gallery-container-wrapper .gallery-container .image-group`).each(function (index, el) {
                        if ($(el).find('.image-tile').length == 0) {
                            $(el).remove();
                        }
                    });
                } else {
                    $('#main-gallery').html(data);
                }

                mediaViewer.init();

                if (typeof callback === 'function') {
                    callback();
                }
            }
        },
        complete: () => {
            isPageLoading = false;
        }
    });
}

export function refreshGalleryPage(callback) {
    loadGalleryPage(1, false, callback);
}

export default init;
