# Google Actions for Microsoft Edge

A Manifest V3 Edge extension and packaged Windows App Actions provider. Windows
apps invoke the companion over COM; it forwards requests over a current-user-only
named pipe to Edge's Native Messaging host. The extension calls Google's official
APIs and displays approvals. No Gmail DOM automation, cookie extraction, remote
service, or localhost HTTP control endpoint is used.

```text
Windows App Actions consumer
  -> packaged IActionProvider (COM)
  -> current-user named pipe
  -> native messaging host <-> Edge extension
                               -> Gmail / People / Calendar APIs
                               -> user approval for every write
```

## Supported actions

| Windows action ID | Text inputs | Result |
| --- | --- | --- |
| `EdgeGoogleActions.ListContacts` | None | First 50 saved contacts sorted by display name, or fewer if fewer exist |
| `EdgeGoogleActions.ListMeetings` | None | Primary calendar events from local midnight today until midnight eight days later (exclusive) |
| `EdgeGoogleActions.SendEmail` | `To`, `Subject`, `Body` | Send one plain-text Gmail message after approval; `To` is one plain email address |
| `EdgeGoogleActions.MoveMeeting` | `EventId`, `Start`, `End` | Move one organizer-owned event or recurring occurrence after approval; notify all attendees |

Each action returns a **Text** output named `Json`. Its content is a JSON envelope:

```json
{ "ok": true, "result": { "contacts": [] } }
```

Failures are explicit, including denial, expiration, disconnected Edge, Google
authorization errors, and concurrent event modifications:

```json
{ "ok": false, "error": { "message": "The user declined the action." } }
```

The envelope may also contain an opaque request `id`. Consumers must check `ok`;
approval is not itself evidence that Google completed the action.

**Date semantics:** "today through next week" means today plus the next seven
calendar days, not the next Monday-to-Sunday week. Boundaries use the Windows/Edge
local timezone and account for daylight saving. Google returns event timezones
separately. Ongoing events overlapping today's range can be included. All event
types are listed; only normal events owned by the connected account can be moved.
Use a returned occurrence `eventId` to move just that occurrence. Recurring series
masters, invitations organized by someone else, and conversions between timed and
all-day events are rejected. All-day `End` is exclusive.

## Prerequisites

- Windows 11 build 26100 or newer, with Windows App Actions runtime support.
  OS version alone does not guarantee an action consumer is installed.
- Microsoft Edge desktop.
- .NET 10 SDK to build; the publish scripts produce self-contained binaries.
- Windows SDK 10.0.26100 or newer with MakeAppx and SignTool to package.
- Node.js 22 or newer to run extension tests; no npm dependencies are required.
- A Google account, and a Google Cloud project with a **Desktop app** OAuth
  client. An organizational account may require Google Workspace admin approval.

## 1. Configure Google OAuth

For personal development:

