import { auth, bridge, createHubClient, hubErrorMessage, RESYNC, useSession, type HubClient } from '@hc360/core';
import {
  telehealthApi,
  type IceServerDto,
  type TelehealthJoinResult,
  type TelehealthMessageDto,
  type TelehealthRole,
  type TelehealthSessionDetail,
  type TelehealthStatus,
  type WaitingParticipant,
} from './api';
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
 * The doctor's side of a 1:1 video consultation (TEL-02/03/05): signalling through /hubs/telehealth, a peer-to-peer
 * WebRTC connection (media never touches the server), camera/microphone, chat and connection quality.
 *
 * Negotiation ("perfect negotiation", doctor = polite peer): the doctor offers when a patient is admitted (or
 * reconnects), and also accepts an offer from the patient at any time, rolling back its own if both offered at once.
 * Signalling steps run one at a time in arrival order. A failed connection is repaired with ICE restarts; a patient
 * who drops and rejoins gets a fresh connection. The patient portal must follow the payload format in `webrtc.ts`.
 *
 * Kept out of React on purpose: media streams and the peer connection live here, React reads immutable snapshots
 * through `subscribe`/`getSnapshot` (useSyncExternalStore). `start()`/`stop()` may be called repeatedly (React
 * StrictMode mounts effects twice in development); anything still arriving from an earlier start is ignored.
 */

export type CallPhase = 'idle' | 'joining' | 'joined' | 'failed' | 'ended';
export type HubStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';
export type PeerStatus = 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'failed';
export type MediaStatus = 'idle' | 'requesting' | 'ready' | 'denied' | 'no-device' | 'unsupported';

export interface RemoteParticipant {
  participantId: string;
  name: string;
  role: TelehealthRole;
}

export interface MediaState {
  status: MediaStatus;
  /** Explanation for the doctor when the camera or microphone is not available. */
  message: string | null;
  hasAudio: boolean;
  hasVideo: boolean;
}

export interface CallState {
  phase: CallPhase;
  joinError: string | null;
  hub: HubStatus;
  participantId: string | null;
  sessionStatus: TelehealthStatus | null;
  waiting: WaitingParticipant[];
  messages: TelehealthMessageDto[];
  remote: RemoteParticipant | null;
  remoteStream: MediaStream | null;
  remoteAudioOnly: boolean;
  peer: PeerStatus;
  localStream: MediaStream | null;
  media: MediaState;
  micOn: boolean;
  cameraOn: boolean;
  audioOnly: boolean;
  quality: QualityReading | null;
  /** Consecutive poor quality samples (drives the "switch to audio only" suggestion). */
  poorStreak: number;
}

export interface TelehealthCallOptions {
  sessionId: string;
  hubPath: string;
  iceServers: IceServerDto[];
  /** Loads the session; used to find an admitted patient when the doctor rejoins a running call. */
  loadSession: () => Promise<TelehealthSessionDetail>;
  /** The session changed on the server (status, waiting room); the screen refreshes its details. */
  onSessionChanged?: () => void;
}

const INITIAL_STATE: CallState = {
  phase: 'idle',
  joinError: null,
  hub: 'disconnected',
  participantId: null,
  sessionStatus: null,
  waiting: [],
  messages: [],
  remote: null,
  remoteStream: null,
  remoteAudioOnly: false,
  peer: 'idle',
  localStream: null,
  media: { status: 'idle', message: null, hasAudio: false, hasVideo: false },
  micOn: false,
  cameraOn: false,
  audioOnly: false,
  quality: null,
  poorStreak: 0,
};

const AUDIO: MediaTrackConstraints = { echoCancellation: true, noiseSuppression: true, autoGainControl: true };
const VIDEO: MediaTrackConstraints = {
  width: { ideal: 1280 },
  height: { ideal: 720 },
  frameRate: { ideal: 24, max: 30 },
  facingMode: 'user',
};

const MEDIA_DENIED =
  'Camera and microphone access is blocked for HealthCare360. Allow it (in HC360 Desktop or the browser site settings) and select Try again. You can still see and hear the patient and use chat.';
