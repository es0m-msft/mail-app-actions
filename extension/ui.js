const $ = id => document.getElementById(id);
const approvalId = new URL(location.href).searchParams.get("approval");

async function send(message) {
  const response = await chrome.runtime.sendMessage(message);
  if (!response?.ok) throw new Error(response?.error?.message ?? "Extension did not respond.");
  return response.result;
}

async function run(operation) {
  $("error").textContent = "";
  try { return await operation(); }
  catch (error) { $("error").textContent = error.message; }
}

async function status() {
  const result = await send({ type: "status" });
  $("status").textContent = result.connected
    ? "Companion connected. Saved Google authorization is present."
    : `Companion connected. ${result.configured ? "Connect your Google account." : "Import a Desktop OAuth client first (see README)."}`;
}

$("connect").onclick = () => run(async () => {
  $("connect").disabled = true;
  try {
    $("status").textContent = "Complete Google consent in the browser...";
    await send({ type: "connect" });
    await status();
  } finally { $("connect").disabled = false; }
});
$("disconnect").onclick = () => run(async () => {
  await send({ type: "disconnect" });
  await status();
});
for (const [id, action] of [["contacts", "contacts.list"], ["meetings", "meetings.list"]]) {
  $(id).onclick = () => run(async () => {
    $(id).disabled = true;
    try { $("result").textContent = JSON.stringify(await send({ type: "read", action }), null, 2); }
    finally { $(id).disabled = false; }
  });
}

async function showApproval() {
  if (!approvalId) return;
  const key = `approval:${approvalId}`;
  const pending = (await chrome.storage.session.get(key))[key];
  $("approval").hidden = false;
  if (!pending || pending.expiresAt <= Date.now()) {
    $("preview").textContent = "This request expired or was already handled. No action can be approved here.";
    $("approve").disabled = $("reject").disabled = true;
    return;
  }
  const { action, args, event } = pending;
  $("preview").textContent = action === "mail.send"
    ? `SEND EMAIL\nTo: ${args.to.join(", ")}\nCc: ${args.cc.join(", ")}\nBcc: ${args.bcc.join(", ")}\nSubject: ${args.subject}\n\n${args.body}`
    : `MOVE ONE MEETING / OCCURRENCE\n${event.title}\nEvent ID: ${args.eventId}\nOrganizer: ${event.organizer?.email}\nAttendees: ${event.attendees.map(person => person.email).join(", ")}\n\nCurrent: ${JSON.stringify(event.start)} - ${JSON.stringify(event.end)}\nNew: ${args.start} - ${args.end}\n\nAll attendees will be notified. All-day end dates are exclusive.`;
  $("expiry").textContent = `Expires at ${new Date(pending.expiresAt).toLocaleTimeString()}.`;
  setTimeout(() => void showApproval(), Math.max(0, pending.expiresAt - Date.now() + 10));
}
for (const [id, approved] of [["approve", true], ["reject", false]]) {
  $(id).onclick = () => run(async () => {
    $("approve").disabled = $("reject").disabled = true;
    await send({ type: "decide", id: approvalId, approved });
    $("preview").textContent = approved
      ? "Approved once. The result is returned to the calling app; approval alone does not guarantee success."
      : "Declined. No action was taken.";
  });
}
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "session" && approvalId && Object.hasOwn(changes, `approval:${approvalId}`)) void showApproval();
});
void run(status);
void run(showApproval);
