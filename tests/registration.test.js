import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

test("Windows discovery, COM class and provider dispatch agree on all four actions", async () => {
  const [registrationText, source, manifest] = await Promise.all([
    readFile(new URL("../native/ActionProvider/Assets/registration.json", import.meta.url), "utf8"),
    readFile(new URL("../native/ActionProvider/GoogleActionProvider.cs", import.meta.url), "utf8"),
    readFile(new URL("../packaging/AppxManifest.xml", import.meta.url), "utf8")
  ]);
  const registration = JSON.parse(registrationText);
  assert.equal(registration.version, 3);
  assert.equal(registration.actions.length, 4);
  const clsid = source.match(/\[Guid\("([^"]+)"\)\]/)[1];
  assert.ok(manifest.includes(`Id="${clsid}"`));
  assert.match(manifest, /Name="com\.microsoft\.windows\.ai\.actions"/);
  assert.match(manifest, /PublicFolder="Assets"/);
  assert.match(manifest, /<Registration xmlns="">registration\.json<\/Registration>/);
  for (const action of registration.actions) {
    assert.ok(source.includes(`"${action.id}" =>`), `Missing dispatch for ${action.id}`);
    assert.equal(action.invocation.type, "COM");
    assert.equal(action.invocation.clsid, clsid);
    assert.deepEqual(action.allowedAppInvokers, ["*"]);
    assert.deepEqual(action.outputs, [{ name: "Json", kind: "Text" }]);
    assert.equal(action.isAvailable, true);
    assert.equal(action.usesGenerativeAI, false);
    assert.deepEqual(action.inputs.map(input => input.name), action.inputCombinations[0].inputs);
    for (const input of action.inputs) {
      assert.equal(input.kind, "Text");
      assert.ok(source.includes(`Required("${input.name}")`));
    }
  }
});

test("Edge requests only API hosts and local extension control permissions", async () => {
  const manifest = JSON.parse(await readFile(new URL("../extension/manifest.json", import.meta.url), "utf8"));
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual(manifest.permissions, ["nativeMessaging", "storage", "alarms"]);
  assert.equal(manifest.externally_connectable, undefined);
  assert.equal(manifest.content_scripts, undefined);
  assert.equal(manifest.background.type, "module");
  for (const host of manifest.host_permissions) assert.match(host, /^https:\/\/(?:gmail|people|www)\.googleapis\.com\//);
});
