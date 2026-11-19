# HC360 training videos

Role-based training for Nana Hospitals, Theni: one short video per built-in role, three shared videos every member of
staff watches first, all made from the real HC360 application. Each video is a **storyboard** in this folder: what to
do on the screen and what the narrator says. The pipeline runs the storyboard on a training server as that role, records
the screens, speaks the narration in Indian English, puts the video together with captions, and checks it. When a
screen changes in a release, the storyboard fails (or the screen looks different) and the video is made again.

- `curriculum/`: what each role sees and which video teaches it (`role-menus.json`, written from the code by
  `pipeline/role-menus.mjs`; `README.md`, written by `pipeline/curriculum.mjs`).
- `storyboards/`: one file per video.
- `pipeline/`: the tools. `out/` (not committed) holds the videos.

## The curriculum

Every member of staff watches the three shared videos, then their role's video. Role videos are grouped by the
go-live wave of their department ([MODULE-ROLLOUT-PLAN.md](../docs/deployment/MODULE-ROLLOUT-PLAN.md), section 5):
train a department just before its wave, on the training server, and judge it by the plan's own test: each user
finishes the department's day script unaided (section 6, step 7).

| Video | For |
|---|---|
| `00-getting-started` | Every member of staff: sign in, home page, menu, search, alerts, connection, lock and sign out |
| `01-everyday-tools` | Every member of staff: My HR (leave), incident reporting, approvals (the two-person rule) |
| `02-find-patient` | The 22 roles with patient lists: find the right patient (two identifiers), read the record, where every patient is |
| `w<wave>-<role>` | One role: its daily tasks, done on the real application |

## Making the videos

On a Linux machine with .NET 10, Node 22, PostgreSQL 16, libfaketime, ffmpeg (with libass) and Chromium for
Playwright. Never on a hospital's server: the videos register patients, bill and dispense.

```bash
training/pipeline/setup-models.sh                       # the voice and the checking recogniser, offline; Python venv
dotnet build src/server/HyFib.HealthCare360.Server -c Release -p:RunAnalyzers=false   # needs about 14 GB of memory
(cd src/web && pnpm install && pnpm -r --filter './apps/**' build)
```

The server project compiles 6.9 million lines of migration snapshots: give the compiler about 14 GB (swap is fine)
and run it alone (`DOTNET_GCHeapHardLimit=0x600000000`, `-m:1`).

Then the training server and its morning (secrets in `~/.hc360-training.env`, see `pipeline/server.sh`):

```bash
training/pipeline/server.sh clock 09:01 && training/pipeline/server.sh start   # an empty database: migrates and seeds
node src/web/demo/story.mjs story.json          # the morning, 9:00 to 11:10 am (DEMO_CLOCK_FILE, NODE_EXTRA_CA_CERTS)
node training/pipeline/accounts.mjs             # an account for each role the demo has none for
training/pipeline/server.sh snapshot story
node training/pipeline/take-on.mjs              # what a department takes on at go-live (leave balances, ...)
training/pipeline/server.sh snapshot morning    # every video starts from here, at 11:10 am
```

```bash
training/pipeline/check.sh 02-find-patient      # an author's check: runs the storyboard, lists its screenshots
training/pipeline/make.sh 02-find-patient       # the video: capture, narrate, compose, check
training/pipeline/make.sh --all
training/pipeline/retell.sh 02-find-patient     # after a change of wording only: new words over the same screens
```

`retell.sh` needs no training server: the words are the storyboard's fields alone (`pipeline/narration.mjs`), so the
narration, the cards and the ending can be made again over the screens already captured on that machine. It refuses
when the steps themselves changed (a step added or removed, or a different action): that needs `make.sh`.

## Writing a storyboard

Find what a screen says to a screen reader, signed in as the role, before writing a step that points at it:

```bash
node training/pipeline/explore.mjs pharmacist /pharmacy/queue shot.png --click "button=Dispense"
```

