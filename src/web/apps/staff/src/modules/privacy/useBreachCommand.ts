import { isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { privacyKeys } from './api';
import type { CommandOptions, CommandResult } from './useRequestCommand';

/**
 * Runs the steps on one breach. When the breach moved on meanwhile (409) or a step is not allowed in its state (422),
 * the user is told why and the latest details are loaded.
 */
export function useBreachCommand() {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: privacyKeys.breaches });
    void queryClient.invalidateQueries({ queryKey: privacyKeys.overview });
  };

  const run = async <T>(action: () => Promise<T>, options: CommandOptions): Promise<CommandResult<T>> => {
    setBusy(true);
    try {
      const value = await action();
      if (options.success) {
        notify.success(options.success, options.successBody);
      }
      refresh();
      return { ok: true, value };
    } catch (error) {
      if (options.onValidation?.(error)) {
        return { ok: false };
      }
      if (isApiError(error) && (error.status === 409 || error.status === 422 || error.status === 404)) {
        notify.warning(
          error.status === 409 ? 'Someone else changed this breach' : options.failure,
          `${error.userMessage} The latest details have been loaded.`,
        );
        refresh();
      } else {
        notify.error(error, options.failure);
      }
      return { ok: false };
    } finally {
      setBusy(false);
    }
  };

  return { busy, run, refresh };
}

export type BreachCommand = ReturnType<typeof useBreachCommand>;
