const SCOPES = [
  "https://www.googleapis.com/auth/gmail.send",
  "https://www.googleapis.com/auth/contacts.readonly",
  "https://www.googleapis.com/auth/calendar.events"
];
export { SCOPES };

export function validateRequest(request) {
  if (!request || typeof request !== "object" || Array.isArray(request)) {
    throw new Error("Expected an action request object.");
  }
  const { action, args = {} } = request;
  if (!args || typeof args !== "object" || Array.isArray(args)) {
    throw new Error("args must be an object.");
  }
  const allowed = {
    "contacts.list": [],
    "meetings.list": [],
    "mail.send": ["to", "cc", "bcc", "subject", "body"],
    "meetings.move": ["eventId", "start", "end"]
  };
  if (!Object.hasOwn(allowed, action)) throw new Error("Unknown action.");
  for (const key of Object.keys(args)) {
    if (!allowed[action].includes(key)) throw new Error(`Unexpected argument: ${key}`);
  }
  if (action === "mail.send") {
    const parsed = { to: addresses(args.to, true), subject: args.subject, body: args.body };
    for (const key of ["cc", "bcc"]) parsed[key] = addresses(args[key] ?? [], false);
    if (parsed.to.length + parsed.cc.length + parsed.bcc.length > 100) {
      throw new Error("At most 100 recipients are allowed.");
    }
    if (typeof args.subject !== "string" || args.subject.length > 500 || /[\r\n\0]/.test(args.subject)) {
      throw new Error("Subject must be a single line of at most 500 characters.");
    }
    if (typeof args.body !== "string" || new TextEncoder().encode(args.body).length > 65536) {
      throw new Error("Body must be plain text of at most 64 KiB.");
    }
    return { action, args: parsed };
  }
  if (action === "meetings.move") {
    if (typeof args.eventId !== "string" || !/^[a-zA-Z0-9_-]{1,1024}$/.test(args.eventId)) {
      throw new Error("A valid eventId from meetings.list is required.");
    }
    const start = parseTime(args.start);
    const end = parseTime(args.end);
    if (start.kind !== end.kind || end.value <= start.value) {
      throw new Error("Start and end must have the same type, and end must be after start.");
    }
  }
  return { action, args: { ...args } };
}

function addresses(value, required) {
  if (!Array.isArray(value) || (required && value.length === 0) || value.length > 100 ||
      value.some(item => typeof item !== "string" || item.length > 254 ||
        !/^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?\.[a-zA-Z]{2,}$/.test(item))) {
    throw new Error("Recipients must be arrays of plain email addresses (no display names).");
  }
  return [...new Set(value)];
}

export function parseTime(value) {
  if (typeof value !== "string") throw new Error("Meeting times must be strings.");
  const kind = /^\d{4}-\d{2}-\d{2}$/.test(value) ? "date" : "dateTime";
  if (kind === "dateTime" &&
      !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?(?:Z|[+-]\d{2}:\d{2})$/.test(value)) {
    throw new Error("Use YYYY-MM-DD or an RFC3339 timestamp with an explicit UTC offset.");
  }
  const datePart = value.slice(0, 10);
  const date = new Date(`${datePart}T00:00:00Z`);
  const instant = Date.parse(value);
  if ((kind === "dateTime" && (Number(value.slice(11, 13)) > 23 ||
      Number(value.slice(14, 16)) > 59 || Number(value.slice(17, 19)) > 59)) ||
      !Number.isFinite(instant) || !Number.isFinite(date.getTime()) ||
      date.toISOString().slice(0, 10) !== datePart) {
    throw new Error("Invalid meeting date.");
  }
  return { kind, value: instant };
}

export function meetingRange(now = new Date()) {
  const start = new Date(now);
  start.setHours(0, 0, 0, 0);
  const end = new Date(start);
  end.setDate(end.getDate() + 8);
  return { timeMin: start.toISOString(), timeMax: end.toISOString() };
}

function base64(bytes) {
  let text = "";
  for (const byte of bytes) text += String.fromCharCode(byte);
  return btoa(text);
}

