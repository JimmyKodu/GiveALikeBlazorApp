/**
 * B站视频点赞工具 JavaScript 模块
 *
 * 点赞流程：
 * 1. 尝试从 document.cookie 中读取 bili_jct（CSRF token）
 *    ─ 若 App 部署在 bilibili.com 子域下，可直接读取并调用官方 API
 * 2. 若读取失败（跨域限制），以弹窗方式打开 B站视频页面
 *    ─ 弹窗继承浏览器的 bilibili.com 登录态，用户可在弹窗内点赞
 */
window.bilibili = (function () {
    'use strict';

    /** 从 document.cookie 读取指定名称的 cookie 值 */
    function getCookie(name) {
        var match = document.cookie.match(new RegExp('(?:^|; )' + name + '=([^;]*)'));
        return match ? decodeURIComponent(match[1]) : null;
    }

    /**
     * 调用官方 Web API 给视频点赞
     * @param {string} bvid  BV 号（已包含 "BV" 前缀）
     * @param {string} csrf  bili_jct cookie 值
     * @returns {Promise<{code:number, message:string}>}
     */
    async function callLikeApi(bvid, csrf) {
        var body = 'bvid=' + encodeURIComponent(bvid)
            + '&like=1'
            + '&csrf=' + encodeURIComponent(csrf);

        var resp = await fetch('https://api.bilibili.com/x/web-interface/archive/like', {
            method: 'POST',
            credentials: 'include',
            headers: {
                'Content-Type': 'application/x-www-form-urlencoded',
                'Referer': 'https://www.bilibili.com/video/' + bvid
            },
            body: body
        });
        return resp.json();
    }

    /**
     * 主入口：尝试点赞，若无法自动点赞则打开弹窗
     * @param {string} bvid  BV 号（已包含 "BV" 前缀）
     * @returns {Promise<{status: 'liked'|'popup'|'popup_blocked'|'error', message: string}>}
     */
    async function likeVideo(bvid) {
        // ── 1. 尝试读取 CSRF token ──────────────────────────────────────────
        var csrf = getCookie('bili_jct');

        if (csrf) {
            try {
                var data = await callLikeApi(bvid, csrf);
                if (data.code === 0) {
                    return { status: 'liked', message: '点赞成功！' };
                }
                if (data.code === 65006) {
                    return { status: 'liked', message: '您已经点赞过该视频了' };
                }
                // 其他错误码：继续走弹窗逻辑
            } catch (_) {
                // 网络错误 / CORS 拦截 → 走弹窗逻辑
            }
        }

        // ── 2. 弹窗方式打开视频 ─────────────────────────────────────────────
        return openVideoPopup(bvid);
    }

    /**
     * 在弹窗中打开视频页面（浏览器已登录 B站 时弹窗内为已登录状态）
     */
    function openVideoPopup(bvid) {
        var url = 'https://www.bilibili.com/video/' + bvid;

        // 计算居中位置
        var w = 960, h = 620;
        var left = Math.max(0, Math.round((screen.width - w) / 2));
        var top = Math.max(0, Math.round((screen.height - h) / 2));
        var features = 'width=' + w + ',height=' + h
            + ',left=' + left + ',top=' + top
            + ',scrollbars=yes,resizable=yes,menubar=no,toolbar=no,location=yes';

        var popup = window.open(url, 'bili_video_' + bvid, features);

        if (popup) {
            popup.focus();
            return {
                status: 'popup',
                message: '视频已在新窗口打开，请在新窗口中点击点赞按钮',
                url: url
            };
        }

        // 弹窗被浏览器阻止
        return {
            status: 'popup_blocked',
            message: '弹窗被浏览器拦截，请点击下方链接在新标签页打开视频后手动点赞',
            url: url
        };
    }

    /** 直接在新标签页打开视频链接 */
    function openVideoTab(bvid) {
        window.open('https://www.bilibili.com/video/' + bvid, '_blank');
    }

    return {
        likeVideo: likeVideo,
        openVideoTab: openVideoTab
    };
})();
