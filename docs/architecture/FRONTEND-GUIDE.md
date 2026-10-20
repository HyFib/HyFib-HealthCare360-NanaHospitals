# Staff app – frontend guide for module screens

The staff app (`src/web/apps/staff`) is what doctors, nurses, front desk, billing, pharmacy, lab and admin staff use all
day, inside **HC360 Desktop** (WebView2) on hospital workstations and in Edge/Chrome on tablets. Every module screen must
feel like one product: same page anatomy, same components, same words. Read this before adding screens.

Reference implementations to copy from:

| Pattern | File |
|---|---|
| List with filters in the URL, server paging | `src/routes/_app/patients/index.tsx`, `src/routes/_app/front-office/visits.tsx` |
| Big form (RHF + zod), draft auto-save, Ctrl+S, server errors | `src/modules/patients/PatientForm.tsx`, `src/routes/_app/patients/register.tsx` |
| Detail page with tabs, patient banner, dialogs | `src/routes/_app/patients/$patientId/index.tsx` |
| Dialog form + printing | `src/modules/front-office/CheckInDialog.tsx`, `src/lib/print.ts` |
| Live console with realtime + keyboard shortcut | `src/routes/_app/front-office/queue.tsx` |
| Drawer editor, row actions menu, confirm dialog | `src/routes/_app/admin/users.tsx`, `src/routes/_app/admin/roles.tsx` |
| Dashboard widgets + manifest | `src/modules/front-office/manifest.ts`, `src/modules/front-office/widgets.tsx` |
| Charts | `src/lib/EChart.tsx`, used in `src/routes/_app/admin/system.tsx` |

## 1. Where things go

```
src/modules/<module>/manifest.ts      navigation, quick actions, dashboard widgets (auto-discovered)
src/modules/<module>/api.ts           DTO types (mirror the server records), queryOptions, mutations
src/modules/<module>/*.tsx            module components (dialogs, forms, widgets, print templates)
src/routes/_app/<module>/...          screens (TanStack Router file routes; `_app` = signed-in shell)
```

* Route files: `index.tsx` → `/<module>`, `queue.tsx` → `/<module>/queue`, `$id/index.tsx` → `/<module>/$id`.
  Export `export const Route = createFileRoute('/_app/<module>/...')({ component, validateSearch? })`.
* **Never edit shared files**: `src/lib/*`, `src/shell/*`, `src/routes/__root.tsx`, `src/routes/_app.tsx`,
  `src/routes/_app/index.tsx`, `packages/*`, other modules' folders, `package.json`, `vite.config.ts`. If you need
  something shared, build it inside your module and mention it in your report so it can be promoted.
* Don't hand-edit `src/routeTree.gen.ts`; `pnpm --filter @hc360/staff typecheck` regenerates it.

## 2. Manifest (navigation)

```ts
import { BedRegular, PersonAddRegular } from '@fluentui/react-icons';
import type { ModuleManifest } from '@/lib/modules';

const manifest: ModuleManifest = {
  id: 'inpatient',
  title: 'In-patients',
  section: 'inpatient',            // patients | clinical | diagnostics | inpatient | pharmacy | finance | virtual | support | engagement | people | insights | admin
  icon: BedRegular,
  order: 10,
  links: [{ label: 'Bed board', to: '/inpatient/beds', permission: 'inpatient.admissions.read', keywords: ['ward', 'occupancy'] }],
  quickActions: [{ label: 'Admit patient', to: '/inpatient/admit', permission: 'inpatient.admissions.admit', icon: PersonAddRegular, description: 'Assign a bed' }],
  widgets: [{ id: 'inpatient-census', permission: 'inpatient.admissions.read', span: 2, order: 30, component: CensusWidget }],
};
export default manifest;
```

* `permission` uses the server permission names (see `docs/modules/<module>.md`); a string or an array means
  "any of". Links a user may not open are hidden. The server enforces permissions again on every call.