A storyboard (see `storyboards/02-find-patient.mjs`) exports `id`, `title`, `role` (the built-in role, or `*`),
`roleLabel`, `user` (the training account), `wave`, `audience`, `intro`, `objectives` (2 to 4, each one task),
`modules` and `covers` (the menu paths it teaches, for the coverage table), `scenes` of `steps`, `recap`, `check`
(two questions with answers), and for a shared video `next`, the title of the video every learner watches next.
A role's video is the last on that role's path, so it ends with practice instead: on the training server until the
learner can finish the department's day script without help, the check before go-live
([MODULE-ROLLOUT-PLAN.md](../docs/deployment/MODULE-ROLLOUT-PLAN.md), section 6, step 7). `outro` (the last words)
and `endLines` (the end card's lines) replace the ending, as the patient portal's does for patients. Options: `desk: true` sets the workstation's desk (queue at General Medicine,
room OPD-01; nurses' station on the female ward; OP counter 1); `signIn: 'on-screen'` films the sign-in.
`setup: async ({ api }) => { ... }` makes data that other roles would have made earlier in the day, through the API
as those roles (`await api('purchase')`), after the training server is restored and before the filmed steps: a
purchase order for the store keeper to receive, a sample for the pathologist to verify.

A step is one thing the viewer sees happen, with what the narrator says before it (`say`) and, if the screen changes,
after it (`after`):

| Key | Does |
|---|---|
| `nav: 'Queue'` | clicks a menu button (areas expand; an area with one screen opens it) |
| `click: target` | clicks |
| `fill: [target, text]` | types, filmed as it is typed; `secret: true` for a password |
| `select: [target, 'Option']` | chooses an option; `native: true` for a plain drop-down (`<select>`) |
| `press: 'Control+k'` | presses keys |
| `show: target` | points at something without changing it: outlined, the rest dimmed |
| `go: '/path'` | opens a screen directly (setup only: teach the menu with `nav`) |
| `wait`, `expect` | what must appear after the step: the run fails if it does not |
| `result` | what to outline after the step (defaults to `wait`) |
| `zoom: 1.4` | magnifies the step's region (forms, small text) |
| `before`, `after_do` | `async ({ page, find, api, state }) => ...` just before the step is filmed, or after its action: for what has no step type, such as `page.setInputFiles(...)` on `{ css: 'input[type=file]' }` |

Targets are named as a person reads the screen: `{ role: 'button', name: 'Send report' }`, `{ label: 'Reason' }`
(a field's accessible name), `{ role: 'row', name: /Fatima Begum/ }`, `{ text: 'Now serving' }`,
`{ within: target, ... }`. Names must match what `explore.mjs` prints; a name can be a regular expression.
A dialog's name includes its subtitle ("Attach the tracing Electrocardiogram..."): match it with a regular expression.

## How the narrator speaks

The audience speaks Tamil first and English well. The narration is written for them:

- Short sentences, one idea each, about 20 words at most. Present tense, active voice: "Click Send report."
- The screen's own words, spelled as the screen spells them, in the captions too: "UHID", "GWF-01", "₹1,930",
  "10:47 am". The pronunciation lexicon (`pipeline/lexicon.json`) and the number rules in `pipeline/narrate.py` turn
  them into what an Indian hospital says ("U H I D", "one thousand nine hundred and thirty rupees"). Add a term to the
  lexicon rather than spelling it out in a storyboard.
- Say why, once, where it matters for safety or money: "Two patients can have the same name."
- Name the patient and what to look at: "Vimala Raghavan is with the doctor now."
- No idioms, no jokes, no "simply" or "just".
- Every claim must be true of the screen or the code: read the screen (`explore.mjs`) or the source before saying how
  HC360 behaves. A step whose `after` claims a result has a `wait` or `expect` for it, so the run proves it.
- Give commands as full phrases: "Open the Approvals screen.", not "Open Approvals.". Two-word sentences are the
  ones listeners (and the recogniser) most often miss.
- Do not start a line with "Part": this voice's "Part two" is heard as other words. Chapters are announced as
  "First: ...", "Next: ...", "Finally: ...".

## The checks (`pipeline/qa.py`)

Each video is checked on the finished file, by tools other than those that made it:

| Gate | Passes when |
|---|---|
| Run | every step found its target and its expected result; the server answered no request with an error |
| Speech | Whisper small.en, an independent recogniser, hears the narration with a word error rate of 5% or less |
| Pace | 120 to 150 spoken words a minute ("₹1,930" is five spoken words) |
| Captions | 2 lines of 42 characters at most, 0.8 to 7 s on screen, 17 characters a second at most, word for word |
| Loudness | -16 LUFS (EBU R128), true peak -1 dBTP or lower |
| Format | H.264 1920x1080 at 30 fps, AAC 48 kHz, chapters |

The recogniser was trained mostly on Western English. The voice says *p*, *t* and *k* without the puff of air,
as Indian English does, so the recogniser now and then hears "pause" as "bose". Names of people and places are
scored as heard whatever the recogniser writes: it cannot spell Tamil names it has never met ("Kalaivani" comes back as
"kalevani"), and the captions show them as they are. That is the accent the audience
knows, not an error for them, and it stays well under the 5% gate.

What machines cannot check needs people: the department head reviews the department's video before its wave (are
these the steps our desk follows?), and pilot users finish the day script on the training server after watching it.

## The voice

The narrator is Kokoro v1.0 (Apache-2.0), voice `hf_alpha`, an Indian English voice, run offline with sherpa-onnx.
Nothing leaves the machine and there is no charge per minute. The cloud Indian English voices (Microsoft, Google) were
not reachable from the build machine; `pipeline/narrate.py` is the one place to change if the hospital prefers one.

Chosen on evidence. Each candidate read the same script, and Whisper small.en scored what it heard:

| Voice | Licence | Words a minute | Word error rate |
|---|---|---:|---:|
| Kokoro `hf_alpha` (Indian, female) | Apache-2.0 | 139 | 0.0% |
| Kokoro `hm_omega` (Indian, male) | Apache-2.0 | 143 | 0.0% |
| Kokoro `hf_beta`, `hm_psi` (Indian) | Apache-2.0 | 155, 149 | 0.0% |
| VCTK p376, p251, p248 (Indian English speakers, Piper) | CC BY 4.0 | 172 to 199 | 6.7 to 11.5% |
| Kokoro `bf_emma` (British, for comparison) | Apache-2.0 | 162 | 2.9% |

AI4Bharat's Indian English voice (IIT Madras recordings) was not used: its weights carry no licence that allows a
hospital's commercial use; HyFib could ask AI4Bharat. Whether a voice *sounds* right to Tamil-speaking staff is for
them to judge: `HC360_VOICE` (`hf_alpha`, `hf_beta`, `hm_omega`, `hm_psi`) and `HC360_SPEED` change it for every video.
