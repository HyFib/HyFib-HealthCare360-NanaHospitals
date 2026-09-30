import { auth, createHubClient, hubErrorMessage, useSession, type HubClient } from '@hc360/core';
import type {
  IceServerDto,
  TelehealthJoinResult,
  TelehealthMessageDto,
  TelehealthRole,
  TelehealthStatus,
} from '@/lib/types';
import { canFallBackToAudio, classifyMediaError, type MediaProblem } from './media';
import {
  computeQuality,
  decodeCandidate,
  decodeDescription,
  encodeCandidate,
  snapshotFromStats,
  type QualityReading,
  type StatsSnapshot,
} from './webrtc';

/**
 * The patient's side of a 1:1 video consultation (TEL-02/03/05, PP-04): the waiting room, then a peer-to-peer WebRTC
 * call with the doctor (media never passes through the hospital server), chat, audio-only fallback and connection
 * quality reports.
 *
 * Signalling contract with the staff console (hub /hubs/telehealth, see apps/staff/.../TelehealthCall.ts):
 *  - the doctor offers when the patient is admitted (and whenever either side reconnects), and is the polite peer;
 *  - the patient never offers: it answers every offer, so the two sides can never offer at the same time;
 *  - offer/answer payloads are raw SDP text, ICE candidates are JSON of `RTCIceCandidateInit` (webrtc.ts);
 *  - an offer from a new doctor connection (new participant id, or after this side re-joined) gets a fresh peer
 *    connection, because the doctor builds a fresh one too.
 *
 * Framework-free on purpose: React reads immutable snapshots through `subscribe`/`getSnapshot`
 * (useSyncExternalStore). `start()`/`stop()` may run twice under React StrictMode; a generation counter drops anything
 * that arrives for an earlier start.
 */

export type CallPhase = 'idle' | 'connecting' | 'waiting' | 'in-call' | 'failed' | 'ended';
export type HubStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';
export type PeerStatus = 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'failed';
/** Why the call screen closed: the patient left, or the session reached a final status on the server. */
export type EndReason = 'left' | TelehealthStatus;

export interface PatientCallState {
  phase: CallPhase;
  /** The hub's own sentence when joining was refused (the screen maps known ones to the patient's language). */
  joinError: string | null;
  hub: HubStatus;
  admitted: boolean;
  sessionStatus: TelehealthStatus | null;
  doctorName: string | null;
  scheduledStartAt: string | null;
  /** When this device entered the waiting room (for "waiting for 3 min"). */
  waitingSince: number | null;
  doctorPresent: boolean;
  messages: TelehealthMessageDto[];
  remoteStream: MediaStream | null;
  remoteAudioOnly: boolean;
  peer: PeerStatus;
  localStream: MediaStream | null;
  mediaProblem: MediaProblem | null;
  micOn: boolean;
  cameraOn: boolean;
  audioOnly: boolean;
  quality: QualityReading | null;
  /** Consecutive poor samples (drives the "switch to audio only" suggestion). */
  poorStreak: number;
  endReason: EndReason | null;
}

export interface PatientCallOptions {
  sessionId: string;
  hubPath: string;
  /** A signed-in portal patient, or a guest holding the join token from the SMS/WhatsApp link. */
  identity: { kind: 'patient' } | { kind: 'guest'; token: string };
  iceServers?: IceServerDto[];
  audioDeviceId?: string | null;
  videoDeviceId?: string | null;
  /** Join without the camera (no camera, or a weak connection). */
  startAudioOnly?: boolean;
}

const INITIAL_STATE: PatientCallState = {
  phase: 'idle',
  joinError: null,
  hub: 'disconnected',
  admitted: false,
  sessionStatus: null,
  doctorName: null,
  scheduledStartAt: null,
  waitingSince: null,
  doctorPresent: false,
  messages: [],
  remoteStream: null,
  remoteAudioOnly: false,
  peer: 'idle',
  localStream: null,
  mediaProblem: null,
  micOn: false,
  cameraOn: false,
  audioOnly: false,
  quality: null,
  poorStreak: 0,
  endReason: null,
};

/** Modest capture settings: good enough for a consultation, light on mobile data and battery. */
const AUDIO: MediaTrackConstraints = { echoCancellation: true, noiseSuppression: true, autoGainControl: true };
const VIDEO: MediaTrackConstraints = {
  width: { ideal: 640 },
  height: { ideal: 480 },
  frameRate: { ideal: 20, max: 24 },
};
const CLOSED: ReadonlySet<TelehealthStatus> = new Set(['Completed', 'Cancelled', 'Missed']);
const QUALITY_SAMPLE_MS = 5_000;
const QUALITY_REPORT_MS = 15_000;