* Icons: use only names that exist in `@fluentui/react-icons` 2.0.342 (`*Regular`). The full list of valid names is in
  `fluent-icons-regular.txt` in the session scratchpad; when unsure, `grep -x Name` it.

## 3. Data access

```ts
import { api } from '@/lib/api';
export const bedQueries = {
  board: (wardId?: string) => queryOptions({
    queryKey: ['inpatient', 'bed-board', wardId ?? 'all'],
    queryFn: ({ signal }) => api.get<BedBoard>('/api/v1/beds/board', { query: { wardId }, signal }),
    refetchInterval: 60_000,
  }),
};
```

* `api.get/post/put/patch/delete/blob` add auth, retries and an **Idempotency-Key** on every write, so a WiFi retry
  never duplicates a bill or an order. Never call `fetch` directly.
* Types mirror the C# records in camelCase; enums arrive as **strings** (`'Admitted'`), dates as ISO strings,
  `DateOnly` as `yyyy-MM-dd`, money as numbers (INR).
* Put filters and paging in the **URL search params** (`validateSearch`) so Back/Forward and bookmarks work.
* Use `keepPreviousData` for paged lists; invalidate the module's query keys after mutations.
* Live updates: `useRealtimeEvent(topic, eventName, handler)` from `@hc360/core`, then invalidate queries. Topics:
  `facility:{id}` (auto-joined), `queue:{deptId}`, `beds:{facilityId}`, `ward:{id}`, `patient:{id}`, `lab:{facilityId}`,
  `pharmacy:{facilityId}`, `tele:{sessionId}`, `ot:{facilityId}` (see each module doc for event names).
* Optimistic concurrency: detail DTOs carry `version`; send it back on update. A 409 means someone else changed it:
  toast, refetch, let the user redo.

## 4. Screens and components (`@hc360/ui`)

* Anatomy: `<Page>` → `<PageHeader title subtitle breadcrumbs actions>` → `<Section title description actions flush?>`.
  One primary button per screen, top right. PageHeader also sets the window title.
* Lists: `DataTable` (columns with `priority: 'low'` hide on tablets; `onRowClick`; `rowActions`; server paging via
  `page/pageSize/totalCount/onPageChange`; built-in loading/empty/error states). Filters in a `FilterBar`. The table
  lays out its columns from the header (fixed layout). It measures the `rowActions` buttons itself and gives their
  column the widest row's width, so no width is needed. To line them up at the end of the column, wrap them in a flex
  container with `justifyContent: 'flex-end'`, as the appointments list does. Cell text stays on one line unless the
  cell sets `whiteSpace: 'normal'`; let long text wrap rather than force the table wider than the screen. Columns
  without a `width` share what the others leave equally, down to nothing: give the one that matters (the patient, the
  service) a `minWidth`, below which the table scrolls sideways in its box, and the less important ones a `width`, in
  pixels or in per cent to grow with the table. Size for Segoe UI, which is wider than the fonts of a Mac or a Linux
  desktop. A name with badges (`WorklistPatient`): a wrapping flex row, the name `<Text wrap={false} truncate
  title={name}>` with `minWidth: 0` so that it ends in an ellipsis, and the badges in a group that goes onto a line of
  its own when the cell is narrow.
* Feedback: `LoadingState`, `EmptyState` (always say what to do next), `ErrorState` (never raw errors),
  `StatusBadge` (colour from the status word), `KpiCard`, `ConfirmDialog` (every destructive/irreversible action),
  `useNotify()` toasts (success after saves; `notify.error(error, 'Could not …')` on failures).
* Forms: react-hook-form + zod (`zodResolver`) + `TextField`, `TextAreaField`, `NumberField`, `SelectField`,
  `DateField`, `SwitchField`, `ChoiceField`, `LookupField`, laid out with `FormGrid`. On submit errors call
  `applyServerErrors(error, form.setError)` so server validation lands on the right field. Long clinical forms use
  `useFormDraft(form, key)` (auto-save to the workstation) and `useHotkey('mod+s', …)`. A page's shortcuts do nothing
  while a dialog or drawer is open over it; a shortcut that belongs to a dialog passes its surface as `within` (a ref
  on `DialogSurface` or `OverlayDrawer`).
