import test from "node:test";
import assert from "node:assert/strict";
import { GoogleActions, makeMime, meetingRange, parseTime, validateRequest } from "../extension/google.js";

const mail = {
  action: "mail.send",
  args: { to: ["recipient@example.com"], subject: "Hello", body: "Message\nSecond line" }
};
function mockApi(responses, clock) {
  const calls = [];
  return {
    calls,
    api: new GoogleActions(async () => "test-access-token", async (url, options) => {
      calls.push({ url, ...options });
      const next = responses.shift();
      if (!next) throw new Error("Unexpected API call.");
      return new Response(JSON.stringify(next.data), { status: next.status ?? 200 });
    }, clock)
  };
}
const ownMeeting = {
  id: "abc_20261015T090000Z", summary: "Planning", etag: '"revision-1"',
  organizer: { self: true, email: "owner@example.com" },
  attendees: [{ email: "guest@example.com" }],
  start: { dateTime: "2026-10-15T10:00:00+01:00", timeZone: "Europe/London" },
  end: { dateTime: "2026-10-15T11:00:00+01:00", timeZone: "Europe/London" }
};
const move = {
  action: "meetings.move",
  args: { eventId: ownMeeting.id, start: "2026-10-16T10:00:00+01:00", end: "2026-10-16T11:00:00+01:00" }
};

test("validates external actions, strict argument sets and recipients", () => {
  assert.deepEqual(validateRequest(mail).args.cc, []);
  for (const request of [
    { action: "auth.token" }, { action: "toString" }, { action: "contacts.list", args: { limit: 51 } },
    { ...mail, args: { ...mail.args, subject: "Hi\r\nBcc: attacker@example.com" } },
    { ...mail, args: { ...mail.args, to: ["Alice <alice@example.com>"] } },
    { ...mail, args: { ...mail.args, to: [] } },
    { ...mail, args: { ...mail.args, body: "x".repeat(65537) } },
    { ...mail, args: { ...mail.args, cc: Array.from({ length: 100 }, (_, index) => `other${index}@example.com`) } },
    { ...move, args: { ...move.args, eventId: "../other" } }
  ]) assert.throws(() => validateRequest(request));
});

test("requires real dates, explicit time zones and a positive same-kind interval", () => {
  assert.equal(parseTime("2026-10-15").kind, "date");
  for (const value of ["2026-02-30", "2026-10-15T10:00:00", "2026-10-15T24:00:00Z", "nonsense"]) {
    assert.throws(() => parseTime(value));
  }
  for (const args of [
    { ...move.args, end: move.args.start },
    { ...move.args, end: "2026-10-14T12:00:00Z" },
    { ...move.args, start: "2026-10-15" }
  ]) assert.throws(() => validateRequest({ ...move, args }));
});

test("range covers local today through end of day seven days ahead, including DST", () => {
  const old = process.env.TZ;
  process.env.TZ = "Europe/London";
  try {
    assert.deepEqual(meetingRange(new Date("2026-10-22T18:30:00+01:00")), {
      timeMin: "2026-10-21T23:00:00.000Z", timeMax: "2026-10-30T00:00:00.000Z"
    });
  } finally {
    if (old === undefined) delete process.env.TZ;
    else process.env.TZ = old;
  }
});

test("MIME preserves Unicode and recipient types without oversized encoded words", () => {
  const args = validateRequest({
    action: "mail.send",
    args: { ...mail.args, subject: "😀".repeat(30), body: "Hello 🌎", cc: ["copy@example.com"], bcc: ["private@example.com"] }
  }).args;
  const mime = Buffer.from(makeMime(args), "base64url").toString("utf8");
  assert.match(mime, /Cc: copy@example.com\r\nBcc: private@example.com/);
  const words = mime.match(/=\?UTF-8\?B\?[^?]*\?=/g);
  assert.ok(words.every(word => word.length <= 75));
  assert.equal(words.map(word => Buffer.from(word.slice(10, -2), "base64").toString("utf8")).join(""), args.subject);
  assert.equal(Buffer.from(mime.split("\r\n\r\n")[1], "base64").toString("utf8"), args.body);
  assert.ok(mime.split("\r\n").every(line => line.length < 998));
});

