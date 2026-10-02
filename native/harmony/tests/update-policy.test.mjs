import assert from 'node:assert/strict';
import { createHash, createPublicKey, verify } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';

const source = readFileSync(new URL('../entry/src/main/ets/update/StableUpdatePolicy.ts', import.meta.url), 'utf8');
const js = stripTypeScriptTypes(source, { mode: 'strip' });
const policy = await import('data:text/javascript;base64,' + Buffer.from(js).toString('base64'));
let passed = 0;
function check(action) { action(); passed++; }
async function checkAsync(action) { await action(); passed++; }

const fixtureText = readFileSync(new URL('./fixtures/stable-manifest-valid.json', import.meta.url), 'utf8');
const envelope = policy.parseSignedEnvelope(fixtureText);
const payload = Buffer.from(envelope.payload, 'base64');
const signature = Buffer.from(envelope.signature, 'base64');
const publicKey = createPublicKey({ key: Buffer.from(policy.STABLE_SIGNER_SPKI_BASE64, 'base64'), format: 'der', type: 'spki' });
check(() => assert.equal(policy.STABLE_MANIFEST_URL, 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json'));
check(() => assert.equal(policy.STABLE_MANIFEST_FALLBACK_URL, 'https://github.com/linjierd/TabLink/releases/latest/download/manifest.json'));
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
check(() => assert.equal(policy.parseStoredUpdateMode(undefined, false), 'automatic'));
check(() => assert.equal(policy.parseStoredUpdateMode('automatic', true), 'automatic'));
check(() => assert.equal(policy.parseStoredUpdateMode('downloadThenAsk', true), 'downloadThenAsk'));
check(() => assert.equal(policy.parseStoredUpdateMode('never', true), 'never'));
for (const damaged of ['', 'auto', 'AUTOMATIC', 1, null]) {
  check(() => assert.equal(policy.parseStoredUpdateMode(damaged, true), 'never'));
}
await checkAsync(async () => {
  const queue = new policy.StableUpdatePreferenceQueue();
  const writes = [];
  let releaseFirst;
  const firstGate = new Promise(resolve => { releaseFirst = resolve; });
  const first = queue.enqueueMode('automatic', async mode => {
    writes.push(`${mode}:start`);
    await firstGate;
    writes.push(`${mode}:finish`);
    return true;
  });
  const second = queue.enqueueMode('never', async mode => {
    writes.push(`${mode}:start`);
    writes.push(`${mode}:finish`);
    return true;
  });
  await Promise.resolve();
  assert.deepEqual(writes, ['automatic:start'], 'a newer mode save must wait behind the active save');
  releaseFirst();
  const firstResult = await first;
  const secondResult = await second;
  assert.deepEqual(writes, ['automatic:start', 'automatic:finish', 'never:start', 'never:finish']);
  assert.equal(queue.isLatestMode(firstResult.sequence), false);
  assert.equal(queue.isLatestMode(secondResult.sequence), true);
  assert.equal(secondResult.mode, 'never');
});
await checkAsync(async () => {
  const queue = new policy.StableUpdatePreferenceQueue();
  const writes = [];
  const failed = queue.enqueueMode('automatic', async mode => { writes.push(mode); throw new Error('disk'); });
  const recovered = queue.enqueueMode('downloadThenAsk', async mode => { writes.push(mode); return true; });
  assert.equal((await failed).saved, false);
  assert.equal((await recovered).saved, true);
  assert.deepEqual(writes, ['automatic', 'downloadThenAsk'], 'a failed save must not poison the serialized queue');
});
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

check(() => {
  const older = policy.parseVerifiedPayload(JSON.stringify({ ...harmonyObject,
    releaseId: 'stable-0.8.9', publishedAtUtc: '2026-09-29T14:34:00Z' }), Date.parse('2026-09-29T15:00:00Z'));
  const paused = policy.parseVerifiedPayload(JSON.stringify({ ...harmonyObject,
    releaseId: 'stable-0.9.0-pause', publishedAtUtc: '2026-09-29T14:36:00Z', rolloutPercentage: 0 }), Date.parse('2026-09-29T15:00:00Z'));
  const selected = policy.selectNewestVerifiedManifest([
    { sourceUrl: policy.STABLE_MANIFEST_URL, payloadText: 'older', manifest: older },
    { sourceUrl: policy.STABLE_MANIFEST_FALLBACK_URL, payloadText: 'paused', manifest: paused }
  ]);
  assert.equal(selected.manifest.releaseId, 'stable-0.9.0-pause');
  assert.equal(selected.manifest.rolloutPercentage, 0);
});
check(() => {
  const first = { sourceUrl: policy.STABLE_MANIFEST_URL, payloadText: 'same', manifest: harmony };
  const second = { sourceUrl: policy.STABLE_MANIFEST_FALLBACK_URL, payloadText: 'same', manifest: harmony };
  assert.equal(policy.selectNewestVerifiedManifest([first, second]).manifest.releaseId, harmony.releaseId);
});
check(() => {
  const withTwoArtifacts = structuredClone(harmonyObject);
  withTwoArtifacts.artifacts.push({ platform: 'android', version: '0.9.0', build: 900,
    url: 'https://blog.example/TabLink.apk', size: 654321, sha256: 'cd'.repeat(32), notes: 'android' });
  const reorderedMirror = structuredClone(withTwoArtifacts);
  reorderedMirror.artifacts.reverse();
  reorderedMirror.artifacts[0].url = 'https://github.example/TabLink.apk';
  reorderedMirror.artifacts[1].url = 'https://github.example/TabLink.hap';
  const blog = policy.parseVerifiedPayload(JSON.stringify(withTwoArtifacts), Date.parse('2026-09-29T15:00:00Z'));
  const github = policy.parseVerifiedPayload(JSON.stringify(reorderedMirror), Date.parse('2026-09-29T15:00:00Z'));
  assert.equal(policy.canonicalStableDecision(blog), policy.canonicalStableDecision(github),
    'artifact order and download-mirror URLs are not signed decision differences');
  assert.equal(policy.selectNewestVerifiedManifest([
    { sourceUrl: policy.STABLE_MANIFEST_URL, payloadText: JSON.stringify(withTwoArtifacts), manifest: blog },
    { sourceUrl: policy.STABLE_MANIFEST_FALLBACK_URL, payloadText: JSON.stringify(reorderedMirror), manifest: github }
  ]).manifest.releaseId, blog.releaseId);
});
check(() => {
  const conflict = policy.parseVerifiedPayload(JSON.stringify({ ...harmonyObject,
    releaseId: 'stable-conflict' }), Date.parse('2026-09-29T15:00:00Z'));
  assert.equal(policy.newestConflictPublishedAt([
    { sourceUrl: policy.STABLE_MANIFEST_URL, payloadText: 'one', manifest: harmony },
    { sourceUrl: policy.STABLE_MANIFEST_FALLBACK_URL, payloadText: 'two', manifest: conflict }
  ]), harmony.publishedAtUtc);
  assert.throws(() => policy.selectNewestVerifiedManifest([
    { sourceUrl: policy.STABLE_MANIFEST_URL, payloadText: 'one', manifest: harmony },
    { sourceUrl: policy.STABLE_MANIFEST_FALLBACK_URL, payloadText: 'two', manifest: conflict }
  ]), /冲突/);
});
check(() => {
  const futureProtocol = policy.parseVerifiedPayload(JSON.stringify({ ...harmonyObject,
    releaseId: 'stable-1.0.0', publishedAtUtc: '2026-09-29T14:36:00Z', minimumProtocolVersion: 2
  }), Date.parse('2026-09-29T15:00:00Z'));
  const selected = policy.selectNewestVerifiedManifest([
    { sourceUrl: policy.STABLE_MANIFEST_URL, payloadText: 'old-installable', manifest: harmony },
    { sourceUrl: policy.STABLE_MANIFEST_FALLBACK_URL, payloadText: 'new-future-protocol', manifest: futureProtocol }
  ]);
  assert.equal(selected.manifest, futureProtocol, 'newer future-protocol decision remains authoritative');
  assert.equal(policy.requiresNewerUpdateProtocol(selected.manifest), true);
  assert.equal(policy.selectHarmonyUpdate(selected.manifest, '0.8.0', 800, true), undefined);
});
check(() => {
  const decision = policy.canonicalStableDecision(harmony);
  const digest = createHash('sha256').update(decision).digest('hex').toUpperCase();
  const floor = policy.parseStoredDecisionFloor(policy.serializeDecisionFloor(harmony.publishedAtUtc, digest));
  assert.equal(floor.blocked, false);
  assert.equal(policy.compareManifestWithFloor(harmony, digest, floor), 0);

  const mirrorObject = structuredClone(harmonyObject);
  mirrorObject.artifacts[0].url = 'https://mirror.example/releases/0.9.0/TabLink.hap';
  const mirror = policy.parseVerifiedPayload(JSON.stringify(mirrorObject), Date.parse('2026-09-29T15:00:00Z'));
  const mirrorDigest = createHash('sha256').update(policy.canonicalStableDecision(mirror)).digest('hex').toUpperCase();
  assert.equal(mirrorDigest, digest, 'anti-replay digest ignores only the package mirror URL');
  assert.equal(policy.compareManifestWithFloor(mirror, mirrorDigest, floor), 0);

  const changedObject = structuredClone(harmonyObject);
  changedObject.artifacts[0].notes = 'changed decision';
  const changed = policy.parseVerifiedPayload(JSON.stringify(changedObject), Date.parse('2026-09-29T15:00:00Z'));
  const changedDigest = createHash('sha256').update(policy.canonicalStableDecision(changed)).digest('hex').toUpperCase();
  assert.throws(() => policy.compareManifestWithFloor(changed, changedDigest, floor), /冲突/);

  const blocked = policy.parseStoredDecisionFloor(policy.serializeConflictFloor(harmony.publishedAtUtc));
  assert.equal(blocked.blocked, true);
  assert.throws(() => policy.compareManifestWithFloor(harmony, digest, blocked), /未晚于/);
  const older = policy.parseVerifiedPayload(JSON.stringify({ ...harmonyObject,
    releaseId: 'older-before-conflict', publishedAtUtc: '2026-09-29T14:34:00Z' }), Date.parse('2026-09-29T15:00:00Z'));
  assert.throws(() => policy.compareManifestWithFloor(older,
    createHash('sha256').update(policy.canonicalStableDecision(older)).digest('hex').toUpperCase(), blocked), /未晚于/);
  const recovered = policy.parseVerifiedPayload(JSON.stringify({ ...harmonyObject,
    releaseId: 'newer-after-conflict', publishedAtUtc: '2026-09-29T14:36:00Z' }), Date.parse('2026-09-29T15:00:00Z'));
  assert.equal(policy.compareManifestWithFloor(recovered,
    createHash('sha256').update(policy.canonicalStableDecision(recovered)).digest('hex').toUpperCase(), blocked), 1);
  const legacyFloor = policy.parseStoredDecisionFloor(JSON.stringify({ schema: 1,
    publishedAtUtc: harmony.publishedAtUtc, decisionSha256: digest }));
  assert.equal(legacyFloor.blocked, false, 'schema-1 floor records written before the blocked marker remain readable');
  assert.throws(() => policy.parseStoredDecisionFloor(JSON.stringify({ schema: 1,
    publishedAtUtc: harmony.publishedAtUtc, decisionSha256: digest, blocked: true })), /损坏/);
});
check(() => {
  const canonical = policy.canonicalStableDecision(harmony);
  const variants = [];
  for (const [field, value] of [['releaseId', 'stable-other'], ['publishedAtUtc', '2026-09-29T14:36:00Z'],
    ['rolloutPercentage', 38], ['minimumProtocolVersion', 2]]) {
    const variant = structuredClone(harmony); variant[field] = value; variants.push(variant);
  }
  for (const [field, value] of [['version', policy.parseStableVersion('0.9.1')], ['build', 901], ['size', 123457],
    ['sha256', 'CD'.repeat(32)], ['installerUrl', 'https://appgallery.huawei.com/app/C654321'], ['notes', 'changed']]) {
    const variant = structuredClone(harmony); variant.artifacts[0][field] = value; variants.push(variant);
  }
  variants.forEach(variant => assert.notEqual(policy.canonicalStableDecision(variant), canonical,
    'every signed decision field except artifact.url must affect the canonical digest'));
});
check(() => {
  const digest = createHash('sha256').update(policy.canonicalStableDecision(harmony)).digest('hex').toUpperCase();
  const olderFloor = policy.parseStoredDecisionFloor(policy.serializeDecisionFloor('2026-09-29T14:34:00Z', '11'.repeat(32)));
  assert.equal(policy.compareManifestWithFloor(harmony, digest, olderFloor), 1);
  const newerFloor = policy.parseStoredDecisionFloor(policy.serializeDecisionFloor('2026-09-29T14:36:00Z', '22'.repeat(32)));
  assert.throws(() => policy.compareManifestWithFloor(harmony, digest, newerFloor), /早于/);
  assert.throws(() => policy.parseStoredDecisionFloor('{"schema":1,"publishedAtUtc":"bad","decisionSha256":"00"}'), /损坏/);
  assert.throws(() => policy.parseStoredDecisionFloor('{"schema":1,"schema":1,"publishedAtUtc":"2026-09-29T14:35:00Z","decisionSha256":"' + '00'.repeat(32) + '"}'), /损坏/);
});
check(() => {
  assert.equal(policy.compareManifestWithLegacyFloor(harmony, '2026-09-29T14:34:00Z', 'stable-0.8.9'), 1);
  assert.throws(() => policy.compareManifestWithLegacyFloor(harmony, harmony.publishedAtUtc, harmony.releaseId), /无法验证/);
  assert.throws(() => policy.compareManifestWithLegacyFloor(harmony, 'damaged', harmony.releaseId), /损坏/);
});

console.log(`PASS ${passed} stable-update policy/signature assertions (pure Node; no Harmony SDK or AppGallery execution).`);
