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
  cell sets `whiteSpace: 'normal'`; let long text wrap rather than force the table wider than the screen.
* Feedback: `LoadingState`, `EmptyState` (always say what to do next), `ErrorState` (never raw errors),
  `StatusBadge` (colour from the status word), `KpiCard`, `ConfirmDialog` (every destructive/irreversible action),
  `useNotify()` toasts (success after saves; `notify.error(error, 'Could not …')` on failures).
* Forms: react-hook-form + zod (`zodResolver`) + `TextField`, `TextAreaField`, `NumberField`, `SelectField`,
  `DateField`, `SwitchField`, `ChoiceField`, `LookupField`, laid out with `FormGrid`. On submit errors call
  `applyServerErrors(error, form.setError)` so server validation lands on the right field. Long clinical forms use
  `useFormDraft(form, key)` (auto-save to the workstation) and `useHotkey('mod+s', …)`. A page's shortcuts do nothing
  while a dialog or drawer is open over it; a shortcut that belongs to a dialog passes its surface as `within` (a ref
  on `DialogSurface` or `OverlayDrawer`).
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

## 5. UX rules (non-negotiable)

1. **Plain language** a nurse understands: "Admit patient", not "Create admission entity". Sentence case. No jargon,
   no codes without their meaning, no exclamation marks.
2. **Fast for daily work**: the common path needs the fewest clicks; sensible defaults; `autoFocus` on the first field;
   Enter submits dialogs; keyboard shortcuts for console-style screens (show them in tooltips/labels).
3. **Safe**: confirm irreversible actions; show who/when on clinical records; never lose typing (drafts);
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
  and put data a browser check could use too in `fixtures/` (no test framework in there).
* `SLOW` and `SCREEN_TESTS` allow for the first screen of a file, which also loads its route's code.
* Then open its dialogs, menus and other states as a user does (user-event), and check again.
* Fix a problem in the component. If a rule truly cannot apply, pass `{ skip: { '<rule id>': '<why>' } }`.

**What needs a browser.** jsdom does no layout and paints nothing. The checks above therefore leave out colour contrast
(axe could only say "cannot tell") and target size (every box is 0 × 0 at the same spot, so axe would call each control
covered by the next, and pass it). They cannot see focus rings, clipping, overlap or zoom either. After changing
colours, a theme, focus styles or the size of controls, check in a browser:

1. Serve the screens with canned data: a throwaway `apps/staff/preview.html` and `src/__preview__/main.tsx`. It answers
   `/api/*` with `createFakeFetch(answers)` from `src/test/fakeApi.ts` (the fixtures the tests use), signs in
   `STAFF_USER`, and renders `createAppRouter(queryClient, createMemoryHistory({ initialEntries: [path] }))`. Start
   Vite on a port of its own (`pnpm exec vite --port 5373 --strictPort --host 127.0.0.1` from `apps/staff`, never
   5360). Delete both files afterwards.
2. Put `axe-core/axe.min.js` into the page with a `<script>`, and run every WCAG A/AA rule:
   `axe.run({ include: document, exclude: '[data-tabster-dummy]' }, { runOnly: { type: 'tag', values: WCAG_TAGS } })`.
   Do it in the light, dark and high-contrast themes (`localStorage['hc360:theme']`), at 1280 px and at 768 px, and
   with the palette, menus, drawers and dialogs open.
3. Turn transitions off in the page first (`* { transition: none !important }`). A page in a hidden tab or pane never
   finishes a transition, so after a change of theme the colours axe measures would be the old theme's.
4. Look at what axe could not decide. It cannot measure text on a gradient or an image (the sign-in pages' brand
   panel) or text that overlaps something: work out those contrasts by hand.
5. Press Tab through the screen: every stop must show a focus ring that is not hidden behind something else.

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
has 1.5:1.

## 7. Verify before you report

```
cd src/web
pnpm --filter @hc360/staff typecheck      # regenerates the route tree, then tsc (must be clean)
pnpm --filter @hc360/staff test           # vitest (add *.test.ts for pure logic; don't import Fluent in tests)
npx eslint apps/staff/src/modules/<yours> apps/staff/src/routes/_app/<yours>   # 0 errors (CI runs lint)
```

The React Compiler lint rules (eslint-plugin-react-hooks 7) reject two common patterns:
- **Calling `setState` synchronously inside `useEffect`.** Instead:
  - derive the value during render;
  - adjust state during render when an input changes: `if (prev !== x) { setPrev(x); setY(…) }`;
  - or remount with a `key`.
- **Writing `ref.current` during render.** Use `useEffectEvent` for "latest callback" needs, and touch refs only in
  effects and event handlers.

Do **not** run `vite build` or the dev server (several people work in the app at once and the build output is
shared). Keep tests for pure functions (calculators, mappers, validators) next to the code as `*.test.ts`.