1. Create a project in [Google Cloud Console](https://console.cloud.google.com/).
2. Enable **Gmail API**, **People API**, and **Google Calendar API**.
3. Configure Google Auth Platform branding and audience. Use External / Testing
   for a personal Gmail account and add your email as a test user.
4. Declare these scopes in Data Access:

   ```text
   https://www.googleapis.com/auth/gmail.send
   https://www.googleapis.com/auth/contacts.readonly
   https://www.googleapis.com/auth/calendar.events
   ```

5. Create an OAuth client with application type **Desktop app**, and download
   its JSON file **outside this project**. Do not create a Web client, configure
   a Chromium redirect, or put credentials in the extension manifest.

The host uses Google's installed-app authorization-code flow, random state,
PKCE S256, and a temporary `127.0.0.1` listener on an OS-assigned port. Tokens
are exchanged by the host, not by a public service. Refresh tokens and the client
configuration are encrypted with Windows DPAPI for the current user under
`%LOCALAPPDATA%\EdgeGoogleActions`. Short-lived access tokens are passed only
to the extension over Native Messaging and are not persisted by the extension.

Testing-mode consent for these scopes generally yields refresh tokens that expire
after seven days. Reconnect when required. Personal/testing use can qualify for
verification exceptions. A public release requesting these sensitive scopes
normally needs Google OAuth verification, a public homepage/privacy policy,
scope justifications, and a demonstration. Listing saved contacts deliberately
avoids restricted Gmail read scopes; it does **not** rank people by email frequency.
The Desktop client secret is not a security boundary in a distributed desktop app.
Normally the publisher supplies the OAuth client, so end users only install and
consent; end users do not each create Cloud projects.

## 2. Load the extension and install its host

At `edge://extensions`, enable **Developer mode**, choose **Load unpacked**, and
select this project's `extension` directory. Copy the 32-character extension ID.

From PowerShell in this project:

```powershell
.\scripts\Install-NativeHost.ps1 -ExtensionId '<your-extension-id>' -Runtime win-arm64
```

Use `win-x64` or `win-x86` when appropriate. This builds a self-contained native
host in `%LOCALAPPDATA%\EdgeGoogleActions\host`, writes its exact extension-origin
allowlist, and registers it under:

```text
HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.edge_google_actions.bridge
```

Import the downloaded Desktop client, without copying it into the project:

```powershell
& "$env:LOCALAPPDATA\EdgeGoogleActions\host\EdgeGoogleActions.NativeHost.exe" `
  --import-client 'C:\path\outside-project\downloaded-desktop-client.json'
```

Reload the extension, open its toolbar button, and choose **Connect Google
account**. Select the intended Google account and grant all three permissions.
The authorized account need not be the account currently displayed in a Gmail
tab. Gmail and Calendar tabs need not be open.

The toolbar page also provides contact and meeting lists, bridge errors, and a
**Disconnect and revoke access** button. Disconnect before changing clients or
accounts. If revocation fails due to a network/server error, credentials are
retained and the error is displayed; retry rather than assuming access is revoked.

Use just one Edge profile for this extension. The first connected profile owns
the per-user bridge; other profiles report an error instead of silently routing
requests to the wrong Google account. Edge must remain running.

## 3. Build and install the Windows App Actions package

```powershell
.\scripts\Build-Package.ps1 -Architecture arm64
```

Use `x64` or `x86` for other machines. The script publishes the companion,
generates tile images, and runs MakeAppx with manifest validation. Outputs are
`artifacts\arm64\EdgeGoogleActions.msix`, `artifacts\arm64\edge-extension.zip`,
and a self-contained host in `artifacts\arm64\native-host`. The host is also
built/registered by the separate install script above. An **unsigned** MSIX is
not installable as-is.

For local development, create a signing certificate in your current-user store:

```powershell
$cert = New-SelfSignedCertificate `
  -Type Custom -Subject 'CN=EdgeGoogleActionsDev' `
  -KeyUsage DigitalSignature -FriendlyName 'Edge Google Actions development' `
  -CertStoreLocation 'Cert:\CurrentUser\My' `
  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

Export-Certificate -Cert $cert -FilePath '.\artifacts\EdgeGoogleActionsDev.cer'
```

For sideloading, **deliberately trust this certificate** on your development
machine. In an elevated PowerShell prompt:

```powershell
Import-Certificate -FilePath 'C:\d\edge-add-on-for-uc\artifacts\EdgeGoogleActionsDev.cer' `
  -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
```

Then, back in your normal current-user prompt:

```powershell
.\scripts\Build-Package.ps1 -Architecture arm64 -CertificateThumbprint $cert.Thumbprint
Add-AppxPackage '.\artifacts\arm64\EdgeGoogleActions.msix'
```

Only trust certificates you control. These commands change your machine's trust
and installed apps; the build script does not perform those changes. Enable
Developer Mode/sideloading if required by your Windows policy. For distribution,
replace the development publisher identity and use appropriate trusted signing
or Microsoft Store packaging.

The package registers `com.microsoft.windows.ai.actions` and the same COM CLSID
used by `GoogleActionProvider`. The provider manually implements `IActionProvider`
because `Microsoft.AI.Actions` 0.1.0's attribute generator does not accept
parameterless actions. Microsoft's package still generates the COM activation
infrastructure. Registration JSON is checked in rather than generated so
`allowedAppInvokers` remains intact.

## Invoke from outside Edge

Install Microsoft's
[App Actions Testing Playground](https://aka.ms/AppActionsTestingPlayground).
In its **Action catalog**, find the four `EdgeGoogleActions.*` actions. Read
actions need no input entities. For a mail action, create Text entities named
`To`, `Subject`, and `Body`. For a move, first list meetings and then pass its
`eventId`, plus `Start` and `End` as Text entities.

Timed values must include seconds and an explicit offset, for example
`2026-10-16T10:00:00+01:00` or `2026-10-16T09:00:00Z`. All-day values use
`YYYY-MM-DD`. A write opens a review tab in Edge with the full recipients/body or
meeting/attendee/time changes. Approval is one-use and expires after at most two
minutes. Leave the action invocation running until it returns.

Applications can use Windows `ActionCatalog` and `ActionInvocation` APIs to
discover/invoke these actions. See Microsoft's
[App Actions documentation](https://learn.microsoft.com/en-us/windows/ai/app-actions/).
Discovery is not a promise that a specific Windows shell surface will display an
action; it depends on the consumer and OS capabilities.

### Scriptable local API

For scripts that do not use the Windows action runtime, the same host also
provides a JSON request-file CLI. This uses the same current-user pipe and the
same extension approval path; it does not start a separate Google connection.

```powershell
@{ action = 'contacts.list'; args = @{} } |
  ConvertTo-Json | Set-Content "$env:TEMP\google-action.json"

& "$env:LOCALAPPDATA\EdgeGoogleActions\host\EdgeGoogleActions.NativeHost.exe" `
  --invoke "$env:TEMP\google-action.json"
```

The process writes the result envelope to stdout and exits nonzero for errors.
Example requests:

```json
{ "action": "meetings.list", "args": {} }
```

```json
{
  "action": "mail.send",
  "args": {
    "to": ["recipient@example.com"],
    "cc": [],
    "bcc": [],
    "subject": "Planning",
    "body": "Can we meet tomorrow?"
  }
}
```

```json
{
  "action": "meetings.move",
  "args": {
    "eventId": "id-from-meetings-list",
    "start": "2026-10-16T10:00:00+01:00",
    "end": "2026-10-16T11:00:00+01:00"
  }
}
```

CLI mail requests support up to 100 plain-address recipients across To/Cc/Bcc,
500 subject characters, and 64 KiB of UTF-8 plain text. Attachments, HTML, aliases,
and meeting-series editing are not implemented.

## Trust and reliability boundaries

- This is a **local, current-Windows-user API**. All Windows apps may discover the
  actions (`allowedAppInvokers: ["*"]`), and processes running as that user can use
  the pipe. They can read the authorized contact/calendar data without another
  prompt. Do not deploy this policy on a machine where same-user processes are
  untrusted; tighten the action allowlist for a restricted deployment. The pipe
  remains a same-user interface, not per-application authentication.
- Native Messaging is allowlisted to exactly one extension origin. No web-page
  message listener, content script, arbitrary URL fetch, or external token API
  is exposed.
- Every mail send and meeting move requires approval inside an extension-owned
  page. Treat requests as untrusted; the review page does not assert the calling
  app's identity.
- Moves use the reviewed event's ETag in `If-Match`. If the event changes after
  review, Google rejects the update rather than overwriting the newer event.
- Network timeouts after a write are **ambiguous**: Google may have committed the
  change even if the caller did not receive a result. Verify Gmail/Calendar
  before retrying. Writes are never automatically retried, and request IDs are
  not durable idempotency keys. A write already in flight cannot be undone by
  closing the caller.
- Approval details exist temporarily in `chrome.storage.session`, restricted to
  trusted extension contexts, and are removed after resolution. Requests are
  not replayed after a browser/host restart. No message bodies or contact/event
  lists are deliberately logged to disk.
- Contact enumeration is capped at 100 pages of 1000; meetings at 50 pages of
  100. Exceeding a cap or the 900 KB bridge response limit returns an error, not
  a success-shaped partial list.

## Development checks

From the project root:

```powershell
npm test
npm run check
npm run test:native
dotnet build '.\native\ActionProvider\ActionProvider.csproj'
.\scripts\Build-Package.ps1 -Architecture arm64
```

The JavaScript suite mocks Google and browser APIs; it covers pagination,
alphabetical 50-contact selection, date/DST boundaries, MIME, move semantics,
ETag conflicts, validation, and approval/denial/expiry behavior. The native
suite launches a real host and exercises binary framing and a full external
named-pipe -> simulated extension -> caller round trip. It does not sign in,
send real email, or alter real meetings.

Live OAuth and App Actions discovery/invocation require your own consent and an
installed, signed package. Complete those checks using the Playground and your
own test Google account before distributing this app.

## Uninstall

Disconnect/revoke Google from the extension first. Remove the extension at
`edge://extensions`, uninstall **Google Actions for Edge** in Windows Settings,
and remove the specific native-host registry key shown above. After closing
Edge, remove the host and DPAPI files under
`%LOCALAPPDATA%\EdgeGoogleActions` if no longer needed. Removing a local token
file alone does not revoke Google's authorization; use Google Account's
third-party access settings if the extension is unavailable. Remove a development
certificate you deliberately trusted when you no longer need it.

## References

- [Edge extensions overview](https://learn.microsoft.com/en-us/microsoft-edge/extensions/)
- [Edge Native Messaging](https://learn.microsoft.com/en-us/microsoft-edge/extensions/developer-guide/native-messaging)
- [App Actions on Windows](https://learn.microsoft.com/en-us/windows/ai/app-actions/)
- [Manual IActionProvider](https://learn.microsoft.com/en-us/windows/ai/app-actions/actions-iactionprovider-manual)
- [Google installed-app OAuth](https://developers.google.com/identity/protocols/oauth2/native-app)
- [Google Gmail scopes](https://developers.google.com/workspace/gmail/api/auth/scopes)
- [Google verification](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification)
