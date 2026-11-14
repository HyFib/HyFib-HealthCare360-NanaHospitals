import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { DowntimePack } from '@/modules/downtime/continuityApi';
import {
  ageText,
  clearOfflineCopy,
  columnsOf,
  columnTitle,
  copyOf,
  openOfflineCopy,
  PACK_SLOT,
  readKeptCopy,
  rowsOf,
  takeOfflineCopy,
  useOfflineCopy,
} from './offlineCopy';
import { deriveOfflineKey, memoryBackend, seal, unseal } from './vault';

// The offline copy: kept on the computer only as ciphertext under a key derived from the session's, readable only by
// the same user and slot, opened again after a reload of the tab, and wiped at sign-out.

const SERVER_KEY = btoa(String.fromCharCode(...Array.from({ length: 32 }, (_, i) => i + 1)));
const OTHER_KEY = btoa(String.fromCharCode(...Array.from({ length: 32 }, (_, i) => 200 - i)));

const PACK: DowntimePack = {
  id: 'p1',
  kind: 'Nightly',
  generatedAt: '2026-11-08T20:30:00Z',
  windowEndsAt: '2026-11-09T20:30:00Z',
  facilityName: 'Nana Hospitals',
  generatedByName: null,
  patients: 1,
  sections: [
    {
      key: 'census',
      title: 'Census by ward',
      note: null,
      pagePerTable: false,
      tables: [
        {
          heading: 'Ward A',
          note: null,
          alert: null,
          columns: ['Bed', 'Patient (UHID)'],
          rows: [['A-1', 'Asha Rao (NH26000001)']],
        },
      ],
    },
  ],
};

function server(answers: Record<string, unknown>) {
  return async (path: string) => {
    if (!(path in answers)) {
      throw new Error('404');
    }
    return answers[path];
  };
}

beforeEach(async () => {
  await clearOfflineCopy(null);
  sessionStorage.clear();
  localStorage.clear();
});

describe('the sealed record', () => {
  it('opens only with the same key, user and slot', async () => {
    const key = await deriveOfflineKey(SERVER_KEY, 'u1');
    const record = await seal(key, 'u1:pack', { secret: 'Asha Rao' }, 1);
    expect(record.data).not.toContain('Asha');
    expect(await unseal(key, 'u1:pack', record)).toEqual({ secret: 'Asha Rao' });
    expect(await unseal(key, 'u2:pack', record)).toBeNull();
    expect(await unseal(key, 'u1:worklist:lab', record)).toBeNull();
    expect(await unseal(await deriveOfflineKey(OTHER_KEY, 'u1'), 'u1:pack', record)).toBeNull();
    expect(await unseal(await deriveOfflineKey(SERVER_KEY, 'u2'), 'u1:pack', record)).toBeNull();
  });
});

