// The demo page's code. './install' comes first: it sets the clock and puts the replay in front of the network before
// any other module (the staff app's included) runs.
import { dataReady, meta, replay } from './install';
import { Hc360ThemeProvider } from '@hc360/ui';
import { StrictMode } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { DemoBadge } from './DemoBadge';
import { Landing } from './Landing';
import { currentRole, setCurrentRole } from './replay';

declare global {
  interface Window {
    /** For verify.mjs: opens a screen of the running app as a link inside it would. */
    HC360_DEMO_NAVIGATE?: (path: string) => Promise<void>;
    /** For verify.mjs: the screen the app is on (/patients?tab=visits). */
    HC360_DEMO_HERE?: () => string;
  }
}

const container = document.getElementById('root')!;
let landing: Root | null = null;

/** Keeps the workstation's settings a role's screens open with (its queue desk, ward, counter), as staff set them. */
function setUpDesk(desk: Record<string, string>): void {
  try {
    for (const [key, value] of Object.entries(desk)) {
      if (localStorage.getItem(key) === null) {
        localStorage.setItem(key, value);
      }
    }
  } catch {
    // Without storage the screens ask for their desk, as on a new computer.
  }
}

function changeRole(): void {
  setCurrentRole(null);
  location.reload();
}

/** Signs in as `role` and opens the staff app, at `path` when given, else at the role's home. */
async function start(role: string, path = '/'): Promise<void> {
  const data = await dataReady;
  const recording = data.roles[role];
  if (!recording) {
    setCurrentRole(null);
    showLanding(null);
    return;
  }
  setCurrentRole(role);
  setUpDesk(recording.desk);
  landing?.unmount();
  landing = null;
  const { startStaffApp } = await import('./staff');
  const router = startStaffApp(container, path);
  const navigate = (to: string) => router.navigate({ href: to });
  window.HC360_DEMO_NAVIGATE = navigate;
  window.HC360_DEMO_HERE = () => router.state.location.href;

  const badge = document.createElement('div');
  document.body.append(badge);
  createRoot(badge).render(
    <StrictMode>
      <Hc360ThemeProvider>
        <DemoBadge meta={meta} navigate={(to) => void navigate(to)} changeRole={changeRole} />
      </Hc360ThemeProvider>
    </StrictMode>,
  );
}

function showLanding(error: string | null, ready = false): void {
  landing ??= createRoot(container);
  landing.render(
    <StrictMode>
      <Hc360ThemeProvider>
        <Landing meta={meta} ready={ready} error={error} onChoose={(role, path) => void start(role, path)} />
      </Hc360ThemeProvider>
    </StrictMode>,
  );
}

const role = currentRole();
if (role && meta.roles.some((r) => r.role === role)) {
  // A reload, or a new tab of a signed-in one: carry on as that role, from its home.
  void start(role);
} else {
  showLanding(null);
  dataReady.then(
    () => showLanding(null, true),
    (error: Error) => showLanding(`${error.message} Reload the page to try again.`),
  );
}

// Nothing above reaches the network; this keeps it so if a later change to the app adds a way round the replay.
void replay;
