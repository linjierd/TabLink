// Platform-independent framing and identity parsing. Tested with Node's
// type-stripper; this does not substitute for an ArkTS / Harmony SDK build.
export const MAX_PAYLOAD = 8 * 1024 * 1024;
export const NATIVE_PORTS: number[] = [27184, 27186, 27187, 27188, 27189, 27190, 27191, 27192];
export class Pairing {
  host: string = '';
  port: number = 0;
  token: string = '';
  cert: string = '';
}
export function parsePairing(input: string): Pairing {
  const text = input.trim();
  if (text.length > 1024 || !text.startsWith('tablink://connect?') || text.includes('#')) {
    throw new Error('请粘贴电脑 TabLink 生成的完整原生客户端连接信息');
  }
  const values = new Map<string, string>();
  for (const part of text.substring(18).split('&')) {
    const equals = part.indexOf('=');
    if (equals < 1) throw new Error('连接参数无效');
    const key = part.substring(0, equals);
    if (values.has(key) || !['host', 'port', 'token', 'cert'].includes(key)) throw new Error('连接参数重复或未知');
    // PC-generated values are ASCII and never need percent decoding. Reject
    // alternate spellings rather than interpreting an encoded second format.
    const value = part.substring(equals + 1);
    if (value.includes('%') || value.includes('=')) throw new Error('连接参数编码无效');
    values.set(key, value);
  }
  const result = new Pairing();
  result.host = values.get('host') ?? '';
  const octets = result.host.split('.');
  if (octets.length !== 4 || octets.some((value: string) => !/^(0|[1-9][0-9]{0,2})$/.test(value) || Number(value) > 255) ||
    Number(octets[0]) === 0 || Number(octets[0]) === 127 || Number(octets[0]) >= 224) throw new Error('需要有效的电脑 IPv4 地址');
  const port = values.get('port') ?? '';
  if (!/^[1-9][0-9]{0,4}$/.test(port) || !NATIVE_PORTS.includes(Number(port))) throw new Error('原生会话端口无效；27185 仅供浏览器使用');
  result.port = Number(port);
  result.token = values.get('token') ?? '';
  result.cert = (values.get('cert') ?? '').toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(result.token) || !/^[0-9a-f]{64}$/.test(result.cert)) throw new Error('配对令牌或证书指纹无效');
  return result;
}
export class Packet {
  type: number = 0;
  payload: Uint8Array = new Uint8Array(0);
}
export function encodePacket(type: number, payload: Uint8Array): ArrayBuffer {
  if (!Number.isInteger(type) || type < 0 || type > 255 || payload.length < 1 || payload.length > MAX_PAYLOAD) throw new Error('发送的数据包无效');
  const bytes = new Uint8Array(payload.length + 5);
  bytes[0] = type;
  new DataView(bytes.buffer).setUint32(1, payload.length, false);
  bytes.set(payload, 5);
  return bytes.buffer;
}
export class PacketReader {
  private header: Uint8Array = new Uint8Array(5);
  private headerSize: number = 0;
  private payload: Uint8Array = new Uint8Array(0);
  private payloadSize: number = 0;
  feed(chunk: ArrayBuffer, consume: (packet: Packet) => void): void {
    const incoming = new Uint8Array(chunk);
    if (incoming.length > MAX_PAYLOAD * 2) throw new Error('接收缓冲区超过限制');
    let offset = 0;
    while (offset < incoming.length) {
      if (this.headerSize < 5) {
        const count = Math.min(5 - this.headerSize, incoming.length - offset);
        this.header.set(incoming.subarray(offset, offset + count), this.headerSize);
        this.headerSize += count; offset += count;
        if (this.headerSize < 5) continue;
        const length = new DataView(this.header.buffer).getUint32(1, false);
        if (length < 1 || length > MAX_PAYLOAD) throw new Error('视频数据包长度无效');
        this.payload = new Uint8Array(length); this.payloadSize = 0;
      }
      const count = Math.min(this.payload.length - this.payloadSize, incoming.length - offset);
      this.payload.set(incoming.subarray(offset, offset + count), this.payloadSize);
      this.payloadSize += count; offset += count;
      if (this.payloadSize === this.payload.length) {
        const packet = new Packet(); packet.type = this.header[0]; packet.payload = this.payload;
        this.headerSize = 0; this.payload = new Uint8Array(0); this.payloadSize = 0;
        consume(packet);
      }
    }
  }
}
export function annexBNalTypes(bytes: Uint8Array): number[] {
  if (bytes.length < 4 || bytes.length > MAX_PAYLOAD) throw new Error('H.264 访问单元长度无效');
  const types: number[] = [];
  let nalStart = -1;
  for (let index = 0; index < bytes.length; index++) {
    let prefix = 0;
    if (index + 2 < bytes.length && bytes[index] === 0 && bytes[index + 1] === 0) {
      if (bytes[index + 2] === 1) prefix = 3;
      else if (index + 3 < bytes.length && bytes[index + 2] === 0 && bytes[index + 3] === 1) prefix = 4;
    }
    if (prefix === 0) { if (nalStart === -1) throw new Error('H.264 缺少 Annex-B 起始码'); continue; }
    if (nalStart === index || index + prefix >= bytes.length) throw new Error('H.264 单元为空或被截断');
    nalStart = index + prefix;
    const type = bytes[nalStart] & 31;
    if ((bytes[nalStart] & 128) !== 0 || type < 1 || type > 23) throw new Error('H.264 单元头无效');
    types.push(type);
    if (types.length > 4096) throw new Error('H.264 单元过多');
    index += prefix - 1;
  }
  if (types.length === 0) throw new Error('H.264 没有可读取的单元');
  return types;
}
export function validateParameterSets(sps: Uint8Array, pps: Uint8Array): void {
  if (sps.length > 65536 || pps.length > 65536) throw new Error('H.264 参数集过大');
  const first = annexBNalTypes(sps), second = annexBNalTypes(pps);
  if (first.length !== 1 || first[0] !== 7 || second.length !== 1 || second[0] !== 8) throw new Error('H.264 SPS/PPS 参数类型无效');
}
export function validateAccessUnit(payload: Uint8Array): number {
  const pts = readPts(payload);
  const types = annexBNalTypes(payload.subarray(8));
  if (!types.includes(1) && !types.includes(5)) throw new Error('H.264 视频包没有图像');
  return pts;
}
export function hasFreshOutput(submittedFrames: number, lastPtsUs: number): boolean {
  return Number.isSafeInteger(submittedFrames) && submittedFrames > 0 && Number.isSafeInteger(lastPtsUs) && lastPtsUs >= 0;
}
export function readPts(payload: Uint8Array): number {
  if (payload.length < 9) throw new Error('视频帧过短');
  const view = new DataView(payload.buffer, payload.byteOffset, payload.byteLength);
  const high = view.getUint32(0, false);
  const low = view.getUint32(4, false);
  const value = high * 4294967296 + low;
  if (!Number.isSafeInteger(value)) throw new Error('视频时间戳超出范围');
  return value;
}
export function samePin(actual: string, expected: string): boolean {
  if (actual.length !== 64 || expected.length !== 64) return false;
  let mismatch = 0;
  for (let index = 0; index < 64; index++) mismatch |= actual.charCodeAt(index) ^ expected.charCodeAt(index);
  return mismatch === 0;
}
