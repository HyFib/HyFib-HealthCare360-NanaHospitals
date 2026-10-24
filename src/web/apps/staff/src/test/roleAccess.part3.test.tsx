import type * as Core from '@hc360/core';
import type * as EChartModule from '@/lib/EChart';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { findRefusals } from './roleAccess';

// Part 3 of 4 of the role-access check (roleAccess.ts): every role can use the screens its menu shows.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
// jsdom has no canvas to draw a chart on, and a screen whose chart fails shows its error page instead of its buttons.
vi.mock('@/lib/EChart', async (importOriginal) => ({
  ...(await importOriginal<typeof EChartModule>()),
  EChart: () => null,
}));

describe('every role can use the screens its menu shows, part 3 of 4', () => {
  it(
    'asks the server only for what the role may read, and offers only what it may do',
    { timeout: 1_200_000 },
    async () => {
      const { refused, gaps } = await findRefusals(2, 4);
      expect(refused, `Screens that ask for what the role may not read or do:\n${refused.join('\n')}`).toEqual([]);
      expect(gaps, `Screens the check cannot see whole (roleAccess/ has their data):\n${gaps.join('\n')}`).toEqual([]);
    },
  );
});
