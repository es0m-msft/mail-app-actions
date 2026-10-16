import { GoogleActions, validateRequest } from "./google.js";

const HOST = "com.edge_google_actions.bridge";
let port;
let activeInvocations = 0;
const nativeCalls = new Map();
const approvals = new Map();
const actions = new GoogleActions(async () => (await nativeCall("auth.token")).accessToken);

function failure(error) {
  return { ok: false, error: { message: error instanceof Error ? error.message : String(error) } };
}

function connect() {
  if (port) return;
  port = chrome.runtime.connectNative(HOST);
  const current = port;
  current.onMessage.addListener(message => {
    if (message.type === "invoke") {
      void invoke(message).then(result => {
        if (port === current) {
          let response = { type: "result", id: message.id, ...result };
          if (new TextEncoder().encode(JSON.stringify(response)).length > 900000) {
            response = { type: "result", id: message.id, ...failure(new Error("Result exceeds the 900 KB bridge limit. No partial result was returned.")) };
          }
          current.postMessage(response);
        }
      });
    } else if (message.type === "nativeResult") {
      const call = nativeCalls.get(message.id);
      if (!call) return;
      nativeCalls.delete(message.id);
      clearTimeout(call.timer);
      if (message.ok) call.resolve(message.result);
      else call.reject(new Error(message.error?.message ?? "Native host request failed."));
    }
  });
  current.onDisconnect.addListener(() => {
    const error = chrome.runtime.lastError?.message ?? "Native host disconnected.";
    if (port === current) port = undefined;
    for (const call of nativeCalls.values()) {
      clearTimeout(call.timer);
      call.reject(new Error(error));
    }
    nativeCalls.clear();
    for (const approval of approvals.values()) {
      clearTimeout(approval.timer);
      approval.reject(new Error("Bridge disconnected. No automatic retry was attempted."));
    }
    approvals.clear();
    void chrome.storage.session.set({ bridgeError: error });
  });
}

function nativeCall(action) {
  connect();
  return new Promise((resolve, reject) => {
    const id = crypto.randomUUID();
    const timer = setTimeout(() => {
      nativeCalls.delete(id);
      reject(new Error("Native request timed out. Check the companion installation."));
    }, action === "auth.connect" ? 180000 : 35000);
    nativeCalls.set(id, { resolve, reject, timer });
    port.postMessage({ type: "nativeCall", id, action });
  });
}

async function invoke(message) {
  activeInvocations++;
  try {
    const deadline = message.deadline ?? Date.now() + 150000;
    if (Date.now() >= deadline) throw new Error("Request expired before processing.");
    const request = validateRequest(message);
    let prepared = await actions.prepare(request);
    if (request.action === "mail.send" || request.action === "meetings.move") {
      if (approvals.size >= 5) throw new Error("Too many pending approvals. Resolve existing requests first.");
      prepared = await approve(message.id, prepared, deadline);
    }
    if (Date.now() >= deadline) throw new Error("Request expired. No write was attempted.");
    const result = await actions.execute(prepared);
    return { ok: true, result };
  } catch (error) {
    return failure(error);
  } finally {
    activeInvocations--;
  }
}

async function approve(id, prepared, deadline) {
  const key = `approval:${id}`;
  const duration = Math.min(120000, deadline - Date.now() - 30000);
  if (duration <= 0) throw new Error("Insufficient time remains for approval. Request the action again.");
  const expiresAt = Date.now() + duration;
  const decision = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("Approval expired. No action was taken.")), duration);
    approvals.set(id, { prepared, resolve, reject, timer, expiresAt });
  });
  // Attach a handler before any asynchronous setup can reject the decision.
  decision.catch(() => {});
  try {
    await chrome.storage.session.set({ [key]: { ...prepared, expiresAt } });
    await chrome.tabs.create({ url: chrome.runtime.getURL(`ui.html?approval=${encodeURIComponent(id)}`) });
    await chrome.action.setBadgeText({ text: String(approvals.size) });
    return await decision;
  } finally {
    clearTimeout(approvals.get(id)?.timer);
    approvals.delete(id);
    await chrome.storage.session.remove(key);
    await chrome.action.setBadgeText({ text: approvals.size ? String(approvals.size) : "" });
  }
}

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (sender.id !== chrome.runtime.id || !sender.url?.startsWith(chrome.runtime.getURL("ui.html"))) return;
  const handle = async () => {
    switch (message.type) {
      case "status": return nativeCall("auth.status");
      case "connect":
        if (activeInvocations) throw new Error("Wait for external requests to finish before connecting an account.");
        return nativeCall("auth.connect");
      case "disconnect":
        if (activeInvocations) throw new Error("Wait for external requests to finish before disconnecting the account.");
        return nativeCall("auth.disconnect");
      case "decide": {
        const approval = approvals.get(message.id);
        if (!approval) throw new Error("Approval expired or was already handled.");
        approvals.delete(message.id);
        clearTimeout(approval.timer);
        if (approval.expiresAt <= Date.now()) approval.reject(new Error("Approval expired. No action was taken."));
        else if (message.approved === true) approval.resolve(approval.prepared);
        else approval.reject(new Error("The user declined the action."));
        return { accepted: true };
      }
      case "read":
        if (!["contacts.list", "meetings.list"].includes(message.action)) throw new Error("Unsupported read action.");
        return actions.execute({ action: message.action });
      default: throw new Error("Unknown extension message.");
    }
  };
  handle().then(result => respond({ ok: true, result }), error => respond(failure(error)));
  return true;
});

chrome.action.onClicked.addListener(() => chrome.tabs.create({ url: chrome.runtime.getURL("ui.html") }));
chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === "bridge-reconnect") connect();
});
chrome.runtime.onStartup.addListener(() => void start());
chrome.runtime.onInstalled.addListener(() => void start());
async function start() {
  await chrome.storage.session.setAccessLevel({ accessLevel: "TRUSTED_CONTEXTS" });
  await chrome.alarms.create("bridge-reconnect", { periodInMinutes: 1 });
  connect();
}
void start();