interface SignalPayload {
  from: string;
  data: unknown;
}

const noop = () => undefined;

export class PatientCall {
  private state: PatientCallState = INITIAL_STATE;
  private readonly listeners = new Set<() => void>();
  private generation = 0;
  private hub: HubClient | null = null;
  private hubSubscriptions: (() => void)[] = [];
  private pc: RTCPeerConnection | null = null;
  private remoteId: string | null = null;
  /** Set after this side re-joined: the doctor's next offer comes from a brand-new connection. */
  private needsFreshPeer = false;
  private pendingCandidates: RTCIceCandidateInit[] = [];
  private signalling: Promise<void> = Promise.resolve();
  private iceServers: RTCIceServer[];
  private qualityTimer: ReturnType<typeof setInterval> | undefined;
  private previousStats: StatsSnapshot | null = null;
  private lastQualityReportAt = 0;
  private facingMode: 'user' | 'environment' = 'user';

  constructor(private readonly options: PatientCallOptions) {
    this.iceServers = toRtcIceServers(options.iceServers ?? []);
  }

  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  readonly getSnapshot = (): PatientCallState => this.state;

  // ------------------------------------------------------------------ lifecycle

  start(): void {
    const generation = ++this.generation;
    this.update({
      ...INITIAL_STATE,
      phase: 'connecting',
      hub: 'connecting',
      audioOnly: Boolean(this.options.startAudioOnly),
    });
    void this.acquireMedia(generation);
    void this.connect(generation);
    clearInterval(this.qualityTimer);
    this.qualityTimer = setInterval(() => void this.sampleQuality(), QUALITY_SAMPLE_MS);
  }

  /** Releases everything without leaving a trace on screen (component unmount). */
  stop(): void {
    this.teardown();
    this.update({ ...INITIAL_STATE });
  }

  /** The patient chose to leave (the consultation stays open; they can join again). */
  leave(): void {
    const messages = this.state.messages;
    this.teardown();
    this.update({ ...INITIAL_STATE, phase: 'ended', endReason: 'left', messages });
  }

  /** Joins again after a failed join (network back, permission granted). */
  retry(): void {
    this.teardown();
    this.start();
  }

  private finish(status: TelehealthStatus): void {
    const messages = this.state.messages;
    this.teardown();
    this.update({ ...INITIAL_STATE, phase: 'ended', endReason: status, sessionStatus: status, messages });
  }

  private teardown(): void {
    this.generation++;
    clearInterval(this.qualityTimer);
    this.disconnectHub(true);
    this.closePeer();
    this.stopLocalMedia();
  }

  // ------------------------------------------------------------------ patient actions

  setMicrophone(on: boolean): void {
    const audio = this.tracks().audio;
    if (audio) {
      audio.enabled = on;
      this.update({ micOn: on });
    }
  }

  setCamera(on: boolean): void {
    if (on && this.state.audioOnly) {
      this.setAudioOnly(false);
      return;
    }
    const video = this.tracks().video;
    if (video) {
      video.enabled = on;
      this.update({ cameraOn: on });
    }
  }

  /** Audio-only fallback (TEL-02): stops sending video to save data and tells the doctor's screen. */
  setAudioOnly(on: boolean): void {
    const video = this.tracks().video;
    const sender = this.videoSender();
    if (sender) {
      void sender.replaceTrack(on ? null : video).catch(noop);
    }
    if (video) {
      video.enabled = !on;
    }
    this.update({ audioOnly: on, cameraOn: !on && Boolean(video), poorStreak: 0 });
    void this.hub?.send('ChangeMediaMode', this.options.sessionId, on).catch(noop);
  }

