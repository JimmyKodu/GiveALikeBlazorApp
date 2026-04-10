/**
 * B站视频点赞工具 — 客户端辅助模块
 *
 * 主要功能由服务端完成（读取浏览器 Cookie → 调用 B 站 API）。
 * 此脚本仅提供弹窗/新标签页打开视频的 fallback 功能。
 */
window.bilibili = (function () {
    'use strict';

    /**
     * 在弹窗中打开视频页面（浏览器已登录 B 站时弹窗内为已登录状态）。
     * 作为服务端点赞失败时的手动 fallback。
     */
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
        }
    }

    /** 在新标签页中打开视频链接 */
    function openVideoTab(bvid) {
        window.open('https://www.bilibili.com/video/' + bvid, '_blank');
    }

    return {
        openVideoPopup: openVideoPopup,
        openVideoTab: openVideoTab
    };
})();
