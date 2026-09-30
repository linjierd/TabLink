export interface DecoderStats {
  submittedFrames: number;
  lastPtsUs: number;
  width: number;
  height: number;
  error: string;
  decoder: string;
}
export const start: (surfaceId: string, width: number, height: number, codecConfig: ArrayBuffer) => void;
export const push: (accessUnit: ArrayBuffer, ptsUs: number) => boolean;
export const stats: () => DecoderStats;
export const stop: () => void;
