import type * as Core from '@hc360/core';
import type * as EChartModule from '@/lib/EChart';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { access, findRefusals, ruleFor, reads, RECORD_ID, screens, writes } from './roleAccess';

// Part 1 of 4 of the role-access check (roleAccess.ts): every role can use the screens its menu shows.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
// jsdom has no canvas to draw a chart on, and a screen whose chart fails shows its error page instead of its buttons.
vi.mock('@/lib/EChart', async (importOriginal) => ({
  ...(await importOriginal<typeof EChartModule>()),
  EChart: () => null,
}));

describe('every role can use the screens its menu shows, part 1 of 4', () => {
  it('finds the roles, the read and write rules, and the record screens', () => {
    expect(Object.keys(access.roles)).toContain('FrontOffice');
    expect(ruleFor(reads, '/api/v1/patients')).toBeDefined();
    expect(ruleFor(writes.get('POST') ?? [], '/api/v1/patients')).toBeDefined();
    expect(screens.map((s) => s.to)).toContain(`/mrd/records/${RECORD_ID}`);
  });

  it(
    'asks the server only for what the role may read, and offers only what it may do',
    { timeout: 1_200_000 },
    async () => {
      const { refused, gaps } = await findRefusals(0, 4);
      expect(refused, `Screens that ask for what the role may not read or do:\n${refused.join('\n')}`).toEqual([]);
      expect(gaps, `Screens the check cannot see whole (roleAccess/ has their data):\n${gaps.join('\n')}`).toEqual([]);
    },
  );
});