test("paginates contacts then returns exactly the first 50 alphabetically", async () => {
  const people = Array.from({ length: 75 }, (_, index) => ({
    resourceName: `people/${index}`, names: [{ displayName: `Person ${String(index).padStart(3, "0")}` }],
    emailAddresses: [{ value: `person${index}@example.com` }]
  })).reverse();
  const { api, calls } = mockApi([
    { data: { connections: people.slice(0, 30), nextPageToken: "page 2" } },
    { data: { connections: people.slice(30) } }
  ]);
  const result = await api.contacts();
  assert.equal(result.contacts.length, 50);
  assert.equal(result.total, 75);
  assert.equal(result.contacts[0].name, "Person 000");
  assert.equal(result.contacts[49].name, "Person 049");
  assert.equal(new URL(calls[1].url).searchParams.get("pageToken"), "page 2");
});

test("lists all meeting pages, expands recurring instances and excludes cancelled meetings", async () => {
  const { api, calls } = mockApi([
    { data: { items: [ownMeeting], nextPageToken: "next", timeZone: "Europe/London" } },
    { data: { items: [{ ...ownMeeting, id: "second", recurringEventId: "series" }, { ...ownMeeting, status: "cancelled" }], timeZone: "Europe/London" } }
  ], () => new Date("2026-10-15T12:00:00Z"));
  const result = await api.meetings();
  assert.equal(result.meetings.length, 2);
  assert.equal(result.meetings[1].recurringEventId, "series");
  const params = new URL(calls[0].url).searchParams;
  assert.equal(params.get("singleEvents"), "true");
  assert.equal(params.get("orderBy"), "startTime");
  assert.equal(new URL(calls[1].url).searchParams.get("pageToken"), "next");
});

test("meeting move changes only start/end, preserves zones and uses reviewed ETag", async () => {
  const { api, calls } = mockApi([
    { data: ownMeeting }, { data: { ...ownMeeting, ...move.args, etag: '"revision-2"' } }
  ]);
  const prepared = await api.prepare(move);
  assert.equal(calls.length, 1);
  await api.execute(prepared);
  const patch = calls[1];
  assert.equal(patch.method, "PATCH");
  assert.equal(patch.headers["If-Match"], ownMeeting.etag);
  assert.equal(new URL(patch.url).searchParams.get("sendUpdates"), "all");
  assert.deepEqual(JSON.parse(patch.body), {
    start: { dateTime: move.args.start, timeZone: "Europe/London" },
    end: { dateTime: move.args.end, timeZone: "Europe/London" }
  });
});

test("blocks invitation edits, series edits, cancelled/special events, and time-type conversion", async () => {
  for (const event of [
    { ...ownMeeting, organizer: { self: false } },
    { ...ownMeeting, recurrence: ["RRULE:FREQ=DAILY"] },
    { ...ownMeeting, status: "cancelled" },
    { ...ownMeeting, eventType: "outOfOffice" },
    { ...ownMeeting, start: { date: "2026-10-15" }, end: { date: "2026-10-16" } }
  ]) {
    const { api, calls } = mockApi([{ data: event }]);
    await assert.rejects(() => api.prepare(move));
    assert.equal(calls.length, 1);
  }
});

test("moves one recurring occurrence and supports all-day exclusive end dates", async () => {
  const occurrence = { ...ownMeeting, recurringEventId: "parent", originalStartTime: ownMeeting.start };
  const { api } = mockApi([{ data: occurrence }, { data: occurrence }]);
  const prepared = await api.prepare(move);
  assert.equal((await api.execute(prepared)).recurringEventId, "parent");
  const day = { ...ownMeeting, start: { date: "2026-10-15" }, end: { date: "2026-10-16" } };
  const { api: dayApi, calls } = mockApi([{ data: day }, { data: day }]);
  const dayMove = { action: "meetings.move", args: { eventId: day.id, start: "2026-10-20", end: "2026-10-22" } };
  await dayApi.execute(await dayApi.prepare(dayMove));
  assert.deepEqual(JSON.parse(calls[1].body), { start: { date: "2026-10-20" }, end: { date: "2026-10-22" } });
});

test("surfaces Google failures, never retries writes and requires a prepared move", async () => {
  for (const [status, pattern] of [[401, /Reconnect/], [412, /changed after review/], [403, /HTTP 403/], [429, /HTTP 429/]]) {
    const { api, calls } = mockApi([{ status, data: {} }]);
    await assert.rejects(() => api.execute(validateRequest(mail)), pattern);
    assert.equal(calls.length, 1);
  }
  const { api } = mockApi([]);
  await assert.rejects(() => api.execute(move), /reviewed/);
});