* Unsaved changes (SRS UX-08): every form a user types into asks before the typing is lost. See *Unsaved changes*
  below for how a form gets the guard.
* Patient context: show `PatientBannerWithFlags` (`src/modules/emr/PatientBannerWithFlags.tsx`) on every
  patient-specific screen. It is `PatientBanner` plus the patient's allergies and alerts (EHR-03), loaded for staff
  allowed to see them and kept live. A dialog or counter that acts on a patient without a banner shows
  `PatientFlagsStrip` instead. Pick patients with `PatientPicker` (`src/modules/patients/PatientPicker.tsx`); link to
  the profile at `/patients/$patientId`.
* Documents the server makes (lab, radiology and cardiology reports, prescriptions, discharge summaries, the full
  health check-up report, A4 bills): print and download them from the server, so the printed copy is the PDF the
  patient gets. Use `<DocumentButtons path={…Document(id)} />` (`src/lib/DocumentButtons.tsx`) or `useServerDocuments()`
  (`src/lib/documents.ts`): Print fetches `<path>/document` and prints it, Download PDF saves `<path>/pdf` and explains a
  503 (`pdf.unavailable`). Never rebuild these documents in the browser.
* Printing anything else (slips, labels, 80 mm receipts, internal lists): build HTML with
  `printDocument(body, { title, pageSize, styles, template })` and send it with `printHtml(...)` (`@hc360/core`); silent
  printing on counters (`{ silent: true, pageSize: 'Receipt80' }`), dialog otherwise. Start a document (not a label)
  with the facility letterhead: `const letterhead = useLetterhead()` (`src/lib/letterhead.ts`), then
  `letterheadHtml(letterhead, templateFor(letterhead, key), { compact, taxIds, aside })` at the top and
  `template: templateFor(letterhead, key)` for `printDocument`, where `key` is the document's print template (`bill`,
  `receipt`, `sales-invoice`, `purchase`, `clinical-note` or `other`; see `docs/modules/print-templates.md`). Barcodes/QR:
  `await barcodeSvg(value, 'code128' | 'qrcode')` (the encoder loads on demand). Escape every value with `escapeHtml`.
* Formatting (`@hc360/core`): `formatInr`, `formatDate`, `formatDateTime`, `formatTime`, `formatDateOnly`,
  `formatRelative`, `formatPhone`, `humanize` (enum → words). The hospital runs on IST (`HOSPITAL_TIME_ZONE`).
* Charts: `EChart` from `@/lib/EChart` (line/bar/pie), lazy per route.

### Unsaved changes (SRS UX-08)

What users see, in both apps: with unsaved changes, closing a dialog or drawer (Cancel, the close button, Esc, a click
outside) asks **Discard your changes?** (*Keep editing* / *Discard changes*), and leaving the screen (a menu link, Back,
a link in a dialog, signing out, switching facility) asks **Leave without saving?** (*Stay* / *Discard changes*).
Closing or reloading the window has the browser ask. After a successful save nothing asks; while a save runs, closing
does nothing. One blocker in each app's root route (`src/shell/LeaveGuard.tsx`) asks for every form; forms only say
whether they have changes. The mechanism is `@hc360/ui` `forms/unsavedChanges.tsx`.

`dirty` is react-hook-form's `form.formState.isDirty`, or for fields kept in `useState`,
`useValuesChanged({ title, notes, lines })` (compares with the values the form opened with; a dialog that stays mounted
passes its `open` as the second argument) or `!sameValues(draft, saved)`.

* **Shared components ask already:** `EditorDrawer` (its `dirty` prop is required), `MasterDrawer` (`useEditorGuard`),
  the module `ReasonDialog`s (the typed reason counts; pass `dirty` for extra fields in `children`), and the portal's
  `Sheet` (pass `guard`).
