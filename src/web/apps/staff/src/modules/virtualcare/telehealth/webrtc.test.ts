import { describe, expect, it } from 'vitest';
import {
  classifyQuality,
  computeQuality,
  decodeCandidate,
  decodeDescription,
  describeQuality,
  encodeCandidate,
  snapshotFromStats,
  type StatsSnapshot,
} from './webrtc';

describe('signalling payloads', () => {
  const init = {
    candidate: 'candidate:1 1 udp 2122260223 192.168.1.20 54400 typ host',
    sdpMid: '0',
    sdpMLineIndex: 0,
    usernameFragment: 'abcd',
  };

  it('round-trips an ICE candidate as JSON', () => {
    expect(decodeCandidate(encodeCandidate(init))).toEqual(init);
  });

  it('accepts a bare candidate line from another client', () => {
    expect(decodeCandidate(init.candidate)).toEqual({ candidate: init.candidate, sdpMLineIndex: 0 });
    expect(decodeCandidate(`a=${init.candidate}`)).toEqual({ candidate: init.candidate, sdpMLineIndex: 0 });
  });

  it('gives a media line to candidates without one and rejects rubbish', () => {
    expect(decodeCandidate(JSON.stringify({ candidate: init.candidate }))?.sdpMLineIndex).toBe(0);
    expect(decodeCandidate('{"sdpMid":"0"}')).toBeNull();
    expect(decodeCandidate('not json')).toBeNull();
    expect(decodeCandidate(42)).toBeNull();
  });

  it('reads SDP whether sent raw or as a description object', () => {
    const sdp = 'v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\n';
    expect(decodeDescription(sdp)).toBe(sdp);
    expect(decodeDescription(JSON.stringify({ type: 'offer', sdp }))).toBe(sdp);
    expect(decodeDescription({ type: 'answer', sdp })).toBe(sdp);
    expect(decodeDescription('{"type":"offer"}')).toBeNull();
    expect(decodeDescription('hello')).toBeNull();
  });
});

describe('connection quality', () => {
  const report = (at: number, bytes: number, received: number, lost: number, rtt = 0.08) => [
    { type: 'transport', id: 't', selectedCandidatePairId: 'cp1' },
    { type: 'candidate-pair', id: 'cp0', state: 'failed', currentRoundTripTime: 2 },
    {
      type: 'candidate-pair',
      id: 'cp1',
      state: 'succeeded',
      nominated: true,
      currentRoundTripTime: rtt,
      availableOutgoingBitrate: 1_500_000,
      timestamp: at,
    },
    {
      type: 'inbound-rtp',
      kind: 'audio',
      bytesReceived: bytes / 10,
      packetsReceived: (received * 3) / 10,
      packetsLost: (lost * 3) / 10,
      timestamp: at,
    },
    {
      type: 'inbound-rtp',
      kind: 'video',
      bytesReceived: (bytes * 9) / 10,
      packetsReceived: (received * 7) / 10,
      packetsLost: (lost * 7) / 10,
      framesPerSecond: 24,
      timestamp: at,
    },
  ];

  it('reads the selected candidate pair and sums incoming streams', () => {
    const snapshot = snapshotFromStats(report(10_000, 100_000, 1_000, 10));
    expect(snapshot).toEqual({
      at: 10_000,
      rttMs: 80,
      availableOutgoingKbps: 1500,
      bytesReceived: 100_000,
      packetsReceived: 1_000,
      packetsLost: 10,
      videoFps: 24,
    });
  });

  it('measures loss and bitrate over the interval', () => {
    const before = snapshotFromStats(report(10_000, 100_000, 1_000, 10));
    const after = snapshotFromStats(report(15_000, 475_000, 2_000, 30));
    // 375 000 bytes in 5 s = 600 kbps; 20 lost of 1 020 packets = 2 %.
    expect(computeQuality(before, after, true)).toEqual({ level: 'good', rttMs: 80, lossPercent: 2, bitrateKbps: 600 });
  });

  it('has no loss or bitrate on the first sample', () => {
    const only: StatsSnapshot = snapshotFromStats(report(10_000, 100_000, 1_000, 10));
    expect(computeQuality(null, only, true)).toEqual({
      level: 'good',
      rttMs: 80,
      lossPercent: null,
      bitrateKbps: null,
    });
  });

  it('grades delay, loss and bitrate', () => {
    expect(classifyQuality({ rttMs: 90, lossPercent: 0.5, bitrateKbps: 900 }, true)).toBe('good');
    expect(classifyQuality({ rttMs: 250, lossPercent: 0, bitrateKbps: 900 }, true)).toBe('fair');
    expect(classifyQuality({ rttMs: 90, lossPercent: 3, bitrateKbps: 900 }, true)).toBe('fair');
    expect(classifyQuality({ rttMs: 90, lossPercent: 0, bitrateKbps: 300 }, true)).toBe('fair');
    expect(classifyQuality({ rttMs: 450, lossPercent: 0, bitrateKbps: 900 }, true)).toBe('poor');
    expect(classifyQuality({ rttMs: 90, lossPercent: 8, bitrateKbps: 900 }, true)).toBe('poor');
    expect(classifyQuality({ rttMs: 90, lossPercent: 0, bitrateKbps: 100 }, true)).toBe('poor');
    // Audio-only calls need far less bandwidth.
    expect(classifyQuality({ rttMs: 90, lossPercent: 0, bitrateKbps: 40 }, false)).toBe('good');
    expect(classifyQuality({ rttMs: null, lossPercent: null, bitrateKbps: null }, true)).toBe('good');
  });

  it('describes a reading in plain words', () => {
    expect(describeQuality(null)).toMatch(/Measuring/);
    expect(describeQuality({ level: 'poor', rttMs: 450, lossPercent: 6.5, bitrateKbps: 120 })).toBe(
      'Poor connection: delay 450 ms, 6.5% lost, 120 kbps',
    );
  });
});