  /** Front ↔ back camera on phones (the new camera replaces the old one without renegotiating). */
  async switchCamera(): Promise<void> {
    const stream = this.state.localStream;
    const old = this.tracks().video;
    if (!stream || !old || !navigator.mediaDevices?.getUserMedia) {
      return;
    }
    const next = this.facingMode === 'user' ? 'environment' : 'user';
    try {
      const fresh = await navigator.mediaDevices.getUserMedia({ video: { ...VIDEO, facingMode: { ideal: next } } });
      const track = fresh.getVideoTracks()[0];
      if (!track) {
        return;
      }
      track.enabled = this.state.cameraOn;
      stream.removeTrack(old);
      old.stop();
      stream.addTrack(track);
      this.facingMode = next;
      const sender = this.videoSender();
      if (sender && !this.state.audioOnly) {
        await sender.replaceTrack(track).catch(noop);
      }
      // A new MediaStream object makes the preview element pick up the new track everywhere.
      this.update({ localStream: new MediaStream(stream.getTracks()) });
    } catch {
      // The other camera is unavailable: keep the current one.
    }
  }

  async sendChat(text: string): Promise<void> {
    const clean = text.trim();
    if (!clean) {
      return;
    }
    await this.requireHub().invoke('SendChat', this.options.sessionId, clean);
  }

  /** Rebuilds the video link: leave and re-enter the room, so the doctor's console sends a fresh offer. */
  async reconnectVideo(): Promise<void> {
    const hub = this.hub;
    const generation = this.generation;
    if (!hub || hub.state !== 'Connected') {
      return;
    }
    this.closePeer();
    this.update({ peer: 'connecting', remoteStream: null, quality: null, poorStreak: 0 });
    await hub.invoke('Leave', this.options.sessionId).catch(noop);
    await this.join(generation).catch((error: unknown) => {
      if (generation === this.generation) {
        this.update({ phase: 'failed', joinError: hubErrorMessage(error) });
      }
    });
  }

  // ------------------------------------------------------------------ signalling

  private async connect(generation: number): Promise<void> {
    const guest = this.options.identity.kind === 'guest';
    // The shared SignalR client (WebSocket with SSE and long-polling fallbacks, keep-alive, automatic reconnection).
    const hub = createHubClient(this.options.hubPath, guest ? () => null : freshAccessToken);
    this.hub = hub;
    this.hubSubscriptions = this.subscribeToHub(hub, generation);
    try {
      await hub.start();
    } catch {
      if (generation === this.generation) {
        this.disconnectHub(false);
        this.update({ phase: 'failed', hub: 'disconnected', joinError: null });
      }
      return;
    }
    if (generation !== this.generation) {
      void hub.stop();
      return;
    }
    this.update({ hub: 'connected' });
    try {
      await this.join(generation);
    } catch (error) {
      if (generation === this.generation) {
        this.disconnectHub(false);
        this.update({ phase: 'failed', hub: 'disconnected', joinError: hubErrorMessage(error) });
      }
    }
  }

  private async join(generation: number): Promise<void> {
    const hub = this.requireHub();
    const identity = this.options.identity;
    const result =
      identity.kind === 'guest'
        ? await hub.invoke<TelehealthJoinResult>('JoinAsGuest', this.options.sessionId, identity.token)
        : await hub.invoke<TelehealthJoinResult>('JoinAsPatient', this.options.sessionId);
    if (generation !== this.generation) {
      return;
    }
    if (result.iceServers.length > 0) {
      this.iceServers = toRtcIceServers(result.iceServers);
    }
    // A new hub connection is a new participant: the doctor's console answers it with a brand-new peer connection.
    this.needsFreshPeer = this.pc !== null;
    this.update({
      phase: result.admitted ? 'in-call' : 'waiting',
      joinError: null,
      admitted: result.admitted,
      sessionStatus: result.status,
      doctorName: result.practitionerName,
      scheduledStartAt: result.scheduledStartAt,
      waitingSince: result.admitted ? null : (this.state.waitingSince ?? Date.now()),
      peer: result.admitted ? 'connecting' : 'idle',
      messages: mergeMessages(this.state.messages, result.recentMessages),
    });
  }

