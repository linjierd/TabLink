import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';

const source = readFileSync(new URL('../entry/src/main/ets/protocol/Wire.ts', import.meta.url), 'utf8');
const js = stripTypeScriptTypes(source, { mode: 'strip' });
const wire = await import('data:text/javascript;base64,' + Buffer.from(js).toString('base64'));
let passed = 0;
function check(action) { action(); passed++; }
const token = 'ab'.repeat(32), cert = 'cd'.repeat(32);
const good = `tablink://connect?host=192.168.1.20&port=27186&token=${token}&cert=${cert}`;
check(() => assert.equal(wire.parsePairing(good).port, 27186));
check(() => assert.equal(wire.parsePairing(good).cert, cert));
for (const port of [27184, 27186, 27187, 27188, 27189, 27190, 27191, 27192]) {
  check(() => assert.equal(wire.parsePairing(good.replace('27186', String(port))).port, port));
}
for (const port of [0, 1, 27183, 27185, 27193, 65535, 65536, -1, '027186', '+27186', '27186.0']) {
  check(() => assert.throws(() => wire.parsePairing(good.replace('27186', String(port)))));
}
for (const invalid of [good + '&host=1.2.3.4', good + '#fragment', good.replace('192.168.1.20', '127.0.0.1'),
  good.replace('192.168.1.20', '192.168.01.20'), good.replace('192.168.1.20', '224.1.2.3'),
  good.replace('192.168.1.20', '256.1.2.3'), good.replace('27186', '65536'),
  good.replace(token, 'no-secret'), good.replace('tablink:', 'http:'), good + '&extra=1']) {
  check(() => assert.throws(() => wire.parsePairing(invalid)));
}
for (const invalid of [good + '&', good.replace('192.168.1.20', '::1'), good.replace('192.168.1.20', '0.1.2.3'),
  good.replace('192.168.1.20', '192.168.1.%32%30'), good.replace('connect?', 'connect/?'),
  good.replace('connect?', 'user@connect?'), good.replace(token, token.toUpperCase()), good.replace(token, token.slice(1)),
  good.replace(cert, 'z'.repeat(64)), good + '&port=27187']) {
  check(() => assert.throws(() => wire.parsePairing(invalid)));
}
check(() => assert(wire.samePin(cert, cert)));
check(() => assert(!wire.samePin(cert, token)));
check(() => assert(!wire.samePin('', '')));
const frame = wire.encodePacket(0x21, Uint8Array.from([1, 2, 3, 4]));
check(() => assert.deepEqual([...new Uint8Array(frame)], [0x21, 0, 0, 0, 4, 1, 2, 3, 4]));
for (let split = 0; split <= frame.byteLength; split++) {
  check(() => {
    const reader = new wire.PacketReader(), packets = [];
    reader.feed(frame.slice(0, split), packet => packets.push(packet));
    reader.feed(frame.slice(split), packet => packets.push(packet));
    assert.equal(packets.length, 1);
    assert.deepEqual([...packets[0].payload], [1, 2, 3, 4]);
  });
}
check(() => {
  const reader = new wire.PacketReader(), packets = [];
  const joined = new Uint8Array(frame.byteLength * 3);
  joined.set(new Uint8Array(frame)); joined.set(new Uint8Array(frame), frame.byteLength); joined.set(new Uint8Array(frame), 2 * frame.byteLength);
  for (const byte of joined) reader.feed(Uint8Array.of(byte).buffer, packet => packets.push(packet));
  assert.equal(packets.length, 3);
});
for (const length of [0, wire.MAX_PAYLOAD + 1, 0xffffffff]) {
  check(() => {
    const input = new Uint8Array(5); new DataView(input.buffer).setUint32(1, length, false);
    assert.throws(() => new wire.PacketReader().feed(input.buffer, () => {}));
  });
}
check(() => assert.throws(() => wire.encodePacket(0x14, new Uint8Array(0))));
check(() => assert.throws(() => wire.encodePacket(256, Uint8Array.of(1))));
check(() => { const p = new Uint8Array(9); new DataView(p.buffer).setUint32(4, 123456, false); assert.equal(wire.readPts(p), 123456); });
check(() => assert.throws(() => wire.readPts(new Uint8Array(8))));
check(() => assert.throws(() => wire.readPts(new Uint8Array(9).fill(255))));
check(() => {
  const body = new Uint8Array(2 * 1024 * 1024); body[body.length - 1] = 17;
  const big = new Uint8Array(wire.encodePacket(0x21, body));
  const reader = new wire.PacketReader(), received = [];
  for (let offset = 0; offset < big.length; offset += 8191) reader.feed(big.slice(offset, offset + 8191).buffer, p => received.push(p));
  assert.equal(received.length, 1); assert.deepEqual(received[0].payload, body);
});
check(() => {
  const reader = new wire.PacketReader(), received = [];
  reader.feed(frame.slice(0, 5), p => received.push(p));
  reader.feed(new ArrayBuffer(0), p => received.push(p));
  reader.feed(frame.slice(5), p => received.push(p));
  assert.equal(received.length, 1);
});
const accessUnit = Uint8Array.from([0, 0, 0, 0, 0, 0, 3, 232, 0, 0, 0, 1, 0x65, 0x88, 0, 0, 1, 0x41, 0x99]);
check(() => assert.equal(wire.validateAccessUnit(accessUnit), 1000));
check(() => assert.deepEqual(wire.annexBNalTypes(accessUnit.slice(8)), [5, 1]));
for (const invalid of [[0, 0, 0, 1], [0, 0, 1, 0x65, 0, 0, 1], [0, 0, 1, 0x80], [1, 2, 3], [0, 0, 1, 0]]) {
  check(() => assert.throws(() => wire.annexBNalTypes(Uint8Array.from(invalid))));
}
const sps = Uint8Array.from([0, 0, 0, 1, 0x67, 0x64, 0, 0x1f]), pps = Uint8Array.from([0, 0, 1, 0x68, 0xee, 3, 0x80]);
check(() => assert.doesNotThrow(() => wire.validateParameterSets(sps, pps)));
check(() => assert.throws(() => wire.validateParameterSets(pps, sps)));
check(() => assert.throws(() => wire.validateParameterSets(new Uint8Array(65537), pps)));
check(() => assert.throws(() => wire.validateAccessUnit(Uint8Array.from([...new Uint8Array(8), ...sps]))));
for (const [frames, pts, expected] of [[0, -1, false], [0, 0, false], [1, -1, false], [1, 0, true], [10, 123, true], [NaN, 123, false], [1, Infinity, false]]) {
  check(() => assert.equal(wire.hasFreshOutput(frames, pts), expected));
}
console.log(`PASS ${passed} protocol assertions (pure TypeScript; no Harmony build or device execution).`);
