import { FluentProvider } from '@fluentui/react-components';
import { formatDateTime, formatTime } from '@hc360/core';
import { act, type ReactNode } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { hc360LightTheme } from '../theme/theme';
import { describeStage, formatStageDuration, Pipeline, type PipelineStage } from './Pipeline';

// A visit seen at 11:00 IST on 31 Oct 2026: checked in at 9:00, called at 10:35, in consultation since 10:40.
const NOW = Date.parse('2026-10-31T05:30:00Z');
const CHECKED_IN = '2026-10-31T03:30:00Z';
const CALLED = '2026-10-31T05:05:00Z';
const SEEN = '2026-10-31T05:10:00Z';

const STAGES: PipelineStage[] = [
  { key: 'checked-in', label: 'Checked in', state: 'Done', at: CHECKED_IN, detail: 'Token GM-012' },
  { key: 'called', label: 'Called', state: 'Done', at: CALLED, detail: 'Room 3' },
  { key: 'in-consultation', label: 'In consultation', state: 'Current', at: SEEN, minutesInStage: 20 },
  { key: 'investigations-done', label: 'Investigations done', state: 'Pending', at: null },
  { key: 'dispensed', label: 'Medicines dispensed', state: 'NotApplicable', at: null, detail: 'Nothing prescribed' },
  { key: 'skipped', label: 'Paid', state: 'Skipped', at: null },
  { key: 'closed', label: 'Visit closed', state: 'Stopped', at: null },
];

let container: HTMLDivElement;
let root: Root;

beforeEach(() => {
  (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.append(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
});

function show(ui: ReactNode) {
  act(() => root.render(<FluentProvider theme={hc360LightTheme}>{ui}</FluentProvider>));
}

const items = () => [...container.querySelectorAll('ol > li')] as HTMLLIElement[];

describe('Pipeline', () => {
  it('is an ordered list of the stages in order, named for screen readers', () => {
    show(<Pipeline stages={STAGES} label="Out-patient visit OP2600981" now={NOW} />);

    const list = container.querySelector('ol');
    expect(list?.getAttribute('aria-label')).toBe('Out-patient visit OP2600981');
    expect(items()).toHaveLength(STAGES.length);
    expect(items()[0]?.textContent).toContain('Checked in');
    expect(items()[6]?.textContent).toContain('Visit closed');
  });

  it('marks the stage the patient is in as the current step, and no other', () => {
    show(<Pipeline stages={STAGES} label="Visit" now={NOW} />);

    const current = items().filter((li) => li.getAttribute('aria-current') === 'step');
    expect(current).toHaveLength(1);
    expect(current[0]?.textContent).toContain('In consultation');
  });

  it('says every state in words beside its icon, with the time the stage was reached', () => {
    show(<Pipeline stages={STAGES} label="Visit" now={NOW} />);

    const text = items().map((li) => li.textContent);
    expect(text[0]).toContain(`Done, ${formatTime(CHECKED_IN)}`);
    expect(text[0]).toContain('Token GM-012');
    expect(text[2]).toContain(`Now, 20 min (since ${formatTime(SEEN)})`);
    expect(text[3]).toContain('Pending');
    expect(text[4]).toContain('Not applicable');
    expect(text[4]).toContain('Nothing prescribed');
    expect(text[5]).toContain('Skipped');
    expect(text[6]).toContain('Stopped');

    // The icons only repeat the words: they are hidden from screen readers.
    for (const icon of container.querySelectorAll('svg')) {
      expect(icon.getAttribute('aria-hidden')).toBe('true');
    }
  });

  it('gives the date with the time of a stage reached on another day', () => {
    const admitted = '2026-10-29T04:30:00Z';

    expect(describeStage({ key: 'admitted', label: 'Admitted', state: 'Done', at: admitted }, NOW)).toBe(
      `Done, ${formatDateTime(admitted)}`,
    );
  });

  it('does not make up a time it does not have', () => {
    expect(describeStage({ key: 'scheduled', label: 'Scheduled', state: 'Current', at: null }, NOW)).toBe('Now');
    expect(describeStage({ key: 'scheduled', label: 'Scheduled', state: 'Current', at: CALLED }, NOW)).toBe(
      `Now, since ${formatTime(CALLED)}`,
    );
    expect(describeStage({ key: 'scheduled', label: 'Scheduled', state: 'Done', at: null }, NOW)).toBe('Done');
  });

  it('in its compact form writes out the current stage with its time and names the others for screen readers', () => {
    show(<Pipeline stages={STAGES} label="Out-patient visit OP2600981" compact now={NOW} />);

    const [checkedIn, , current, pending] = items();
    expect(current?.getAttribute('aria-current')).toBe('step');
    expect(current?.textContent).toContain('In consultation');
    expect(current?.textContent).toContain('20 min');
    expect(checkedIn?.getAttribute('aria-current')).toBeNull();
    expect(checkedIn?.textContent).toBe(`Checked in: Done, ${formatTime(CHECKED_IN)}`);
    expect(checkedIn?.getAttribute('title')).toBe(`Checked in: Done, ${formatTime(CHECKED_IN)}`);
    expect(pending?.textContent).toBe('Investigations done: Pending');
  });

  it('counts the time in a stage in minutes, hours and days', () => {
    expect(formatStageDuration(0)).toBe('0 min');
    expect(formatStageDuration(25)).toBe('25 min');
    expect(formatStageDuration(120)).toBe('2 h');
    expect(formatStageDuration(125)).toBe('2 h 5 min');
    expect(formatStageDuration(24 * 60)).toBe('1 d');
    expect(formatStageDuration(3 * 24 * 60 + 4 * 60 + 30)).toBe('3 d 4 h');
  });
});