* **A dialog or drawer of your own:**

  ```tsx
  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, open, onClose });
  <Dialog open={open} onOpenChange={guard.onOpenChange}>
    <DialogSurface>
      <form>… <Button onClick={guard.requestClose} disabled={busy}>Cancel</Button> …</form>
      {guard.dialogs}
    </DialogSurface>
  </Dialog>
  ```

  Pass `open` when the component stays mounted while closed. Render `{guard.dialogs}` inside the dialog's surface or the
  drawer (the question opens in a layer of its own, over the form). After a save, close with the parent's `onClose`
  (or `guard.close`), never `requestClose`. A result view
  shown after saving is not dirty: `dirty: form.formState.isDirty && !result`. The portal imports `useUnsavedChanges`
  from `@/lib/unsavedChanges` (its question is translated).
* **A dialog whose form is a child mounted while it is open** (`{open && <Body />}`): the dialog takes
  `const closing = useCloseRequests({ onClose })`, `onOpenChange={closing.onOpenChange}` and wraps the child in
  `<CloseRequestScope value={closing}>`; the child calls `useUnsavedChanges` as above. Esc and a click outside then go
  through the child's guard.
* **A form that is the screen** (or a panel on it): `useLeaveGuard({ dirty, busy })`. After saving in place, reset the
  form to what was saved (`form.reset(values)`) so it is no longer dirty. A save that navigates in its `onSuccess` is
  let through while `busy`; one that navigates after `await mutateAsync(...)` calls `allowLeaving()` first. A screen's
  form is lost when the path changes (`leavesScreen`); a dialog's or drawer's also on Back and Forward (`leavesDialog`,
  the default of `useUnsavedChanges`), since going back closes a dialog opened through the address (`?send=true`).
  `leaves` changes either (the dashboard editor asks on any navigation). A form that keeps a
  draft on the workstation (`useFormDraft`, `useDraft`) passes `leaveQuestion: LEAVE_KEEPING_DRAFT`, which says so and
  is not destructive.
* **Leaving outside the router** (sign-out, facility switch): `if (!(await confirmLeaving())) return;` before it starts;
  the navigations that follow are let through. A session that ended (idle sign-out, revoked) navigates with
  `ignoreBlocker: true`: nothing can be saved any more.
* **Not guarded:** filters and searches, read-only views, password prompts, and dialogs whose only field is the
  parameter of a one-step action (a date, a printer, one choice).
* **Focus:** the question returns focus to where the user was (Cancel, the close button, the field Esc was pressed
  in); the dialog's opener still needs `useRestoreFocusTarget()` for focus to come back to it after the dialog closes.
* **Tests:** `apps/staff/src/test/unsavedChanges.test.tsx` and `packages/ui/src/forms/unsavedChanges.test.tsx`. In
  jsdom, Tabster returns focus on closing a dialog only for keyboard users, so answer the question with the keyboard
  (`button.focus()` + `{Enter}`) when a test checks where focus goes.

## 5. UX rules (non-negotiable)

1. **Plain language** a nurse understands: "Admit patient", not "Create admission entity". Sentence case. No jargon,
   no codes without their meaning, no exclamation marks.
2. **Fast for daily work**: the common path needs the fewest clicks; sensible defaults; `autoFocus` on the first field;
   Enter submits dialogs; keyboard shortcuts for console-style screens (show them in tooltips/labels).
3. **Safe**: confirm irreversible actions; show who/when on clinical records; never lose typing (drafts, and the
   unsaved-change guards above);
   disable submit while saving; show server-side safety alerts (e.g. drug interactions) prominently, not as toasts.
4. **Every state designed**: loading skeleton, empty (with next step), error (with retry), no-permission.
5. **Accessible**: every input has a label; icon-only buttons have `aria-label`; colour is never the only signal;
   dialogs trap focus (Fluent does); tables are keyboard navigable (DataTable does). Check new screens with axe (§6).
6. **Responsive**: works at 1280×720 workstations and 768 px tablets; hide low-priority columns, stack forms.
7. **India**: INR with Indian digit grouping, DD MMM YYYY dates, IST, 10-digit mobiles, GST where money is shown.
8. **Privacy**: show only what the task needs (mask phone numbers in lists; full details on the profile), and never put
   patient data in URLs beyond ids.

