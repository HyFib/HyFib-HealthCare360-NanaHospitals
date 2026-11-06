import { describe, expect, it } from 'vitest';
import serverAccess from '@/test/serverAccess.json';
import { APPROVAL_PERMISSIONS } from './api';
import manifest from './manifest';

// Whoever may approve a kind of change must find the Approvals menu, or the request waits unseen. The server writes
// each kind with its approving permission into serverAccess.json (StaffAccessFixtureTests); a new kind whose approver
// the menu leaves out fails here.
describe('the Approvals menu', () => {
  const approvals = Object.entries(serverAccess.approvals);

  it('knows the server has approvals to show', () => {
    expect(approvals.length).toBeGreaterThan(0);
  });

  it.each(approvals)('shows to whoever approves %s (%s)', (_kind, permission) => {
    expect(APPROVAL_PERMISSIONS).toContain(permission);
    expect(manifest.links[0]?.permission).toContain(permission);
  });
});
