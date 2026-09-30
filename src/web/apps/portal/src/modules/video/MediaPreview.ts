import { canFallBackToAudio, classifyMediaError, currentCapabilities, mediaSupport, type MediaProblem } from './media';

/**
 * The device check before a video consultation: asks for the camera and microphone, shows the preview, measures the
 * microphone level ("we can hear you"), lists devices to switch between, and plays a test sound. Framework-free;
 * React reads snapshots through `subscribe`/`getSnapshot`. The preview is released before the call takes the devices.
 */

export type PreviewStatus = 'idle' | 'requesting' | 'ready' | 'error';

export interface PreviewState {
  status: PreviewStatus;
  problem: MediaProblem | null;
  stream: MediaStream | null;
  hasAudio: boolean;
  hasVideo: boolean;
  microphones: MediaDeviceInfo[];
  cameras: MediaDeviceInfo[];
  audioDeviceId: string | null;
  videoDeviceId: string | null;
  /** Microphone level 0–1 (updated about ten times a second). */
  level: number;
  /** True once speech-level sound was picked up. */
  heard: boolean;
}

const INITIAL: PreviewState = {
  status: 'idle',
  problem: null,
  stream: null,
  hasAudio: false,
  hasVideo: false,
  microphones: [],
  cameras: [],
  audioDeviceId: null,
  videoDeviceId: null,
  level: 0,
  heard: false,
};

const AUDIO: MediaTrackConstraints = { echoCancellation: true, noiseSuppression: true, autoGainControl: true };
const VIDEO: MediaTrackConstraints = { width: { ideal: 640 }, height: { ideal: 480 }, facingMode: 'user' };
const HEARD_LEVEL = 0.08;

type AudioContextConstructor = typeof AudioContext;

function audioContextClass(): AudioContextConstructor | null {
  const w = window as unknown as {
    AudioContext?: AudioContextConstructor;
    webkitAudioContext?: AudioContextConstructor;
  };
  return w.AudioContext ?? w.webkitAudioContext ?? null;
}

export class MediaPreview {
  private state: PreviewState = INITIAL;
  private readonly listeners = new Set<() => void>();
  private generation = 0;
  private context: AudioContext | null = null;
  private frame = 0;
  private lastLevelAt = 0;

  readonly subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  readonly getSnapshot = (): PreviewState => this.state;

  /** Asks for the camera and microphone (the browser shows its permission prompt the first time). */
  async start(devices: { audioDeviceId?: string | null; videoDeviceId?: string | null } = {}): Promise<void> {
    const generation = ++this.generation;
    this.release();
    const support = mediaSupport(currentCapabilities());
    if (support !== 'ok') {
      this.update({ ...INITIAL, status: 'error', problem: support });
      return;
    }
    this.update({ status: 'requesting', problem: null, stream: null, level: 0 });

    const audio: MediaTrackConstraints = {
      ...AUDIO,
      ...(devices.audioDeviceId ? { deviceId: { exact: devices.audioDeviceId } } : {}),
    };
    const video: MediaTrackConstraints = devices.videoDeviceId
      ? { ...VIDEO, facingMode: undefined, deviceId: { exact: devices.videoDeviceId } }
      : VIDEO;
    let stream: MediaStream | null = null;
    let problem: MediaProblem | null = null;
    try {
      stream = await navigator.mediaDevices.getUserMedia({ audio, video });
    } catch (error) {
      problem = classifyMediaError(error);
      if (canFallBackToAudio(problem)) {
        try {
          stream = await navigator.mediaDevices.getUserMedia({ audio });
        } catch (audioError) {
          problem = classifyMediaError(audioError);
        }
      }
    }
    if (generation !== this.generation) {
      stream?.getTracks().forEach((track) => track.stop());
      return;
    }
    if (!stream) {
      this.update({ ...INITIAL, status: 'error', problem: problem ?? 'unknown' });
      return;
    }

    const audioTrack = stream.getAudioTracks()[0] ?? null;
    const videoTrack = stream.getVideoTracks()[0] ?? null;
    let microphones: MediaDeviceInfo[] = [];
    let cameras: MediaDeviceInfo[] = [];
    try {
      const all = await navigator.mediaDevices.enumerateDevices();
      microphones = all.filter((device) => device.kind === 'audioinput' && device.deviceId);
      cameras = all.filter((device) => device.kind === 'videoinput' && device.deviceId);
    } catch {
      // Device names are a convenience only.
    }
    if (generation !== this.generation) {
      stream.getTracks().forEach((track) => track.stop());
      return;
    }
    this.update({
      status: 'ready',
      problem: videoTrack ? null : problem,
      stream,
      hasAudio: Boolean(audioTrack),
      hasVideo: Boolean(videoTrack),
      microphones,
      cameras,
      audioDeviceId: audioTrack?.getSettings().deviceId ?? devices.audioDeviceId ?? null,
      videoDeviceId: videoTrack?.getSettings().deviceId ?? devices.videoDeviceId ?? null,
      level: 0,
      heard: false,
    });
    if (audioTrack) {
      this.meter(stream, generation);
    }
  }

