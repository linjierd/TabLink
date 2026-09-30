import assert from 'node:assert/strict';
import { createHash, createPublicKey, verify } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';

const source = readFileSync(new URL('../entry/src/main/ets/update/StableUpdatePolicy.ts', import.meta.url), 'utf8');
const js = stripTypeScriptTypes(source, { mode: 'strip' });
const policy = await import('data:text/javascript;base64,' + Buffer.from(js).toString('base64'));
let passed = 0;
function check(action) { action(); passed++; }

const fixtureText = readFileSync(new URL('./fixtures/stable-manifest-valid.json', import.meta.url), 'utf8');
const envelope = policy.parseSignedEnvelope(fixtureText);
const payload = Buffer.from(envelope.payload, 'base64');
const signature = Buffer.from(envelope.signature, 'base64');
const publicKey = createPublicKey({ key: Buffer.from(policy.STABLE_SIGNER_SPKI_BASE64, 'base64'), format: 'der', type: 'spki' });
check(() => assert.equal(policy.STABLE_MANIFEST_URL, 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json'));
check(() => assert.equal(policy.STABLE_SIGNER_SPKI_BASE64, 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXlJPucXJJzRwjf1p/46Uuebom2dMFvSSiN4wdwxVVtbb9bdaIGnru39akKRRd7BaTlUaEk2Thmb/MpqNPYNu4A=='));
check(() => assert(verify('sha256', payload, publicKey, signature), 'shared fixture signature'));
check(() => {
  const tampered = Buffer.from(payload.toString('utf8').replace('"0.8.0"', '"0.8.1"'));
  assert.equal(verify('sha256', tampered, publicKey, signature), false);
});

const fixture = policy.parseVerifiedPayload(payload.toString('utf8'), Date.parse('2026-09-29T15:00:00Z'));
check(() => assert.equal(fixture.releaseId, 'fixture-0.8.0'));
check(() => assert.equal(fixture.artifacts[0].platform, 'android'));
check(() => assert.equal(policy.selectHarmonyUpdate(fixture, '0.7.9', 799, true), undefined));
check(() => assert.throws(() => policy.parseSignedEnvelope('{"payload":"AA==","p\\u0061yload":"AA==","signature":"AA=="}')));
check(() => assert.throws(() => policy.parseSignedEnvelope('{"payload":"AB==","signature":"AA=="}'), /Base64/));
for (const value of ['0.8', '0.8.0-beta', '00.8.0', '0.8.0+1', '1.02.3']) {
  check(() => assert.throws(() => policy.parseStableVersion(value)));
}
check(() => assert(policy.compareStableVersions(policy.parseStableVersion('0.8.0'), policy.parseStableVersion('0.7.99')) > 0));

const harmonyObject = {
  schema: 1, channel: 'stable', releaseId: 'stable-0.9.0', publishedAtUtc: '2026-09-29T14:35:00Z',
  rolloutPercentage: 37, minimumProtocolVersion: 1,
  artifacts: [{ platform: 'harmony', version: '0.9.0', build: 900,
    url: 'https://updates.tablink.example/releases/0.9.0/TabLink.hap', size: 123456,
    sha256: 'ab'.repeat(32), installerUrl: 'https://appgallery.huawei.com/app/C123456', notes: 'stable' }]
};
const harmony = policy.parseVerifiedPayload(JSON.stringify(harmonyObject), Date.parse('2026-09-29T15:00:00Z'));
check(() => assert.equal(policy.selectHarmonyUpdate(harmony, '0.8.0', 800, true)?.build, 900));
check(() => assert.equal(policy.selectHarmonyUpdate(harmony, '0.9.0', 800, true), undefined));
check(() => assert.equal(policy.selectHarmonyUpdate(harmony, '0.8.0', 800, false), undefined));
check(() => assert.equal(policy.selectHarmonyUpdate(harmony, '0.8.0', 900, true), undefined));
check(() => assert.equal(harmony.artifacts[0].sha256, 'AB'.repeat(32)));
check(() => {
  const zero = { ...harmonyObject, rolloutPercentage: 0 };
  assert.equal(policy.parseVerifiedPayload(JSON.stringify(zero), Date.parse('2026-09-29T15:00:00Z')).rolloutPercentage, 0);
  assert.equal(policy.includedByDigest(new Uint8Array([0, 0, 0, 0]), 0), false);
});
for (const rolloutPercentage of [-1, 101]) {
  check(() => assert.throws(() => policy.parseVerifiedPayload(JSON.stringify({ ...harmonyObject, rolloutPercentage }), Date.parse('2026-09-29T15:00:00Z')), /比例/));
}
check(() => {
  const digest = createHash('sha256').update('0123456789abcdef0123456789abcdef\nstable-0.9.0').digest();
  assert.equal(policy.includedByDigest(digest, 37), policy.includedByDigest(digest, 37));
  assert.equal(policy.includedByDigest(digest, 100), true);
});
check(() => {
  const digest = createHash('sha256').update('0123456789abcdef0123456789abcdef\nstable-0.8.0').digest();
  assert.equal(digest.readUInt32BE(0) % 100, 20);
  assert.equal(policy.includedByDigest(digest, 20), false);
  assert.equal(policy.includedByDigest(digest, 21), true);
});
check(() => {
  const duplicate = structuredClone(harmonyObject); duplicate.artifacts.push({ ...duplicate.artifacts[0], version: '0.9.1' });
  assert.throws(() => policy.parseVerifiedPayload(JSON.stringify(duplicate), Date.parse('2026-09-29T15:00:00Z')), /重复/);
});
check(() => {
  const queried = structuredClone(harmonyObject);
  queried.artifacts[0].url = 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2FTabLink.hap';
  const parsed = policy.parseVerifiedPayload(JSON.stringify(queried), Date.parse('2026-09-29T15:00:00Z'));
  assert.equal(parsed.artifacts[0].url, queried.artifacts[0].url);
});
for (const url of ['https://user@linjie.space/download/api/download?path=TabLink%2Fstable%2FTabLink.hap',
  'https://linjie.space/download/api/download?path=TabLink%2Fstable%2FTabLink.hap#unsafe']) {
  check(() => {
    const invalid = structuredClone(harmonyObject); invalid.artifacts[0].url = url;
    assert.throws(() => policy.parseVerifiedPayload(JSON.stringify(invalid), Date.parse('2026-09-29T15:00:00Z')), /HTTPS/);
  });
}
for (const url of ['https://appgallery.huawei.com/app/C123456',
  'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fharmony']) {
  check(() => assert.equal(policy.isAllowedHarmonyInstallerUrl(url), true));
}
for (const url of ['http://appgallery.huawei.com/app/C123456', 'https://user@appgallery.huawei.com/app/C123456',
  'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fharmony#unsafe',
  'https://linjie.space/download/api/download?path=Other%2Fharmony',
  'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fharmony&extra=1',
  'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fharmony&path=TabLink%2Fstable%2Fother']) {
  check(() => assert.equal(policy.isAllowedHarmonyInstallerUrl(url), false));
}
for (const publishedAtUtc of ['2026-09-29T14:35:00+00:00', '2026-09-29T14:35:00.1Z', '2026-02-31T14:35:00Z', '2026-09-29T24:00:00Z']) {
  check(() => {
    const invalid = { ...harmonyObject, publishedAtUtc };
    assert.throws(() => policy.parseVerifiedPayload(JSON.stringify(invalid), Date.parse('2026-09-29T15:00:00Z')));
  });
}
check(() => {
  const stale = { ...harmonyObject, publishedAtUtc: '2025-09-27T00:00:00Z' };
  assert.throws(() => policy.parseVerifiedPayload(JSON.stringify(stale), Date.parse('2026-09-29T15:00:00Z')), /过期/);
});
check(() => {
  const future = { ...harmonyObject, publishedAtUtc: '2026-10-01T00:00:00Z' };
  assert.throws(() => policy.parseVerifiedPayload(JSON.stringify(future), Date.parse('2026-09-29T15:00:00Z')), /异常/);
});

console.log(`PASS ${passed} stable-update policy/signature assertions (pure Node; no Harmony SDK or AppGallery execution).`);