  private subscribeToHub(hub: HubClient, generation: number): (() => void)[] {
    const live = () => generation === this.generation && this.hub === hub;
    return [
      hub.on('admitted', () => {
        if (live()) {
          this.update({ phase: 'in-call', admitted: true, waitingSince: null, peer: 'connecting' });
        }
      }),
      hub.on<{ participantId: string; role: TelehealthRole }>('participantJoined', (participant) => {
        if (live() && participant.role === 'Clinician') {
          this.update({ doctorPresent: true });
        }
      }),
      hub.on<{ participantId: string; role: TelehealthRole }>('participantLeft', (participant) => {
        if (!live()) {
          return;
        }
        if (participant.role === 'Clinician' || participant.participantId === this.remoteId) {
          this.closePeer();
          this.update({
            doctorPresent: false,
            remoteStream: null,
            remoteAudioOnly: false,
            peer: 'idle',
            quality: null,
            poorStreak: 0,
          });
        }
      }),
      hub.on<SignalPayload>(
        'offer',
        (message) => live() && this.enqueue(() => this.handleOffer(message.from, message.data)),
      ),
      hub.on<SignalPayload>(
        'answer',
        (message) => live() && this.enqueue(() => this.handleAnswer(message.from, message.data)),
      ),
      hub.on<SignalPayload>(
        'iceCandidate',
        (message) => live() && this.enqueue(() => this.handleCandidate(message.from, message.data)),
      ),
      hub.on<TelehealthMessageDto>('chatMessage', (message) => {
        if (live()) {
          this.update({ messages: mergeMessages(this.state.messages, [message]) });
        }
      }),
      hub.on<{ from: string; audioOnly: boolean }>('mediaModeChanged', (message) => {
        if (live() && message.from === this.remoteId) {
          this.update({ remoteAudioOnly: message.audioOnly });
        }
      }),
      hub.on<{ status: TelehealthStatus }>('sessionUpdated', (message) => {
        if (!live()) {
          return;
        }
        if (CLOSED.has(message.status)) {
          this.finish(message.status);
        } else {
          this.update({ sessionStatus: message.status });
        }
      }),
      hub.on<{ status: TelehealthStatus }>('sessionEnded', (message) => {
        if (live()) {
          this.finish(message.status);
        }
      }),
      hub.onreconnecting(() => {
        if (live()) {
          this.update({ hub: 'reconnecting' });
        }
      }),
      hub.onreconnected(() => {
        if (!live()) {
          return;
        }
        this.update({ hub: 'connected' });
        // The server re-admits a patient who was already in a running call; the doctor then sends a fresh offer.
        void this.join(generation).catch((error: unknown) => {
          if (live()) {
            this.update({ phase: 'failed', joinError: hubErrorMessage(error) });
          }
        });
      }),
      hub.onclose(() => {
        if (live()) {
          this.update({ hub: 'disconnected', phase: this.state.phase === 'ended' ? 'ended' : 'failed' });
        }
      }),
    ];
  }

  private disconnectHub(sayGoodbye: boolean): void {
    const hub = this.hub;
    this.hub = null;
    this.hubSubscriptions.forEach((unsubscribe) => unsubscribe());
    this.hubSubscriptions = [];
    if (hub) {
      if (sayGoodbye && hub.state === 'Connected') {
        void hub.send('Leave', this.options.sessionId).catch(noop);
      }
      void hub.stop();
    }
  }

  private requireHub(): HubClient {
    if (!this.hub || this.hub.state !== 'Connected') {
      throw new Error('Not connected.');
    }
    return this.hub;
  }

  /** Signalling steps run strictly one after another, in arrival order. Failures are not logged (no patient data). */
  private enqueue(step: () => Promise<void>): void {
    this.signalling = this.signalling.then(step).catch(noop);
  }

  // ------------------------------------------------------------------ peer connection (answering side)

  private createPeer(remoteId: string): RTCPeerConnection {
    this.closePeer();
    const pc = new RTCPeerConnection({ iceServers: this.iceServers });
    this.pc = pc;
    this.remoteId = remoteId;
    this.needsFreshPeer = false;
    this.previousStats = null;
    this.update({
      remoteStream: null,
      remoteAudioOnly: false,
      peer: 'connecting',
      quality: null,
      poorStreak: 0,
      doctorPresent: true,
    });

    pc.onicecandidate = (event) => {
      if (event.candidate && pc === this.pc) {
        void this.hub
          ?.send('SendIceCandidate', this.options.sessionId, encodeCandidate(event.candidate.toJSON()))
          .catch(noop);
      }
    };
    pc.ontrack = (event) => {
      if (pc !== this.pc) {
        return;
      }
      const stream = event.streams[0] ?? this.state.remoteStream ?? new MediaStream();
      if (!event.streams[0] && !stream.getTracks().includes(event.track)) {
        stream.addTrack(event.track);
      }
      this.update({ remoteStream: stream });
    };
    pc.onconnectionstatechange = () => this.onConnectionState(pc);
    return pc;
  }