export function makeMime(args) {
  const headers = [
    `To: ${args.to.join(",\r\n ")}`,
    ...(args.cc.length ? [`Cc: ${args.cc.join(",\r\n ")}`] : []),
    ...(args.bcc.length ? [`Bcc: ${args.bcc.join(",\r\n ")}`] : []),
    `Subject: ${Array.from(args.subject.matchAll(/[\s\S]{1,10}/gu),
      match => `=?UTF-8?B?${base64(new TextEncoder().encode(match[0]))}?=`).join("\r\n ")}`,
    "MIME-Version: 1.0",
    'Content-Type: text/plain; charset="UTF-8"',
    "Content-Transfer-Encoding: base64"
  ];
  const body = base64(new TextEncoder().encode(args.body)).match(/.{1,76}/g)?.join("\r\n") ?? "";
  const mime = `${headers.join("\r\n")}\r\n\r\n${body}\r\n`;
  return base64(new TextEncoder().encode(mime)).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

function eventSummary(event) {
  return {
    eventId: event.id, title: event.summary ?? "(Untitled)", etag: event.etag,
    start: event.start, end: event.end, organizer: event.organizer,
    attendees: (event.attendees ?? []).map(person => ({
      email: person.email, responseStatus: person.responseStatus
    })),
    recurringEventId: event.recurringEventId,
    originalStartTime: event.originalStartTime,
    movable: event.organizer?.self === true && !event.recurrence &&
      (!event.eventType || event.eventType === "default")
  };
}

export class GoogleActions {
  constructor(getToken, fetcher = fetch, now = () => new Date()) {
    this.getToken = getToken;
    this.fetcher = fetcher;
    this.now = now;
  }

  async api(url, options = {}) {
    const token = await this.getToken();
    const response = await this.fetcher(url, {
      ...options,
      headers: { Authorization: `Bearer ${token}`, "Content-Type": "application/json", ...options.headers },
      signal: AbortSignal.timeout(25000)
    });
    if (!response.ok) {
      // Do not automatically retry writes: an interrupted send may already have succeeded.
      if (response.status === 401) throw new Error("Google authorization expired or was revoked. Reconnect in the extension.");
      if (response.status === 412) throw new Error("The meeting changed after review. List meetings and request the move again.");
      throw new Error(`Google API returned HTTP ${response.status}. Check API enablement, consent, permissions, and quota.`);
    }
    return response.json();
  }

  async contacts() {
    const contacts = [];
    let pageToken;
    for (let page = 0; page < 100; page++) {
      const url = new URL("https://people.googleapis.com/v1/people/me/connections");
      url.search = new URLSearchParams({
        personFields: "names,emailAddresses", sortOrder: "FIRST_NAME_ASCENDING", pageSize: "1000",
        ...(pageToken ? { pageToken } : {})
      });
      const data = await this.api(url.toString());
      for (const person of data.connections ?? []) {
        contacts.push({
          resourceName: person.resourceName,
          name: person.names?.[0]?.displayName ?? person.emailAddresses?.[0]?.value ?? "",
          emails: (person.emailAddresses ?? []).map(email => email.value)
        });
      }
      pageToken = data.nextPageToken;
      if (!pageToken) {
        contacts.sort((a, b) => a.name.localeCompare(b.name) || a.resourceName.localeCompare(b.resourceName));
        return { contacts: contacts.slice(0, 50), ordering: "Display name, alphabetical", total: contacts.length };
      }
    }
    throw new Error("Contact pagination exceeded 100 pages; no partial list was returned.");
  }

  async meetings() {
    const range = meetingRange(this.now());
    const meetings = [];
    let pageToken;
    for (let page = 0; page < 50; page++) {
      const url = new URL("https://www.googleapis.com/calendar/v3/calendars/primary/events");
      url.search = new URLSearchParams({
        ...range, singleEvents: "true", orderBy: "startTime", maxResults: "100",
        showDeleted: "false", ...(pageToken ? { pageToken } : {})
      });
      const data = await this.api(url.toString());
      meetings.push(...(data.items ?? []).filter(event => event.status !== "cancelled").map(eventSummary));
      pageToken = data.nextPageToken;
      if (!pageToken) return { calendar: "primary", ...range, timeZone: data.timeZone, meetings };
    }
    throw new Error("Meeting pagination exceeded 5000 events; no partial list was returned.");
  }

  eventUrl(id) {
    return `https://www.googleapis.com/calendar/v3/calendars/primary/events/${encodeURIComponent(id)}`;
  }

  async prepare(request) {
    const valid = validateRequest(request);
    if (valid.action !== "meetings.move") return valid;
    const event = await this.api(this.eventUrl(valid.args.eventId));
    const summary = eventSummary(event);
    if (!summary.movable || event.status === "cancelled" || !event.etag) {
      throw new Error("Only your own normal meetings or individual occurrences can be moved; series and invitations are not supported.");
    }
    const kind = parseTime(valid.args.start).kind;
    if (!event.start?.[kind] || !event.end?.[kind]) {
      throw new Error("A move cannot change an all-day meeting into a timed meeting, or vice versa.");
    }
    return { ...valid, event: summary };
  }

  async execute(prepared) {
    const { action, args } = validateRequest(prepared);
    switch (action) {
      case "contacts.list": return this.contacts();
      case "meetings.list": return this.meetings();
      case "mail.send":
        return this.api("https://gmail.googleapis.com/gmail/v1/users/me/messages/send", {
          method: "POST", body: JSON.stringify({ raw: makeMime(args) })
        });
      case "meetings.move": {
        if (!prepared.event?.etag) throw new Error("Meeting must be reviewed before moving.");
        const kind = parseTime(args.start).kind;
        return this.api(`${this.eventUrl(args.eventId)}?sendUpdates=all`, {
          method: "PATCH", headers: { "If-Match": prepared.event.etag },
          body: JSON.stringify({
            start: { [kind]: args.start, ...(prepared.event.start.timeZone ? { timeZone: prepared.event.start.timeZone } : {}) },
            end: { [kind]: args.end, ...(prepared.event.end.timeZone ? { timeZone: prepared.event.end.timeZone } : {}) }
          })
        }).then(eventSummary);
      }
    }
  }
}