const MEDIA_MISSING =
  'No microphone or camera was found. Connect a headset or webcam and select Try again. You can still see and hear the patient and use chat.';
const MEDIA_UNSUPPORTED =
  'This window cannot use a camera. Open HealthCare360 through its secure (https) address or in HC360 Desktop. You can still use chat.';

const QUALITY_SAMPLE_MS = 5_000;
const QUALITY_REPORT_MS = 15_000;
const MAX_ICE_RESTARTS = 3;
const CLOSED: ReadonlySet<TelehealthStatus> = new Set(['Completed', 'Cancelled', 'Missed']);

interface SignalPayload {
  from: string;
  data: unknown;
}

const noop = () => undefined;

export class TelehealthCall {
  private state: CallState = INITIAL_STATE;
  private readonly listeners = new Set<() => void>();
  private generation = 0;
  private hub: HubClient | null = null;
  private hubSubscriptions: (() => void)[] = [];
  private pc: RTCPeerConnection | null = null;
  private remoteId: string | null = null;
  private audioTx: RTCRtpTransceiver | null = null;
  private videoTx: RTCRtpTransceiver | null = null;
  private pendingCandidates: RTCIceCandidateInit[] = [];
  private signalling: Promise<void> = Promise.resolve();
  private iceServers: RTCIceServer[];
  private qualityTimer: ReturnType<typeof setInterval> | undefined;
  private previousStats: StatsSnapshot | null = null;
  private lastQualityReportAt = 0;
  private iceRestarts = 0;

  constructor(private readonly options: TelehealthCallOptions) {
    this.iceServers = toRtcIceServers(options.iceServers);
  }

  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  readonly getSnapshot = (): CallState => this.state;

  // ------------------------------------------------------------------ lifecycle

  start(): void {
    const generation = ++this.generation;
    this.update({ ...INITIAL_STATE, phase: 'joining', hub: 'connecting' });
    void this.acquireMedia(generation);
    void this.connect(generation);
    clearInterval(this.qualityTimer);
    this.qualityTimer = setInterval(() => void this.sampleQuality(), QUALITY_SAMPLE_MS);
  }

  /** Leaves the room (the session itself stays open; the patient waits for the doctor to return). */
  stop(): void {
    this.generation++;
    clearInterval(this.qualityTimer);
    this.disconnectHub(true);
    this.closePeer();
    this.stopLocalMedia();
    this.update({ ...INITIAL_STATE });
  }

  /** Tries again after a failed join (network or permission problems solved). */
  retry(): void {
    this.stop();
    this.start();
  }

  /** The consultation ended (here or on another device): hang up and show the summary. */
  finish(status: TelehealthStatus): void {
    this.generation++;
    clearInterval(this.qualityTimer);
    this.disconnectHub(true);
    this.closePeer();
    this.stopLocalMedia();
    this.update({ ...INITIAL_STATE, phase: 'ended', sessionStatus: status, messages: this.state.messages });
  }

  // ------------------------------------------------------------------ doctor actions

  async admit(participantId?: string): Promise<void> {
    const hub = this.requireHub();
    await hub.invoke('Admit', this.options.sessionId, participantId ?? null);
  }

  async sendChat(text: string): Promise<void> {
    const clean = text.trim();
    if (!clean) {
      return;
    }
    // While signalling is down (WiFi drop), the REST endpoint stores the message and relays it to the room. Either way the
    // reply shows it here too: the room's echo may not come (with several servers, while the backplane is away).
    const message =
      this.hub?.state === 'Connected'
        ? await this.hub.invoke<TelehealthMessageDto>('SendChat', this.options.sessionId, clean)
        : await telehealthApi.postMessage(this.options.sessionId, clean);
    this.update({ messages: mergeMessages(this.state.messages, [message]) });
  }

  setMicrophone(on: boolean): void {
    const audio = this.localTracks().audio;
    if (!audio) {
      return;
    }
    audio.enabled = on;
    this.update({ micOn: on });
  }

