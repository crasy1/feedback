// 管理端的一点点浏览器互操作。
//
// 目前只有一件事：把文本写进剪贴板。
// navigator.clipboard 只在安全上下文（https 或 localhost）可用，内网 http 下会不可用，
// 所以这里明确返回 true/false，让 Blazor 侧给出"请手动复制"的回退提示，而不是静默失败。
window.adminInterop = {
    copyText: async function (text) {
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
                return true;
            }
        } catch {
            // 权限被拒或 API 异常：落到下面的 false，由调用方提示手动复制。
        }
        return false;
    }
};
