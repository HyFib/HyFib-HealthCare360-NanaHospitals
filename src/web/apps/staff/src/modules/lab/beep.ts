const SOUND_KEY = 'hc360:lab-scan-sound';

let context: AudioContext | null = null;

/** Workstation preference: short tones for scan problems (on by default; benches are noisy and eyes are on tubes). */
export function scanSoundEnabled(): boolean {
  try {
    return localStorage.getItem(SOUND_KEY) !== 'off';
  } catch {
    return true;
  }
}

export function setScanSoundEnabled(enabled: boolean): void {
  try {
    localStorage.setItem(SOUND_KEY, enabled ? 'on' : 'off');
  } catch {
    // Preference only.
  }
}

/**
 * A short tone: high for a good scan, two low tones for a problem. Best effort: audio may be unavailable (no sound
 * card, policy), and scanning must never fail because of it.
 */
export function beep(kind: 'ok' | 'problem'): void {
  try {
    context ??= new AudioContext();
    const tones =
      kind === 'ok'
        ? [{ frequency: 1320, at: 0 }]
        : [
            { frequency: 330, at: 0 },
            { frequency: 262, at: 0.18 },
          ];
    for (const tone of tones) {
      const oscillator = context.createOscillator();
      const gain = context.createGain();
      oscillator.type = 'sine';
      oscillator.frequency.value = tone.frequency;
      gain.gain.value = kind === 'ok' ? 0.05 : 0.12;
      oscillator.connect(gain).connect(context.destination);
      const start = context.currentTime + tone.at;
      oscillator.start(start);
      oscillator.stop(start + (kind === 'ok' ? 0.06 : 0.15));
    }
  } catch {
    // No audio: visual feedback is still there.
  }
}