  setCamera(on: boolean): void {
    if (on && this.state.audioOnly) {
      this.setAudioOnly(false);
      return;
    }
    const video = this.localTracks().video;
    if (!video) {
      return;
    }
    video.enabled = on;
    this.update({ cameraOn: on });
  }

  /** Audio-only fallback for weak networks (TEL-02): stops sending video and tells the patient's screen. */
  setAudioOnly(on: boolean): void {
    const video = this.localTracks().video;
    if (this.videoTx && video) {
      void this.videoTx.sender.replaceTrack(on ? null : video).catch(noop);
    }
    if (video) {
      video.enabled = !on;
    }
    this.update({ audioOnly: on, cameraOn: !on && Boolean(video), poorStreak: 0 });
    void this.hub?.send('ChangeMediaMode', this.options.sessionId, on).catch(noop);
  }

  /** Asks for the camera and microphone again (after the doctor allowed them or plugged in a headset). */
  retryMedia(): void {
    this.stopLocalMedia();
    void this.acquireMedia(this.generation);
  }

  /** Rebuilds the video connection with the patient (after "could not connect"). */
  reconnectVideo(): void {
    const remote = this.state.remote;
    if (remote) {
      this.startPeerAsOfferer(remote);
    }
  }

  // ------------------------------------------------------------------ signalling

  private async connect(generation: number): Promise<void> {
    const hub = createHubClient(this.options.hubPath, freshAccessToken);
    this.hub = hub;
    this.hubSubscriptions = this.subscribeToHub(hub, generation);
    try {
      await hub.start();
    } catch {
      if (generation === this.generation) {
        this.disconnectHub(false);
        this.update({
          phase: 'failed',
          hub: 'disconnected',
          joinError: 'Could not connect to the video service. Check the network connection and try again.',
        });
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
        this.disconnectHub(true);
        this.update({ phase: 'failed', hub: 'disconnected', joinError: hubErrorMessage(error) });
      }
    }
  }

  private async join(generation: number): Promise<void> {
    const hub = this.requireHub();
    const result = await hub.invoke<TelehealthJoinResult>('JoinAsClinician', this.options.sessionId);
    if (generation !== this.generation) {
      return;
    }
    if (result.iceServers.length > 0) {
      this.iceServers = toRtcIceServers(result.iceServers);
    }
    this.update({
      phase: 'joined',
      joinError: null,
      participantId: result.participantId,
      sessionStatus: result.status,
      waiting: result.waiting,
      messages: mergeMessages(this.state.messages, result.recentMessages),
    });
    if (result.status === 'InProgress' && !this.pc) {
      await this.reconnectToPatient(generation);
    }
  }

  /**
   * Reads the consultation again after the server said messages were lost though the connection stayed open (`resync`:
   * with several servers, Redis was away): the waiting room, the status and the chat may have changed meanwhile, and an
   * admission made then may never have reached this screen. It does not join again, which would make this screen another
   * participant.
   */
  private async catchUp(generation: number): Promise<void> {
    if (this.state.phase !== 'joined') {
      return;
    }
    let result: TelehealthJoinResult;
    try {
      result = await this.requireHub().invoke<TelehealthJoinResult>('Refresh', this.options.sessionId);
    } catch {
      // A reconnect, or the next resync, catches up instead.
      return;
    }
    if (generation !== this.generation) {
      return;
    }
    if (CLOSED.has(result.status)) {
      this.finish(result.status);
      this.options.onSessionChanged?.();
      return;
    }
    this.update({
      sessionStatus: result.status,
      waiting: result.waiting,
      messages: mergeMessages(this.state.messages, result.recentMessages),
    });
    this.options.onSessionChanged?.();
    if (result.status === 'InProgress' && !this.pc) {
      await this.reconnectToPatient(generation);
    }
  }