## 6. Accessibility (SRS UX-10)

The target is WCAG 2.2 AA: contrast, visible focus, names for screen readers, and targets of at least 24 px, in the
light, dark and high-contrast themes (UX-01). Fluent components bring most of it. What a screen adds (labels,
icon-only buttons, its own ARIA, its own colours) is checked automatically where jsdom can, and in a browser where it
cannot.

**Automated checks.** `expectNoAxeViolations(context?)` from `@hc360/ui/testing` runs axe-core with every rule for WCAG
2.0, 2.1 and 2.2 at levels A and AA. Without an argument it checks the whole page: do that once a dialog, drawer, menu
or popover is open, because Fluent renders them in a portal, outside a component's container. A failing test lists
each problem, the element and how to fix it. It fails on what axe only asks a person to review, too: axe reports some
problems only that way (two fields with the same id; an `aria-label` on a plain `div` or `span` that has text, which
screen readers never read). The few review results that are right as they are, such as the page behind an open modal
dialog, are listed with the reason in `REVIEWED` in `packages/ui/src/testing/axe.ts`.

| Checked | Where |
|---|---|
| The check itself: what it fails on and what it leaves alone | `packages/ui/src/testing/axe.test.ts` |
| Shared components: DataTable (rows, sorting, loading, empty, error, paging), form fields with and without errors, ConfirmDialog, badges, states, page anatomy, patient banner | `packages/ui/src/accessibility.test.tsx` |
| Staff frame: navigation (wide, and over the screen on a tablet), top bar, command palette, notifications, account menu, lock screen, sign-in | `apps/staff/src/test/accessibility/shell.test.tsx` |
| Staff screens, whole, with canned data: patients, registration, queue, appointments, bills, OP billing (visits to bill, a draft bill), doctor's worklist and consultation, lab result entry, dispensing, bed board, nursing station, an admission | `apps/staff/src/test/accessibility/*.test.tsx`, data in `fixtures/` |
| Staff dialogs and drawers the modules share (reason dialogs, editor drawers), open and busy | `apps/staff/src/test/accessibility/dialogs.test.tsx` |
| Portal pages: sign-in, home (phone and computer, English and Hindi), appointments, reports, profile | `apps/portal/src/accessibility.test.tsx` |

**Adding a check for a screen.** Render the screen whole against canned data (`src/test/appScreen.tsx`), and check it
once its data is on screen, not its skeleton:

```tsx
// apps/staff/src/test/accessibility/<area>.test.tsx: copy the vi.mock of RealtimeProvider, and the afterEach that
// expects server.unanswered to be empty, from frontOffice.test.tsx.
describe('ward transfers', SCREEN_TESTS, () => {
  it('the list', async () => {
    server = fakeServer({ 'GET /api/v1/inpatient/transfers': paged(TRANSFERS) });
    openScreen('/inpatient/transfers'); // the real router, shell and screen, signed in
    expect(await screen.findByText('Asha Devi', {}, SLOW)).toBeInTheDocument();
    await expectNoAxeViolations();
  });
});
```

* `fakeServer` answers by method and path, and lists what it could not answer in `unanswered`. Answer everything the
  screen asks for, or the check sees an error state instead of the screen. Type the canned data with the module's DTOs,
  and put the data in `fixtures/` (no test framework in there): the browser checks use it too.
* `SLOW` and `SCREEN_TESTS` allow for the first screen of a file, which also loads its route's code.
* Then open its dialogs, menus and other states as a user does (user-event), and check again.
* Fix a problem in the component. If a rule truly cannot apply, pass `{ skip: { '<rule id>': '<why>' } }`.

**What needs a browser: `pnpm test:a11y`.** jsdom does no layout and paints nothing. The checks above therefore leave
out colour contrast (axe could only say "cannot tell") and target size (every box is 0 × 0 at the same spot, so axe
would call each control covered by the next, and pass it), and they cannot see focus rings, clipping or overlap.
Playwright checks those in Chromium (`src/web/playwright.config.ts`, CI job "Accessibility in a browser"):