  private async handleOffer(from: string, data: unknown): Promise<void> {
    const sdp = decodeDescription(data);
    if (!sdp) {
      return;
    }
    let pc = this.pc;
    if (!pc || this.remoteId !== from || this.needsFreshPeer || pc.signalingState !== 'stable') {
      pc = this.createPeer(from);
    }
    try {
      await pc.setRemoteDescription({ type: 'offer', sdp });
    } catch {
      // An offer from a rebuilt doctor connection cannot be applied to the old one: start afresh.
      pc = this.createPeer(from);
      await pc.setRemoteDescription({ type: 'offer', sdp });
    }
    this.sendLocalMedia(pc);
    await this.flushCandidates(pc);
    await pc.setLocalDescription();
    if (pc === this.pc && pc.localDescription) {
      this.update({ admitted: true, phase: this.state.phase === 'ended' ? 'ended' : 'in-call' });
      await this.hub?.send('SendAnswer', this.options.sessionId, pc.localDescription.sdp);
    }
  }

  /** The patient never offers, so an answer is unexpected; it is applied only if this side really is waiting for one. */
  private async handleAnswer(from: string, data: unknown): Promise<void> {
    const sdp = decodeDescription(data);
    const pc = this.pc;
    if (!sdp || !pc || from !== this.remoteId || pc.signalingState !== 'have-local-offer') {
      return;
    }
    await pc.setRemoteDescription({ type: 'answer', sdp });
    await this.flushCandidates(pc);
  }

  private async handleCandidate(from: string, data: unknown): Promise<void> {
    const candidate = decodeCandidate(data);
    if (!candidate || (this.remoteId && from !== this.remoteId)) {
      return;
    }
    const pc = this.pc;
    if (!pc || !pc.remoteDescription) {
      this.pendingCandidates.push(candidate);
      return;
    }
    await pc.addIceCandidate(candidate).catch(noop);
  }

  private async flushCandidates(pc: RTCPeerConnection): Promise<void> {
    const queued = this.pendingCandidates;
    this.pendingCandidates = [];
    for (const candidate of queued) {
      await pc.addIceCandidate(candidate).catch(noop);
    }
  }

  /** Sends our microphone/camera on the transceivers the doctor's offer created (receive-only when we have none). */
  private sendLocalMedia(pc: RTCPeerConnection): void {
    const { stream, audio, video } = this.tracks();
    for (const transceiver of pc.getTransceivers()) {
      if (isStopped(transceiver)) {
        continue;
      }
      const kind = transceiver.receiver.track.kind;
      const track = kind === 'audio' ? audio : kind === 'video' ? (this.state.audioOnly ? null : video) : null;
      const hasDevice = kind === 'audio' ? Boolean(audio) : Boolean(video);
      if (!hasDevice) {
        continue;
      }
      transceiver.direction = 'sendrecv';
      void transceiver.sender.replaceTrack(track).catch(noop);
      if (stream) {
        transceiver.sender.setStreams(stream);
      }
    }
  }

  private videoSender(): RTCRtpSender | null {
    return this.pc?.getTransceivers().find((t) => t.receiver.track.kind === 'video' && !isStopped(t))?.sender ?? null;
  }

  private onConnectionState(pc: RTCPeerConnection): void {
    if (pc !== this.pc) {
      return;
    }
    switch (pc.connectionState) {
      case 'connected':
        this.update({ peer: 'connected' });
        break;
      case 'disconnected':
        // Usually a short network blip; the doctor's console restarts ICE if it does not recover.
        this.update({ peer: 'reconnecting' });
        break;
      case 'failed':
        this.update({ peer: 'failed' });
        break;
      case 'new':
      case 'connecting':
        this.update({ peer: 'connecting' });
        break;
      default:
        break;
    }
  }

  private closePeer(): void {
    const pc = this.pc;
    this.pc = null;
    this.remoteId = null;
    this.pendingCandidates = [];
    if (pc) {
      pc.onicecandidate = null;
      pc.ontrack = null;
      pc.onconnectionstatechange = null;
      pc.close();
    }
  }

  // ------------------------------------------------------------------ camera and microphone

