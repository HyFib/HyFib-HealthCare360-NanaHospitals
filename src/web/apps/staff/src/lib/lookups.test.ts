import { buildUrl, type RequestOptions } from '@hc360/core';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from './api';
import { searchOtNurses } from './lookups';

vi.mock('./api', () => ({ api: { get: vi.fn() } }));

describe('the OT nurse picker', () => {
  beforeEach(() => {
    vi.mocked(api.get).mockReset().mockResolvedValue([]);
  });

  it('asks the practitioner lookup for nurses and OT technicians only, as the server reads repeated kinds', async () => {
    await searchOtNurses('Lakshmi');

    const [path, options] = vi.mocked(api.get).mock.calls[0]!;
    expect(buildUrl('', path, (options as RequestOptions | undefined)?.query)).toBe(
      '/api/v1/masters/lookups/practitioners?search=Lakshmi&kind=Nurse&kind=Technician',
    );
  });
});
