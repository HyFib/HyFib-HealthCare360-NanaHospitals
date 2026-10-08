import { beforeEach, describe, expect, it, vi } from 'vitest';

// The staff app asks the server once a minute whether the facility trains with the labels simulator, and prints
// through the one label print path with the answer.
const get = vi.hoisted(() => vi.fn());
const printLabel = vi.hoisted(() => vi.fn(async () => ({ via: 'page', printer: null })));
const session = vi.hoisted(() => ({
  user: { id: 'u1', facilityId: 'f1' } as { id: string; facilityId: string } | null,
}));
vi.mock('./api', () => ({ api: { get, post: vi.fn() } }));
vi.mock('@hc360/core', () => ({ printLabel, useSession: { getState: () => session } }));

const { forgetLabelMode, printLabelJob } = await import('./labelPrinting');

const job = { kind: 'cssd-packs' as const, labels: [{ packNumber: 'CS1', setName: 'Set' }], page: () => '' };

describe('printing labels', () => {
  beforeEach(() => {
    forgetLabelMode();
    session.user = { id: 'u1', facilityId: 'f1' };
    get.mockReset();
    printLabel.mockClear();
  });

  it('prints with the simulator when the facility trains with it, asking the server once a minute', async () => {
    get.mockResolvedValue({ simulator: true });

    await printLabelJob(job);
    await printLabelJob(job);

    expect(get).toHaveBeenCalledOnce();
    expect(get).toHaveBeenCalledWith('/api/v1/integrations/labels/mode');
    expect(printLabel).toHaveBeenLastCalledWith(expect.anything(), job, { simulator: true });
  });

  it('asks again after switching facility, so labels follow the facility now chosen', async () => {
    get.mockResolvedValueOnce({ simulator: false }).mockResolvedValueOnce({ simulator: true });

    await printLabelJob(job);
    session.user = { id: 'u1', facilityId: 'f2' };
    await printLabelJob(job);

    expect(get).toHaveBeenCalledTimes(2);
    expect(printLabel).toHaveBeenLastCalledWith(expect.anything(), job, { simulator: true });
  });

  it('prints on the printers when the mode cannot be read', async () => {
    get.mockRejectedValue(new Error('Network down'));

    await printLabelJob(job);

    expect(printLabel).toHaveBeenLastCalledWith(expect.anything(), job, { simulator: false });
  });
});
