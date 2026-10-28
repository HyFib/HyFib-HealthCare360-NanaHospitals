import { startClockAt } from './clock';
import { installReplay, Replay, type DemoData, type RoleRecording } from './replay';

// The first module the demo runs, before the staff app's: the clock goes back to the recorded morning and the replay
// takes the API's place, so nothing the app does ever sees the real date or the network.

/** What data/meta.js says before anything else runs: the recorded day, its roles and the files holding its answers. */
export interface DemoMeta {
  clockStart: string;
  facility: string;
  recordedOn: string;
  roles: { role: string; userName: string; displayName: string; roleName: string }[];
  /** Every patient's id (for verify.mjs). */
  patients: string[];
  files: string[];
}

interface DataPart {
  roles?: Record<string, RoleRecording>;
  bodies?: [start: number, bodies: string[]];
}

declare global {
  interface Window {
    HC360_DEMO: DemoMeta;
    HC360_DEMO_PARTS?: DataPart[];
    /** For the walk that checks the demo (verify.mjs): what it asked for that was not recorded. */
    HC360_DEMO_REPLAY?: Replay;
  }
}

export const meta = window.HC360_DEMO;
startClockAt(Date.parse(meta.clockStart));

function load(src: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const script = document.createElement('script');
    script.src = src;
    script.onload = () => resolve();
    script.onerror = () => reject(new Error(`The demo's data (${src}) did not load.`));
    document.head.append(script);
  });
}

/** The recorded answers, from the data files (each adds its part to HC360_DEMO_PARTS as it loads). */
export const dataReady: Promise<DemoData> = Promise.all(meta.files.map(load)).then(() => {
  const roles: Record<string, RoleRecording> = {};
  const bodies: string[] = [];
  for (const part of window.HC360_DEMO_PARTS ?? []) {
    Object.assign(roles, part.roles);
    if (part.bodies) {
      const [start, list] = part.bodies;
      list.forEach((body, i) => {
        bodies[start + i] = body;
      });
    }
  }
  return { clockStart: meta.clockStart, roles, bodies };
});

export const replay = new Replay(dataReady);
installReplay(replay);
window.HC360_DEMO_REPLAY = replay;