describe('the offline copy', () => {
  it('is taken while online, kept encrypted, and read back after the tab reloads', async () => {
    const backend = memoryBackend();
    await openOfflineCopy('u1', SERVER_KEY, backend);
    const taken = await takeOfflineCopy({
      backend,
      now: () => 1000,
      fetchJson: server({
        '/api/v1/downtime/pack/latest': PACK,
        '/api/v1/lab/worklist': [{ id: 'x', patientName: 'Asha Rao', test: 'CBC' }],
      }),
    });
    expect(taken).toBe(true);
    expect(useOfflineCopy.getState().takenAt).toBe(1000);
    expect(JSON.stringify([...backend.records.values()])).not.toContain('Asha');

    // The tab reloads: memory is empty, the key kept for the tab opens the copy on the computer.
    useOfflineCopy.setState({ userId: null, takenAt: null, pack: null, worklists: [] });
    expect(await readKeptCopy('u1', backend)).toBe(true);
    expect(useOfflineCopy.getState().pack?.sections[0]?.tables[0]?.rows[0]).toEqual(['A-1', 'Asha Rao (NH26000001)']);
    expect(useOfflineCopy.getState().worklists.map((w) => w.title)).toEqual(['Laboratory worklist']);
  });

  it('is not readable as another user', async () => {
    const backend = memoryBackend();
    await openOfflineCopy('u1', SERVER_KEY, backend);
    await takeOfflineCopy({ backend, fetchJson: server({ '/api/v1/downtime/pack/latest': PACK }) });
    useOfflineCopy.setState({ userId: null, takenAt: null, pack: null, worklists: [] });
    expect(await readKeptCopy('u2', backend)).toBe(false);
  });

  it('is wiped at sign-out, with the key kept for the tab', async () => {
    const backend = memoryBackend();
    await openOfflineCopy('u1', SERVER_KEY, backend);
    await takeOfflineCopy({ backend, fetchJson: server({ '/api/v1/downtime/pack/latest': PACK }) });
    expect(backend.records.has(PACK_SLOT)).toBe(true);
    await clearOfflineCopy(backend);
    expect(backend.records.size).toBe(0);
    expect(sessionStorage.length).toBe(0);
    expect(useOfflineCopy.getState().takenAt).toBeNull();
    expect(await readKeptCopy('u1', backend)).toBe(false);
  });

  it('takes nothing before the session key has come', async () => {
    const backend = memoryBackend();
    expect(await takeOfflineCopy({ backend, fetchJson: server({ '/api/v1/downtime/pack/latest': PACK }) })).toBe(false);
    expect(backend.records.size).toBe(0);
  });

  it('is wiped, from memory and from the computer, when another user signs in here', async () => {
    const backend = memoryBackend();
    await openOfflineCopy('u1', SERVER_KEY, backend);
    await takeOfflineCopy({ backend, fetchJson: server({ '/api/v1/downtime/pack/latest': PACK }) });
    expect(useOfflineCopy.getState().userId).toBe('u1');

    // Another nurse signs in on the same computer (the first one's session ended unseen, say, during an outage).
    await openOfflineCopy('u2', OTHER_KEY, backend);

    expect(backend.records.size).toBe(0);
    expect(useOfflineCopy.getState().takenAt).toBeNull();
    expect(await readKeptCopy('u2', backend)).toBe(false);
  });

  it('is wiped when another user signs in after the app started again, from what the computer kept', async () => {
    const backend = memoryBackend();
    await openOfflineCopy('u1', SERVER_KEY, backend);
    await takeOfflineCopy({ backend, fetchJson: server({ '/api/v1/downtime/pack/latest': PACK }) });
    // HC360 Desktop starts again: nothing of the first nurse is in memory, but her copy is on the computer.
    useOfflineCopy.setState({ userId: null, takenAt: null, pack: null, worklists: [] });
    vi.resetModules();
    const fresh = await import('./offlineCopy');

    await fresh.openOfflineCopy('u2', OTHER_KEY, backend);

    expect(backend.records.size).toBe(0);
  });

  it('in memory is shown to its own user only', async () => {
    const backend = memoryBackend();
    await openOfflineCopy('u1', SERVER_KEY, backend);
    await takeOfflineCopy({ backend, fetchJson: server({ '/api/v1/downtime/pack/latest': PACK }) });

    expect(copyOf(useOfflineCopy.getState(), 'u1')?.pack?.id).toBe('p1');
    expect(copyOf(useOfflineCopy.getState(), 'u2')).toBeNull();
    expect(copyOf(useOfflineCopy.getState(), null)).toBeNull();
    // Asked for by another user, the copy in memory is put away, not handed over.
    expect(await readKeptCopy('u2', backend)).toBe(false);
    expect(useOfflineCopy.getState().pack).toBeNull();
  });
});

describe('words and columns', () => {
  it('says how old the copy is', () => {
    expect(ageText(0, 60_000)).toBe('1 minute');
    expect(ageText(0, 3 * 3_600_000)).toBe('3 hours');
    expect(ageText(0, 3 * 86_400_000)).toBe('3 days');
  });

  it('shows a worklist by its plain fields, not ids or versions', () => {
    const rows = rowsOf({
      items: [{ id: '1', patientId: '2', patientName: 'Asha', status: 'Due', version: 3, isUrgent: true, nested: {} }],
    });
    expect(columnsOf(rows)).toEqual(['patientName', 'status']);
    expect(columnTitle('patientName')).toBe('Patient name');
  });
});