  /** The doctor (re)joined a running call: connect to the patient who is still in the room. */
  private async reconnectToPatient(generation: number): Promise<void> {
    try {
      const session = await this.options.loadSession();
      if (generation !== this.generation || this.pc) {
        return;
      }
      const present = session.participants
        .filter((p) => p.role !== 'Clinician' && p.admittedAt && !p.leftAt)
        .sort((a, b) => Date.parse(b.joinedAt) - Date.parse(a.joinedAt))[0];
      if (present) {
        this.startPeerAsOfferer({ participantId: present.id, name: present.displayName, role: present.role });
      }
    } catch {
      // The patient's own reconnect brings them back.
    }
  }

  private subscribeToHub(hub: HubClient, generation: number): (() => void)[] {
    const live = () => generation === this.generation && this.hub === hub;
    return [
      hub.on<WaitingParticipant>('waitingRoomChanged', (participant) => {
        if (!live()) {
          return;
        }
        this.update({
          waiting: [...this.state.waiting.filter((w) => w.participantId !== participant.participantId), participant],
        });
        notifyDesktop();
      }),
      hub.on<{ participantId: string }>('waitingRoomLeft', (participant) => {
        if (live()) {
          this.update({ waiting: this.state.waiting.filter((w) => w.participantId !== participant.participantId) });
        }
      }),
      hub.on<{ participantId: string; role: TelehealthRole; name: string }>('participantJoined', (participant) => {
        if (!live() || participant.role === 'Clinician') {
          return;
        }
        this.update({ waiting: this.state.waiting.filter((w) => w.participantId !== participant.participantId) });
        this.startPeerAsOfferer({
          participantId: participant.participantId,
          name: participant.name,
          role: participant.role,
        });
      }),
      hub.on<{ participantId: string }>('participantLeft', (participant) => {
        if (live() && participant.participantId === this.remoteId) {
          this.closePeer();
          this.update({
            remote: null,
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
        if (live()) {
          this.update({ sessionStatus: message.status });
          this.options.onSessionChanged?.();
        }
      }),
      hub.on<{ status: TelehealthStatus }>('sessionEnded', (message) => {
        if (live()) {
          this.finish(message.status);
          this.options.onSessionChanged?.();
        }
      }),
      hub.on(RESYNC, () => {
        if (live()) {
          void this.catchUp(generation);
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
        // A new connection is a new participant on the server: join again (the video keeps flowing peer-to-peer).
        void this.join(generation).catch((error: unknown) => {
          if (live()) {
            this.update({ phase: 'failed', joinError: hubErrorMessage(error) });
          }
        });
      }),
      hub.onclose(() => {
        if (live()) {
          this.update({ hub: 'disconnected' });
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
      throw new Error('Not connected to the video service. Please wait while the connection is restored.');
    }
    return this.hub;
  }

  /** Signalling steps run strictly one after another, in the order they arrived. */
  private enqueue(step: () => Promise<void>): void {
    this.signalling = this.signalling
      .then(step)
      .catch((error: unknown) => console.warn('Video call signalling step failed', error));
  }

  // ------------------------------------------------------------------ peer connection

  private createPeer(remote: RemoteParticipant): RTCPeerConnection {
    this.closePeer();
    const pc = new RTCPeerConnection({ iceServers: this.iceServers });
    this.pc = pc;
    this.remoteId = remote.participantId;
    this.previousStats = null;
    this.iceRestarts = 0;
    this.update({
      remote,
      remoteStream: null,
      remoteAudioOnly: false,
      peer: 'connecting',
      quality: null,
      poorStreak: 0,
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
    pc.onnegotiationneeded = () => this.enqueue(() => this.makeOffer(pc));
    return pc;
  }

  /** Doctor offers: add our media (or receive-only slots) and let negotiationneeded send the offer. */
  private startPeerAsOfferer(remote: RemoteParticipant): void {
    const pc = this.createPeer(remote);
    const { stream, audio, video } = this.localTracks();
    const streams = stream ? [stream] : [];
    this.audioTx = pc.addTransceiver(audio ?? 'audio', { direction: audio ? 'sendrecv' : 'recvonly', streams });
    this.videoTx = pc.addTransceiver(video && !this.state.audioOnly ? video : 'video', {
      direction: video ? 'sendrecv' : 'recvonly',
      streams,
    });
  }

  private async makeOffer(pc: RTCPeerConnection): Promise<void> {
    if (pc !== this.pc || pc.signalingState !== 'stable') {
      return;
    }
    await pc.setLocalDescription();
    if (pc === this.pc && pc.localDescription) {
      await this.hub?.send('SendOffer', this.options.sessionId, pc.localDescription.sdp);
    }
  }

  private async handleOffer(from: string, data: unknown): Promise<void> {
    const sdp = decodeDescription(data);
    if (!sdp) {
      return;
    }
    let pc = this.pc;
    let fresh = false;
    if (!pc || this.remoteId !== from) {
      const known = this.state.remote?.participantId === from ? this.state.remote : null;
      const waiting = this.state.waiting.find((w) => w.participantId === from);
      pc = this.createPeer(
        known ?? { participantId: from, name: waiting?.displayName ?? 'Patient', role: waiting?.role ?? 'Guest' },
      );
      fresh = true;
    }
    if (pc.signalingState !== 'stable') {
      // Both sides offered at once: the doctor is the polite peer and takes the patient's offer.
      await pc.setLocalDescription({ type: 'rollback' });
    }
    await pc.setRemoteDescription({ type: 'offer', sdp });
    if (fresh) {
      this.useRemoteTransceivers(pc);
    }
    await this.flushCandidates(pc);
    await pc.setLocalDescription();
    if (pc === this.pc && pc.localDescription) {
      await this.hub?.send('SendAnswer', this.options.sessionId, pc.localDescription.sdp);
    }
  }

  /** Answering: send our media on the transceivers the patient's offer created. */
  private useRemoteTransceivers(pc: RTCPeerConnection): void {
    const { stream, audio, video } = this.localTracks();
    for (const transceiver of pc.getTransceivers()) {
      const kind = transceiver.receiver.track.kind;
      if (kind === 'audio' && !this.audioTx) {
        this.audioTx = transceiver;
        if (audio) {
          sendOn(transceiver, audio, stream);
        }
      } else if (kind === 'video' && !this.videoTx) {
        this.videoTx = transceiver;
        if (video) {
          sendOn(transceiver, this.state.audioOnly ? null : video, stream);
        }
      }
    }
  }

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
    const pc = this.pc;
    if (!candidate || !pc || from !== this.remoteId) {
      return;
    }
    if (!pc.remoteDescription) {
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

  private onConnectionState(pc: RTCPeerConnection): void {
    if (pc !== this.pc) {
      return;
    }
    switch (pc.connectionState) {
      case 'connected':
        this.iceRestarts = 0;
        this.update({ peer: 'connected' });
        break;
      case 'disconnected':
        this.update({ peer: 'reconnecting' });
        break;
      case 'failed':
        if (this.iceRestarts < MAX_ICE_RESTARTS) {
          this.iceRestarts += 1;
          this.update({ peer: 'reconnecting' });
          pc.restartIce();
        } else {
          this.update({ peer: 'failed' });
        }
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
    this.audioTx = null;
    this.videoTx = null;
    this.pendingCandidates = [];
    if (pc) {
      pc.onicecandidate = null;
      pc.ontrack = null;
      pc.onconnectionstatechange = null;
      pc.onnegotiationneeded = null;
      pc.close();
    }
  }

  // ------------------------------------------------------------------ camera and microphone

  private async acquireMedia(generation: number): Promise<void> {
    const devices = typeof navigator === 'undefined' ? undefined : navigator.mediaDevices;
    if (!devices?.getUserMedia) {
      this.update({ media: { status: 'unsupported', message: MEDIA_UNSUPPORTED, hasAudio: false, hasVideo: false } });
      return;
    }
    this.update({ media: { ...this.state.media, status: 'requesting', message: null } });

    let stream: MediaStream | null = null;
    let failure: { status: MediaStatus; message: string } | null = null;
    let note: string | null = null;
    try {
      stream = await devices.getUserMedia({ audio: AUDIO, video: VIDEO });
    } catch (error) {
      if (isPermissionError(error)) {
        failure = { status: 'denied', message: MEDIA_DENIED };
      } else {
        // No camera, or it is busy in another program: carry on with audio only.
        try {
          stream = await devices.getUserMedia({ audio: AUDIO });
          note = 'No camera was found, or another program is using it. The patient hears you but cannot see you.';
        } catch (audioError) {
          failure = isPermissionError(audioError)
            ? { status: 'denied', message: MEDIA_DENIED }
            : { status: 'no-device', message: MEDIA_MISSING };
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
      media: failure
        ? { status: failure.status, message: failure.message, hasAudio: false, hasVideo: false }
        : { status: 'ready', message: note, hasAudio, hasVideo },
      micOn: hasAudio,
      cameraOn: hasVideo && !this.state.audioOnly,
    });
    if (this.pc && stream) {
      this.sendLocalMediaOnExistingPeer();
    }
  }

  /** Media became available during a call (after "Try again"): start sending it (this renegotiates). */
  private sendLocalMediaOnExistingPeer(): void {
    const { stream, audio, video } = this.localTracks();
    if (this.audioTx && audio) {
      sendOn(this.audioTx, audio, stream);
    }
    if (this.videoTx && video) {
      sendOn(this.videoTx, this.state.audioOnly ? null : video, stream);
    }
  }

  private localTracks(): {
    stream: MediaStream | null;
    audio: MediaStreamTrack | null;
    video: MediaStreamTrack | null;
  } {
    const stream = this.state.localStream;
    return { stream, audio: stream?.getAudioTracks()[0] ?? null, video: stream?.getVideoTracks()[0] ?? null };
  }

  private stopLocalMedia(): void {
    this.state.localStream?.getTracks().forEach((track) => track.stop());
    this.update({
      localStream: null,
      micOn: false,
      cameraOn: false,
      media: { status: 'idle', message: null, hasAudio: false, hasVideo: false },
    });
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

  private update(patch: Partial<CallState>): void {
    this.state = { ...this.state, ...patch };
    this.listeners.forEach((listener) => listener());
  }
}

function sendOn(transceiver: RTCRtpTransceiver, track: MediaStreamTrack | null, stream: MediaStream | null): void {
  transceiver.direction = 'sendrecv';
  void transceiver.sender.replaceTrack(track).catch(noop);
  if (stream) {
    transceiver.sender.setStreams(stream);
  }
}

function isPermissionError(error: unknown): boolean {
  const name = (error as { name?: unknown } | null)?.name;
  return name === 'NotAllowedError' || name === 'SecurityError' || name === 'PermissionDeniedError';
}

function toRtcIceServers(servers: IceServerDto[]): RTCIceServer[] {
  return servers.map((server) => ({
    urls: server.urls,
    ...(server.username ? { username: server.username } : {}),
    ...(server.credential ? { credential: server.credential } : {}),
  }));
}

/** Messages in time order without duplicates (the server echoes the doctor's own messages). */
export function mergeMessages(
  current: readonly TelehealthMessageDto[],
  incoming: readonly TelehealthMessageDto[],
): TelehealthMessageDto[] {
  const byId = new Map(current.map((m) => [m.id, m]));
  incoming.forEach((m) => byId.set(m.id, m));
  return [...byId.values()].sort((a, b) => Date.parse(a.sentAt) - Date.parse(b.sentAt));
}

/** A fresh access token for the hub connection (the session refreshes it a minute before expiry). */
async function freshAccessToken(): Promise<string | null> {
  const { accessToken, expiresAt } = useSession.getState();
  if (!accessToken || (expiresAt !== null && expiresAt - Date.now() < 60_000)) {
    return auth.refresh();
  }
  return accessToken;
}

/** Desktop toast when a patient enters the waiting room while HC360 is in the background (no patient details shown). */
function notifyDesktop(): void {
  if (bridge.isDesktop && document.visibilityState !== 'visible') {
    void bridge.notify('Patient waiting', 'A patient is in the waiting room for your video consultation.').catch(noop);
  }
}
