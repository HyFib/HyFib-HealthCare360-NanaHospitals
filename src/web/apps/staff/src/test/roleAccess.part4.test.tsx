import type * as Core from '@hc360/core';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { findRefusals } from './roleAccess';

// Part 4 of 4 of the role-access check (roleAccess.ts): every role can use the screens its menu shows.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

describe('every role can use the screens its menu shows, part 4 of 4', () => {
  it(
    'asks the server only for what the role may read, and offers only what it may do',
    { timeout: 1_200_000 },
    async () => {
      const { refused } = await findRefusals(3, 4);
      expect(refused, `Screens that ask for what the role may not read or do:\n${refused.join('\n')}`).toEqual([]);
    },
  );
});
