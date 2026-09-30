/**
 * Pure helpers for the video call: the signalling payload format and connection-quality maths (TEL-05).
 *
 * Signalling payloads (strings relayed by the hub's SendOffer / SendAnswer / SendIceCandidate):
 *  - offer and answer: the SDP text itself (the hub method already says which it is);
 *  - ICE candidate: JSON of `RTCIceCandidateInit` ({ candidate, sdpMid, sdpMLineIndex, usernameFragment }).
 * Decoding is lenient (a JSON description or a bare "candidate:" line is accepted too), so the patient portal and
 * the staff console interoperate even if one side sends the other shape.
 *
 * Copied from the staff console (`apps/staff/src/modules/virtualcare/telehealth/webrtc.ts`) so both ends share one
 * wire format; the English `describeQuality` stays there (the portal words quality in the patient's language).
 * Both copies should move into `@hc360/core` (reported).
 */

export function encodeCandidate(candidate: RTCIceCandidateInit): string {
  return JSON.stringify({
    candidate: candidate.candidate ?? '',
    sdpMid: candidate.sdpMid ?? null,
    sdpMLineIndex: candidate.sdpMLineIndex ?? null,
    usernameFragment: candidate.usernameFragment ?? null,
  });
}

export function decodeCandidate(data: unknown): RTCIceCandidateInit | null {
  let value: unknown = data;
  if (typeof data === 'string') {
    const text = data.trim();
    if (text.startsWith('candidate:') || text.startsWith('a=candidate:')) {
      return { candidate: text.replace(/^a=/, ''), sdpMLineIndex: 0 };
    }
    try {
      value = JSON.parse(text);
    } catch {
      return null;
    }
  }
  if (!value || typeof value !== 'object') {
    return null;
  }
  const init = value as Record<string, unknown>;
  if (typeof init.candidate !== 'string') {
    return null;
  }
  const sdpMid = typeof init.sdpMid === 'string' ? init.sdpMid : null;
  const sdpMLineIndex = typeof init.sdpMLineIndex === 'number' ? init.sdpMLineIndex : null;
  return {
    candidate: init.candidate,
    sdpMid,
    // addIceCandidate needs a media line; the first one is the right guess for a bare candidate.
    sdpMLineIndex: sdpMid === null && sdpMLineIndex === null ? 0 : sdpMLineIndex,
    usernameFragment: typeof init.usernameFragment === 'string' ? init.usernameFragment : null,
  };
}

/** SDP text from a relayed offer/answer (raw SDP, or JSON `{ type, sdp }`). */
export function decodeDescription(data: unknown): string | null {
  if (typeof data === 'string') {
    const text = data.trim();
    if (text.startsWith('v=')) {
      return data;
    }
    try {
      return decodeDescription(JSON.parse(text) as unknown);
    } catch {
      return null;
    }
  }
  if (data && typeof data === 'object' && typeof (data as { sdp?: unknown }).sdp === 'string') {
    const sdp = (data as { sdp: string }).sdp;
    return sdp.trim().startsWith('v=') ? sdp : null;
  }
  return null;
}

// ------------------------------------------------------------------ connection quality

/** The totals needed from one `getStats()` report. */
export interface StatsSnapshot {
  /** Report time in milliseconds. */
  at: number;
  rttMs: number | null;
  availableOutgoingKbps: number | null;
  bytesReceived: number;
  packetsReceived: number;
  packetsLost: number;
  /** Frames per second of the incoming video, when video is flowing. */
  videoFps: number | null;
}

type StatLike = Record<string, unknown>;

const num = (value: unknown): number | null => (typeof value === 'number' && Number.isFinite(value) ? value : null);

