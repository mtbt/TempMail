"use strict";
window.tempMail = (() => {
    let csrf, hub, timer, receiver;
    async function init() {
        const r = await fetch('/api/session', { credentials: 'same-origin', cache: 'no-store' });
        if (!r.ok) throw new Error('Không khởi tạo được phiên.');
        csrf = (await r.json()).requestToken;
    }
    async function api(path, method = 'GET', body = null) {
        const headers = { 'X-CSRF-TOKEN': csrf || '' };
        if (body !== null) headers['Content-Type'] = 'application/json';
        const r = await fetch(path, { method, headers, body: body === null ? undefined : JSON.stringify(body), credentials: 'same-origin', cache: 'no-store' });
        const text = await r.text();
        let data = null;
        if (text) { try { data = JSON.parse(text); } catch { data = { title: 'Máy chủ trả về phản hồi không hợp lệ.' }; } }
        return { ok: r.ok, status: r.status, data };
    }
    async function join() { if (hub?.state === signalR.HubConnectionState.Connected) { try { await hub.invoke('JoinMailbox'); } catch { /* No active mailbox is normal on the create screen. */ } } }
    async function watch(ref) {
        await stop(); receiver = ref;
        hub = new signalR.HubConnectionBuilder().withUrl('/mailhub').withAutomaticReconnect([0, 2000, 5000, 10000, 30000]).configureLogging(signalR.LogLevel.Error).build();
        const refresh = () => { receiver?.invokeMethodAsync('InboxChanged').catch(() => {}); };
        hub.on('InboxChanged', refresh);
        hub.onreconnected(async () => { await join(); refresh(); });
        try { await hub.start(); await join(); } catch { /* Timer retries initial connection below. */ }
        let ticks = 0;
        timer = setInterval(async () => {
            const current = hub;
            if (!current) return;
            receiver?.invokeMethodAsync('Tick', current.state === 'Connected' ? 'Cập nhật trực tiếp' : 'Đang kết nối lại…').catch(() => {});
            if (++ticks % 30 === 0) { refresh(); if (current.state === 'Disconnected') { try { await current.start(); await join(); } catch {} } }
        }, 1000);
    }
    async function stop() { clearInterval(timer); receiver = null; const previous = hub; hub = null; if (previous) await previous.stop(); }
    return { init, api, watch, join, stop, copy: text => navigator.clipboard.writeText(text) };
})();
