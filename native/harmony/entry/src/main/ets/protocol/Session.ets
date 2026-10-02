import { socket } from '@kit.NetworkKit';
import { cryptoFramework } from '@kit.CryptoArchitectureKit';
import { util } from '@kit.ArkTS';
import { BusinessError } from '@kit.BasicServicesKit';
import codec, { DecoderStats } from 'libtablink_codec.so';
import { Packet, PacketReader, Pairing, encodePacket, parsePairing, samePin, validateAccessUnit, validateParameterSets, hasFreshOutput } from './Wire';
import { DisplayProfile, readDisplayProfile } from './DisplayProfile';

interface VideoConfig { codec: string; width: number; height: number; fps: number; csd0: string; csd1: string; }
interface HostMessage { message: string; capturePaused?: boolean; protocol?: number; features?: string[]; }
interface Hello { protocol: number; token: string; features: string[]; }
interface SubmittedProgress { width: number; height: number; ptsUs: number; frames: number; fps: number; decoder: string; evidence: string; }
interface LocalizedHostStatus { messageKey: string; arguments_: string[]; }

function localizeKnownHostStatus(message: string, capturePaused: boolean): LocalizedHostStatus {
  const safe = message.replace(/[\u0000-\u001f\u007f-\u009f]/g, ' ').trim().substring(0, 180);
  if (safe === 'Windows 桌面暂不可采集，等待返回普通桌面。' ||
    safe === '画面采集暂时不可用，正在等待恢复。') {
    return { messageKey: 'session_capture_unavailable', arguments_: [] };
  }
  if (safe === '正在恢复同一虚拟副屏画面。') {
    return { messageKey: 'session_capture_recovering', arguments_: [] };
  }
  if (safe === 'USB 副屏画面已恢复。') {
    return { messageKey: 'session_capture_resumed', arguments_: [] };
  }
  return {
    messageKey: capturePaused ? 'session_capture_paused_remote' : 'session_host_status_remote',
    arguments_: [safe]
  };
}

export class Session {
  private tls: socket.TLSSocket | undefined = undefined;
  private connected: boolean = false;
  private stopped: boolean = false;
  private configured: boolean = false;
  private reader: PacketReader = new PacketReader();
  private encoder: util.TextEncoder = new util.TextEncoder();
  private textDecoder: util.TextDecoder = util.TextDecoder.create('utf-8', { fatal: true });
  private writeChain: Promise<void> = Promise.resolve();
  private timer: number = -1;
  private deadline: number = -1;
  private reporting: boolean = false;
  private surfaceId: string;
  private state: (messageKey: string, arguments_: string[], active: boolean) => void;
  private profile: DisplayProfile = new DisplayProfile();
  private frameBase: number = 0;
  private reportedFrames: number = 0;
  private reportTime: number = 0;
  private lastProgressTime: number = 0;
  private lastIncomingPts: number = -1;
  private configWidth: number = 0;
  private configHeight: number = 0;
  private capturePaused: boolean = false;
  private submissionAckNegotiated: boolean = false;