/** Reduces an `RTCStatsReport` (or any iterable of stat records) to the numbers the quality check needs. */
export function snapshotFromStats(stats: Iterable<StatLike>): StatsSnapshot {
  const all = [...stats];
  const selectedPairId = all.find(
    (s) => s.type === 'transport' && typeof s.selectedCandidatePairId === 'string',
  )?.selectedCandidatePairId;
  const pair =
    all.find((s) => s.type === 'candidate-pair' && s.id === selectedPairId) ??
    all.find(
      (s) => s.type === 'candidate-pair' && s.state === 'succeeded' && (s.nominated === true || s.selected === true),
    ) ??
    all.find((s) => s.type === 'candidate-pair' && s.state === 'succeeded');

  let bytesReceived = 0;
  let packetsReceived = 0;
  let packetsLost = 0;
  let videoFps: number | null = null;
  let at = num(pair?.timestamp) ?? 0;
  for (const s of all) {
    if (s.type !== 'inbound-rtp') {
      continue;
    }
    bytesReceived += num(s.bytesReceived) ?? 0;
    packetsReceived += num(s.packetsReceived) ?? 0;
    packetsLost += Math.max(0, num(s.packetsLost) ?? 0);
    if (s.kind === 'video' || s.mediaType === 'video') {
      videoFps = num(s.framesPerSecond) ?? videoFps;
    }
    at = Math.max(at, num(s.timestamp) ?? 0);
  }

  const rtt = num(pair?.currentRoundTripTime);
  const outgoing = num(pair?.availableOutgoingBitrate);
  return {
    at,
    rttMs: rtt === null ? null : Math.round(rtt * 1000),
    availableOutgoingKbps: outgoing === null ? null : Math.round(outgoing / 1000),
    bytesReceived,
    packetsReceived,
    packetsLost,
    videoFps,
  };
}

export type QualityLevel = 'good' | 'fair' | 'poor';

export interface QualityReading {
  level: QualityLevel;
  rttMs: number | null;
  /** Share of incoming packets lost since the previous sample. */
  lossPercent: number | null;
  /** Incoming bitrate since the previous sample. */
  bitrateKbps: number | null;
}

/**
 * Rates the connection from round-trip time, packet loss and incoming bitrate. Thresholds suit a consultation:
 * speech degrades past ~400 ms round trip or 5 % loss, and video below ~150 kbps is barely usable.
 */
export function classifyQuality(sample: Omit<QualityReading, 'level'>, expectVideo: boolean): QualityLevel {
  const { rttMs, lossPercent, bitrateKbps } = sample;
  const lowBitrate = (limit: number) => bitrateKbps !== null && bitrateKbps < limit;
  if ((rttMs ?? 0) > 400 || (lossPercent ?? 0) > 5 || (expectVideo ? lowBitrate(150) : lowBitrate(20))) {
    return 'poor';
  }
  if ((rttMs ?? 0) > 200 || (lossPercent ?? 0) > 2 || (expectVideo && lowBitrate(350))) {
    return 'fair';
  }
  return 'good';
}

/** Quality between two snapshots (loss and bitrate are measured over the interval, not since the call began). */
export function computeQuality(
  previous: StatsSnapshot | null,
  current: StatsSnapshot,
  expectVideo: boolean,
): QualityReading {
  let lossPercent: number | null = null;
  let bitrateKbps: number | null = null;
  if (previous) {
    const lost = Math.max(0, current.packetsLost - previous.packetsLost);
    const received = Math.max(0, current.packetsReceived - previous.packetsReceived);
    if (lost + received > 0) {
      lossPercent = Math.round((lost / (lost + received)) * 1000) / 10;
    }
    const elapsedMs = current.at - previous.at;
    if (elapsedMs > 0 && current.bytesReceived >= previous.bytesReceived) {
      bitrateKbps = Math.round(((current.bytesReceived - previous.bytesReceived) * 8) / elapsedMs);
    }
  }
  const sample = { rttMs: current.rttMs, lossPercent, bitrateKbps };
  return { ...sample, level: classifyQuality(sample, expectVideo) };
}
