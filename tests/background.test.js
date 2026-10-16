import test from "node:test";
import assert from "node:assert/strict";
import { setTimeout as delay } from "node:timers/promises";

class Event {
  handlers = [];
  addListener(handler) { this.handlers.push(handler); }
  emit(...args) { return this.handlers.map(handler => handler(...args)); }
}
const messages = [];
const data = {};
const writes = [];
const urls = [];
const native = { onMessage: new Event(), onDisconnect: new Event(), postMessage(message) {
  messages.push(message);
  if (message.type === "nativeCall") queueMicrotask(() => native.onMessage.emit({
    type: "nativeResult", id: message.id, ok: true,
    result: message.action === "auth.token" ? { accessToken: "fake" } : { connected: true, configured: true }
  }));
} };
const onMessage = new Event();
const base = "chrome-extension://abcdefghijklmnopabcdefghijklmnop/";
globalThis.chrome = {
  runtime: { id: "abcdefghijklmnopabcdefghijklmnop", getURL: path => base + path,
    connectNative: () => native, onMessage, onStartup: new Event(), onInstalled: new Event() },
  action: { onClicked: new Event(), setBadgeText: async () => {} },
  alarms: { onAlarm: new Event(), create: async () => {} },
  tabs: { create: async item => { urls.push(item.url); } },
  storage: { session: {
    setAccessLevel: async () => {},
    set: async values => Object.assign(data, structuredClone(values)),
    remove: async key => { delete data[key]; }
  } }
};
globalThis.fetch = async (url, options) => {
  writes.push({ url, ...options });
  return new Response(JSON.stringify({ id: "sent" }));
};
await import("../extension/background.js");

const sender = { id: chrome.runtime.id, url: base + "ui.html" };
function ui(message, from = sender) {
  return new Promise(resolve => {
    const accepted = onMessage.emit(message, from, resolve);
    if (!accepted.includes(true)) resolve(undefined);
  });
}
async function until(predicate) {
  for (let attempt = 0; attempt < 100; attempt++) {
    if (predicate()) return;
    await delay(5);
  }
  throw new Error("Condition was not reached.");
}
function request(id, action = "mail.send") {
  native.onMessage.emit({ type: "invoke", id, action,
    args: { to: ["recipient@example.com"], subject: "Subject", body: "Body" } });
}

test("external writes wait for one-time approval and duplicate approvals cannot send again", async () => {
  request("approval-one");
  await until(() => data["approval:approval-one"]);
  assert.equal(writes.length, 0);
  assert.match(urls[0], /approval=approval-one/);
  assert.equal((await ui({ type: "decide", id: "approval-one", approved: true })).ok, true);
  await until(() => messages.some(message => message.type === "result" && message.id === "approval-one"));
  assert.equal(writes.length, 1);
  assert.equal((await ui({ type: "decide", id: "approval-one", approved: true })).ok, false);
  assert.equal(writes.length, 1);
});

test("denied requests never call Google and prevent account switching while pending", async () => {
  request("approval-two");
  await until(() => data["approval:approval-two"]);
  assert.equal((await ui({ type: "disconnect" })).ok, false);
  assert.equal((await ui({ type: "connect" })).ok, false);
  await ui({ type: "decide", id: "approval-two", approved: false });
  await until(() => messages.some(message => message.id === "approval-two" && message.ok === false));
  assert.equal(writes.length, 1);
  assert.equal(data["approval:approval-two"], undefined);
});

test("untrusted senders cannot approve and unknown actions return an explicit error", async () => {
  assert.equal(await ui({ type: "connect" }, { id: "other", url: "https://mail.google.com/" }), undefined);
  request("invalid", "auth.token");
  await until(() => messages.some(message => message.id === "invalid" && message.ok === false));
  assert.equal(writes.length, 1);
});

test("expired approval cannot execute even before the expiry timer callback runs", async () => {
  request("expiry");
  await until(() => data["approval:expiry"]);
  const original = Date.now;
  Date.now = () => data["approval:expiry"].expiresAt + 1;
  try { await ui({ type: "decide", id: "expiry", approved: true }); }
  finally { Date.now = original; }
  await until(() => messages.some(message => message.id === "expiry" && message.ok === false));
  assert.equal(writes.length, 1);
});

test("already expired external requests never open approval or call Google", async () => {
  const before = urls.length;
  native.onMessage.emit({ type: "invoke", id: "expired-request", action: "mail.send", deadline: Date.now() - 1 });
  await until(() => messages.some(message => message.id === "expired-request" && message.ok === false));
  assert.equal(urls.length, before);
  assert.equal(writes.length, 1);
});

test("native disconnect cancels approvals without a Google write", async () => {
  request("disconnect");
  await until(() => data["approval:disconnect"]);
  native.onDisconnect.emit();
  await until(() => data["approval:disconnect"] === undefined);
  assert.equal((await ui({ type: "decide", id: "disconnect", approved: true })).ok, false);
  assert.equal(writes.length, 1);
});