  /** Uses another microphone or camera (the whole preview restarts with the chosen pair). */
  changeDevice(kind: 'audio' | 'video', deviceId: string): Promise<void> {
    return this.start({
      audioDeviceId: kind === 'audio' ? deviceId : this.state.audioDeviceId,
      videoDeviceId: kind === 'video' ? deviceId : this.state.videoDeviceId,
    });
  }

  /** A short, gentle beep so the patient can check the speaker or earphones. */
  playTestSound(): void {
    const Context = audioContextClass();
    if (!Context) {
      return;
    }
    const context = new Context();
    const oscillator = context.createOscillator();
    const gain = context.createGain();
    oscillator.type = 'sine';
    oscillator.frequency.value = 660;
    gain.gain.setValueAtTime(0.0001, context.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.25, context.currentTime + 0.05);
    gain.gain.exponentialRampToValueAtTime(0.0001, context.currentTime + 0.8);
    oscillator.connect(gain).connect(context.destination);
    oscillator.start();
    oscillator.stop(context.currentTime + 0.85);
    oscillator.onended = () => void context.close().catch(() => undefined);
  }

  /** Stops the preview (the call then takes the camera and microphone itself). */
  stop(): void {
    this.generation++;
    this.release();
    this.update({ ...INITIAL });
  }

  private release(): void {
    cancelAnimationFrame(this.frame);
    this.frame = 0;
    void this.context?.close().catch(() => undefined);
    this.context = null;
    this.state.stream?.getTracks().forEach((track) => track.stop());
  }

  private meter(stream: MediaStream, generation: number): void {
    const Context = audioContextClass();
    if (!Context) {
      return;
    }
    try {
      const context = new Context();
      this.context = context;
      const source = context.createMediaStreamSource(stream);
      const analyser = context.createAnalyser();
      analyser.fftSize = 512;
      source.connect(analyser);
      const samples = new Uint8Array(analyser.fftSize);
      const tick = (time: number) => {
        if (generation !== this.generation) {
          return;
        }
        analyser.getByteTimeDomainData(samples);
        let sum = 0;
        for (const sample of samples) {
          const centred = (sample - 128) / 128;
          sum += centred * centred;
        }
        const level = Math.min(1, Math.sqrt(sum / samples.length) * 4);
        if (time - this.lastLevelAt > 100) {
          this.lastLevelAt = time;
          this.update({ level, heard: this.state.heard || level > HEARD_LEVEL });
        }
        this.frame = requestAnimationFrame(tick);
      };
      this.frame = requestAnimationFrame(tick);
    } catch {
      // Some browsers block audio analysis until a tap; the preview still works.
    }
  }

  private update(patch: Partial<PreviewState>): void {
    this.state = { ...this.state, ...patch };
    this.listeners.forEach((listener) => listener());
  }
}