  constructor(surfaceId: string, state: (messageKey: string, arguments_: string[], active: boolean) => void) {
    this.surfaceId = surfaceId; this.state = state;
  }
  async connect(uri: string): Promise<void> {
    let pairing: Pairing;
    try { pairing = parsePairing(uri); this.profile = readDisplayProfile(); }
    catch (error) { this.fail('session_invalid_data'); throw error; }
    const tls = socket.constructTLSSocketInstance();
    this.tls = tls;
    this.deadline = setTimeout(() => this.fail('session_timeout'), 30000);
    try {
      await tls.bind({ address: '0.0.0.0', port: 0, family: 1 });
      this.checkOpen();
      tls.on('message', (message: socket.SocketMessageInfo) => {
        if (!this.connected || this.stopped) return;
        try { this.reader.feed(message.message, (packet: Packet) => this.receive(packet)); }
        catch (_error) { this.fail('session_invalid_data'); }
      });
      tls.on('error', (_error: BusinessError) => this.fail('session_network_error'));
      tls.on('close', () => this.fail('session_host_ended'));
      this.state('session_connecting', [], true);
      await tls.connect({
        address: { address: pairing.host, port: pairing.port, family: 1 },
        secureOptions: { protocols: [socket.Protocol.TLSv12, socket.Protocol.TLSv13] },
        // Self-signed bootstrap only: no application token or profile is sent
        // until the DER leaf hash matches the explicit, trusted pairing URI.
        skipRemoteValidation: true
      });
      this.checkOpen();
      const certificate = await tls.getRemoteCertificate();
      this.checkOpen();
      let der = certificate.data;
      if (der.length < 1 || der.length > 65536) throw new Error('电脑证书数据无效');
      if (der[0] === 45) {
        const pem = this.textDecoder.decodeToString(der);
        if (!pem.startsWith('-----BEGIN CERTIFICATE-----') || pem.indexOf('-----BEGIN CERTIFICATE-----', 1) >= 0) throw new Error('电脑证书格式无效');
        const body = pem.replace('-----BEGIN CERTIFICATE-----', '').replace('-----END CERTIFICATE-----', '').replace(/\s/g, '');
        der = new util.Base64Helper().decodeSync(body);
      }
      if (der[0] !== 0x30) throw new Error('电脑证书不是 DER X.509 数据');
      const digest = cryptoFramework.createMd('SHA256');
      await digest.update({ data: der });
      const hash = await digest.digest();
      this.checkOpen();
      let hex = '';
      for (const value of hash.data) hex += value.toString(16).padStart(2, '0');
      if (!samePin(hex, pairing.cert)) { this.fail('session_pin_mismatch'); throw new Error('certificate pin mismatch'); }
      this.connected = true;
      const hello: Hello = { protocol: 1, token: pairing.token, features: ['render-submitted-v1'] };
      await this.sendJson(0x10, JSON.stringify(hello));
      pairing.token = '';
      await this.sendJson(0x13, JSON.stringify(this.profile));
      this.checkOpen();
      this.state('session_secure_ready', [], true);
      this.timer = setInterval(() => { this.report().catch((_error: Error) => this.fail('session_decoder_error')); }, 250);
    } catch (error) { pairing.token = ''; if (!this.stopped) this.fail('session_invalid_data'); throw error; }
  }
  stop(messageKey: string = 'status_stopped', arguments_: string[] = []): void {
    if (this.stopped) return;
    this.stopped = true; this.connected = false; this.submissionAckNegotiated = false;
    if (this.timer !== -1) clearInterval(this.timer);
    if (this.deadline !== -1) clearTimeout(this.deadline);
    codec.stop(); this.configured = false; this.reader = new PacketReader();
    const tls = this.tls; this.tls = undefined;
    if (tls) { tls.close().catch((_error: BusinessError) => {}); }
    this.state(messageKey, arguments_, false);
  }
  private checkOpen(): void { if (this.stopped) throw new Error('连接已停止'); }
  private fail(messageKey: string, arguments_: string[] = []): void { this.stop(messageKey, arguments_); }
  private sendJson(type: number, text: string): Promise<void> {
    const bytes = encodePacket(type, this.encoder.encodeInto(text));
    this.writeChain = this.writeChain.then(async () => {
      this.checkOpen(); const tls = this.tls;
      if (!tls || !this.connected) throw new Error('连接已关闭');
      await tls.send(bytes);
    });
    return this.writeChain;
  }
  private receive(packet: Packet): void {
    this.checkOpen();
    if (packet.type === 0x03) {
      this.fail('session_host_rejected');
    } else if (packet.type === 0x02) {
      if (packet.payload.length > 16384) throw new Error('电脑状态数据过大');
      const status = JSON.parse(this.textDecoder.decodeToString(packet.payload)) as HostMessage;
      if (typeof status.message !== 'string') throw new Error('电脑状态格式无效');
      if (status.protocol === 1 && Array.isArray(status.features) && status.features.includes('render-submitted-v1')) {
        this.submissionAckNegotiated = true;
      }
      if (typeof status.capturePaused === 'boolean') {
        this.capturePaused = status.capturePaused;
        this.lastProgressTime = Date.now();
      }
      if (this.capturePaused || !this.configured) {
        const visible = localizeKnownHostStatus(status.message, this.capturePaused);
        this.state(visible.messageKey, visible.arguments_, true);
      }
    } else if (packet.type === 0x20) {
      if (packet.payload.length > 131072) throw new Error('视频配置过大');
      const config = JSON.parse(this.textDecoder.decodeToString(packet.payload)) as VideoConfig;
      if (config.codec !== 'video/avc' || !Number.isInteger(config.width) || !Number.isInteger(config.height) ||
        config.width !== this.profile.width || config.height !== this.profile.height ||
        !Number.isFinite(config.fps) || config.fps < 1 || config.fps > 240 || config.width * config.height > 16000000 ||
        typeof config.csd0 !== 'string' || typeof config.csd1 !== 'string') throw new Error('电脑视频配置与当前全屏像素不符');
      const base64 = new util.Base64Helper();
      const sps = base64.decodeSync(config.csd0); const pps = base64.decodeSync(config.csd1);
      validateParameterSets(sps, pps);
      const csd = new Uint8Array(sps.length + pps.length); csd.set(sps); csd.set(pps, sps.length);
      if (this.configured) this.frameBase += codec.stats().submittedFrames;
      codec.start(this.surfaceId, config.width, config.height, csd.buffer);
      this.configured = true; this.configWidth = config.width; this.configHeight = config.height;
      this.lastProgressTime = Date.now();
      this.state('session_decoder_started', [], true);
    } else if (packet.type === 0x21) {
      if (!this.configured) throw new Error('收到视频帧前未收到解码配置');
      const pts = validateAccessUnit(packet.payload);
      if (pts <= this.lastIncomingPts) throw new Error('视频 PTS 未递增');
      this.lastIncomingPts = pts;
      if (!codec.push(packet.payload.slice(8).buffer, pts)) throw new Error('视频解码队列已满，已停止以避免画面持续积压');
    } else throw new Error('电脑发来了不支持的数据包类型');
  }
  private async report(): Promise<void> {
    if (this.stopped || !this.configured || this.reporting || !this.submissionAckNegotiated) return;
    this.reporting = true;
    try {
      const stats: DecoderStats = codec.stats();
      if (stats.error.length > 0) { this.fail('session_decoder_error'); return; }
      if (this.capturePaused) return; // No synthetic progress while the PC cannot capture.
      const total = this.frameBase + stats.submittedFrames;
      const now = Date.now();
      // A reconfiguration may add old submitted frames to frameBase while the
      // new decoder still has lastPtsUs=-1. Never report an invented new PTS.
      if (!hasFreshOutput(stats.submittedFrames, stats.lastPtsUs)) {
        if (now - this.lastProgressTime > 15000) { this.fail('session_decoder_no_output'); return; }
        return;
      }
      if (total === this.reportedFrames) {
        if (now - this.lastProgressTime > 15000) { this.fail('session_decoder_stalled'); return; }
        return;
      }
      const fps = this.reportTime === 0 ? 0 : Math.min(240, (total - this.reportedFrames) * 1000 / Math.max(1, now - this.reportTime));
      const progress: SubmittedProgress = {
        width: this.configWidth, height: this.configHeight, ptsUs: stats.lastPtsUs, frames: total,
        fps: fps, decoder: 'Harmony AVCodec', evidence: 'render-submitted'
      };
      // 0x14 is deliberately separate from 0x12 (actual presentation ACK).
      await this.sendJson(0x14, JSON.stringify(progress));
      this.checkOpen();
      this.reportedFrames = total; this.reportTime = now; this.lastProgressTime = now;
      if (this.deadline !== -1) { clearTimeout(this.deadline); this.deadline = -1; }
      this.state('session_progress', [fps.toFixed(1)], true);
    } finally { this.reporting = false; }
  }
}
