/**
 * B站视频点赞工具 — 客户端辅助模块
 *
 * 由于浏览器跨站安全限制，本页无法自动操作 bilibili.com 的点赞按钮。
 * 点击后仅负责打开对应视频页面，点赞需要用户在 B 站页面中手动完成。
 */
window.bilibili = (function () {
    'use strict';

    function likeVideo(bvid) {
        return openVideoPopup(bvid);
    }

    function openVideoPopup(bvid) {
        var url = 'https://www.bilibili.com/video/' + bvid;

        var w = 960, h = 700;
        var left = Math.max(0, Math.round((screen.width - w) / 2));
        var top  = Math.max(0, Math.round((screen.height - h) / 2));
        var features = 'width=' + w + ',height=' + h
            + ',left=' + left + ',top=' + top
            + ',scrollbars=yes,resizable=yes,menubar=no,toolbar=no,location=yes';

        var popup = window.open(url, 'bili_like_' + bvid, features);
        if (popup) {
            popup.focus();
            return {
                status: 'popup',
                message: '已打开视频页，请在新窗口中手动点击点赞按钮。',
                url: url
            };
        }

        return {
            status: 'popup_blocked',
            message: '弹窗被浏览器拦截，请点击下方链接打开视频后手动点赞。',
            url: url
        };
    }

    /** 在新标签页中打开视频链接 */
    function openVideoTab(bvid) {
        window.open('https://www.bilibili.com/video/' + bvid, '_blank');
    }

    return {
        likeVideo: likeVideo,
        openVideoPopup: openVideoPopup,
        openVideoTab: openVideoTab
    };
})();