* It builds the staff app and the portal into their own `dist` folders (never the server's `wwwroot`) and serves them
  with `vite preview` on 127.0.0.1:5380 and :5381. No HC360 Server is needed: `serveFakeApi` (from
  `@hc360/ui/testing/browser`) answers `/api`, `/health` and the SignalR hub in the browser (`page.route`,
  `page.routeWebSocket`) with the fixtures the jsdom tests use, through `createFakeFetch`
  (`@hc360/ui/testing/fake-api`). The session comes back from a canned `POST /api/v1/auth/refresh`, as after a reload.
  A request without a canned answer fails the test.
* Each screen is checked once its data is on screen, in the light, dark and high-contrast themes, with transitions and
  animations off. `expectAccessibleScreen` runs axe-core with every WCAG 2.0, 2.1 and 2.2 A/AA rule (`WCAG_TAGS`,
  `color-contrast` and `target-size` included), then walks the screen with Tab: every stop must show a focus indicator
  (an outline, box shadow or border on the element, its nearest ancestors or their `::before`/`::after`, where Fluent
  draws its ring, that it does not have unfocused) and must not be outside the window or covered by something such as
  a sticky bar (WCAG 2.4.7, 2.4.11). The walk starts at "Skip to main content" and goes on from the main content; the
  frame's own walk starts at the top; in an open dialog it starts where focus is. One report lists everything found,
  attached to the test with a screenshot and a trace.
* What axe only asks a person to review fails too, unless a second look in the browser can tell (`secondLook` in
  `packages/ui/src/testing/browser.ts`): text on a gradient (the sign-in pages' brand panel) is measured against each of
  the gradient's colour stops; text on an opaque drawer or dialog, whose lines lie over different parts of the page
  beneath (all axe compares), is measured against what is painted under each line; text partly scrolled out of view
  is scrolled into view and checked again. What is still undecided fails.
* The portal follows the device's theme: dark is the device's dark mode, and high contrast is forced colours, where the
  browser paints the patient's own system colours over the portal's. axe reads the page's colours rather than those, so
  `color-contrast` is left out there (target size and focus are still checked); the high-contrast theme's own colours
  are checked in the staff app, which offers it in the account menu.

| Checked in Chromium | Where |
|---|---|
| The checks themselves: contrast (and on a gradient), target size, the focus walk (no indicator, covered, the skip link) | `packages/ui/a11y/checks.spec.ts` |
| Staff app, light, dark and high contrast at 1280 px: sign-in, a front desk's home page, patient search, registration, the patient chart, queue, appointments, the appointment calendar, the doctor's worklist, a consultation with the patient banner, OP billing (a draft bill), bills, system health; the frame (top bar and navigation), the command palette, notifications, the account menu. At 768 px: registration, queue, worklist, OP billing, the navigation opened over the screen | `apps/staff/a11y/screens.spec.ts` |
| Portal on a phone, light, dark and forced colours: sign-in and the code step, home and its More menu, appointments, find a doctor, a doctor's page with a time chosen, booking, bills, reports, profile, messages from the hospital (preferences). On a computer: sign-in, home, appointments, a doctor's page, bills, reports | `apps/portal/a11y/pages.spec.ts` |

Run it with `cd src/web && pnpm test:a11y` (a minute to build, three or four to check). The report opens by itself
when something fails; `pnpm exec playwright show-report` opens it again. Locally Playwright uses the Chromium that
matches its pinned version: `pnpm exec playwright install chromium` once, unless `PLAYWRIGHT_BROWSERS_PATH` already
has it. To add a screen, add an entry to the spec's list: its path, the canned answers (from the `fixtures/` of the
jsdom checks, typed with the module's DTOs) and what shows once its data is there. Fix what it finds in the component
or, better, in the theme or the shared component it comes from; `{ skip: { '<rule id>': '<why>' } }` is only for a rule
that cannot apply. What a run leaves behind (each app's `dist`, `playwright-report`, `test-results`, `blob-report`) is
ignored by git, `pnpm lint` and `pnpm format:check`. Each package that imports `@playwright/test` (or
`@axe-core/playwright`, as `@hc360/ui/testing/browser` does) declares it as a dev dependency, at the version the
workspace root pins.

**Fonts.** CI's Ubuntu runner has none of the theme's named fonts, so Chromium paints there in DejaVu Sans, which is
about as wide as Segoe UI on Windows. A Mac, or a Linux desktop whose fonts stand in for those names (Inter, Noto
Sans…), paints narrower text, and a column that fits there can overflow in CI: axe then cannot tell the colours behind
text that spills over its neighbours, and fails the check. On Linux, check with the runner's fonts:
`FONTCONFIG_FILE="$PWD/a11y/fonts.conf" pnpm test:a11y` (`src/web/a11y/fonts.conf`; DejaVu Sans must be installed).
It is not set in `playwright.config.ts`: Chromium on Windows and macOS does not use fontconfig, and a Linux machine
that sets a `FONTCONFIG_FILE` of its own needs to keep it.

Tabster's focus sentinels (`[data-tabster-dummy]`) are left out of every check: they are hidden from screen readers and
pass focus on the moment they get it, but axe reports each one.

**Spinners.** A Fluent `Spinner` is a progress bar to a screen reader, read only as "progress bar" without a name.
Hide one that sits beside words already saying what is happening, such as a busy button's icon (the button's label,
and its unavailable state, say it): `icon={busy ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}`. Name one
that stands for something loading: `label="Loading…"`, or `aria-label` to keep it small. The lint rule refuses a
`Spinner` with neither (`no-restricted-syntax` in `eslint.config.js`). A test that waits for a busy button finds its
hidden spinner with `findByRole('progressbar', { hidden: true })`; waiting for `aria-disabled` instead would turn a
button that went back to `disabled` into a timeout rather than a failure on lost focus.

**Colours of your own.** Use theme tokens, and pair them: a background token with the foreground token of the same
state (`colorNeutralBackground1Selected` with `colorNeutralForeground1Selected`, `…Hover` with `…Hover`). In the
high-contrast theme the selected and hover backgrounds are the highlight colour (cyan), on which the usual white text
has 1.5:1. Brand text: `colorBrandForeground1` on the neutral backgrounds and on `colorBrandBackground2`,
`colorBrandForeground2` on `colorBrandBackground2Hover` (the HC360 light theme draws both one step darker than Fluent
does, for 4.5:1 on those). Text boxes fill their column and no more (`useFieldSizing` in the theme); a row of a field
and its unit inside a `FormGrid` cell needs `minWidth: 0` on the part that should give way.

## 7. Verify before you report

```
cd src/web
pnpm --filter @hc360/staff typecheck      # regenerates the route tree, then tsc (must be clean)
pnpm --filter @hc360/staff test           # vitest (add *.test.ts for pure logic; don't import Fluent in tests)
npx eslint apps/staff/src/modules/<yours> apps/staff/src/routes/_app/<yours>   # 0 errors (CI runs lint)
pnpm test:a11y                            # after changing colours, focus styles, layout or the size of controls (§6)
```

The React Compiler lint rules (eslint-plugin-react-hooks 7) reject two common patterns:
- **Calling `setState` synchronously inside `useEffect`.** Instead:
  - derive the value during render;
  - adjust state during render when an input changes: `if (prev !== x) { setPrev(x); setY(…) }`;
  - or remount with a `key`.
- **Writing `ref.current` during render.** Use `useEffectEvent` for "latest callback" needs, and touch refs only in
  effects and event handlers.

Do **not** run `vite build` or the dev server (several people work in the app at once and the build output is
shared). `pnpm test:a11y` is fine: it builds into each app's own `dist` folder and serves on ports of its own (5380,
5381). Keep tests for pure functions (calculators, mappers, validators) next to the code as `*.test.ts`.
