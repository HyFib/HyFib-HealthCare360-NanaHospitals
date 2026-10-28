/** What the demo page needs of an app it has started (the staff app or the patient portal). */
export interface StartedApp {
  /** Opens a screen as a link inside the app does, by its whole path: /patients?tab=visits, /portal/reports. */
  open: (path: string) => Promise<void>;
  /** The screen showing, by its whole path. */
  here: () => string;
}

/** A module that starts one of the apps in `root`, at `path`: staff.tsx and portal.tsx. */
export interface AppModule {
  startApp: (root: HTMLElement, path: string) => StartedApp;
}