  private async acquireMedia(generation: number): Promise<void> {
    const devices = typeof navigator === 'undefined' ? undefined : navigator.mediaDevices;
    if (!devices?.getUserMedia) {
      this.update({ mediaProblem: 'unsupported' });
      return;
    }
    const audio: MediaTrackConstraints = {
      ...AUDIO,
      ...(this.options.audioDeviceId ? { deviceId: { exact: this.options.audioDeviceId } } : {}),
    };
    const video: MediaTrackConstraints = {
      ...VIDEO,
      ...(this.options.videoDeviceId ? { deviceId: { exact: this.options.videoDeviceId } } : { facingMode: 'user' }),
    };
    let stream: MediaStream | null = null;
    let problem: MediaProblem | null = null;
    try {
      stream = await devices.getUserMedia(this.options.startAudioOnly ? { audio } : { audio, video });
    } catch (error) {
      problem = classifyMediaError(error);
      if (!this.options.startAudioOnly && canFallBackToAudio(problem)) {
        try {
          stream = await devices.getUserMedia({ audio });
        } catch (audioError) {
          problem = classifyMediaError(audioError);
        }
      }
    }
    if (generation !== this.generation) {
      stream?.getTracks().forEach((track) => track.stop());
      return;
    }
    const hasAudio = Boolean(stream?.getAudioTracks().length);
    const hasVideo = Boolean(stream?.getVideoTracks().length);
    this.update({
      localStream: stream,
      mediaProblem:
        stream && hasAudio ? (hasVideo || this.options.startAudioOnly ? null : problem) : (problem ?? 'unknown'),
      micOn: hasAudio,
      cameraOn: hasVideo && !this.state.audioOnly,
    });
    if (this.pc && stream) {
      this.sendLocalMedia(this.pc);
    }
  }

  private tracks(): { stream: MediaStream | null; audio: MediaStreamTrack | null; video: MediaStreamTrack | null } {
    const stream = this.state.localStream;
    return { stream, audio: stream?.getAudioTracks()[0] ?? null, video: stream?.getVideoTracks()[0] ?? null };
  }

  private stopLocalMedia(): void {
    this.state.localStream?.getTracks().forEach((track) => track.stop());
  }

  // ------------------------------------------------------------------ connection quality (TEL-05)

  private async sampleQuality(): Promise<void> {
    const pc = this.pc;
    if (!pc || pc.connectionState !== 'connected') {
      return;
    }
    try {
      const report = await pc.getStats();
      if (pc !== this.pc) {
        return;
      }
      const snapshot = snapshotFromStats(report.values() as Iterable<Record<string, unknown>>);
      const expectVideo = !this.state.remoteAudioOnly && Boolean(this.state.remoteStream?.getVideoTracks().length);
      const reading = computeQuality(this.previousStats, snapshot, expectVideo);
      this.previousStats = snapshot;
      this.update({ quality: reading, poorStreak: reading.level === 'poor' ? this.state.poorStreak + 1 : 0 });

      const now = Date.now();
      if (now - this.lastQualityReportAt >= QUALITY_REPORT_MS) {
        this.lastQualityReportAt = now;
        void this.hub
          ?.send('ReportQuality', this.options.sessionId, {
            roundTripMs: reading.rttMs,
            packetLossPercent: reading.lossPercent,
            bitrateKbps: reading.bitrateKbps,
            audioOnly: this.state.audioOnly,
          })
          .catch(noop);
      }
    } catch {
      // Statistics are best effort.
    }
  }

  private update(patch: Partial<PatientCallState>): void {
    this.state = { ...this.state, ...patch };
    this.listeners.forEach((listener) => listener());
  }
}

function isStopped(transceiver: RTCRtpTransceiver): boolean {
  return transceiver.direction === 'stopped' || transceiver.currentDirection === 'stopped';
}

function toRtcIceServers(servers: IceServerDto[]): RTCIceServer[] {
  return servers.map((server) => ({
    urls: server.urls,
    ...(server.username ? { username: server.username } : {}),
    ...(server.credential ? { credential: server.credential } : {}),
  }));
}

/** Messages in time order without duplicates (the server echoes the patient's own messages back). */
export function mergeMessages(
  current: readonly TelehealthMessageDto[],
  incoming: readonly TelehealthMessageDto[],
): TelehealthMessageDto[] {
  const byId = new Map(current.map((message) => [message.id, message]));
  incoming.forEach((message) => byId.set(message.id, message));
  return [...byId.values()].sort((a, b) => Date.parse(a.sentAt) - Date.parse(b.sentAt));
}

/** A current access token for the hub (the session refreshes it a minute before expiry). */
async function freshAccessToken(): Promise<string | null> {
  const { accessToken, expiresAt } = useSession.getState();
  if (!accessToken || (expiresAt !== null && expiresAt - Date.now() < 60_000)) {
    return auth.refresh();
  }
  return accessToken;
}
