import { isApiError, useRealtimeEvent, useSession } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { privacyKeys } from './api';

export interface CommandOptions {
  /** Toast title on success; nothing is shown without it. */
  success?: string;
  successBody?: string;
  /** Toast title when it fails for a reason that is not the request's state. */
  failure: string;
  /** Puts 400 validation messages on form fields; return true when it did. */
  onValidation?: (error: unknown) => boolean;
}

export type CommandResult<T> = { ok: true; value: T } | { ok: false };

/** Our own changes echo back as realtime events; ignore those that arrive this soon after one. */
const ECHO_MS = 4000;

/**
 * Runs the grievance officer's actions on one request. When the request moved on meanwhile (409), or the action is not
 * allowed in its state (422), the officer is told why and the latest details are loaded. Changes made elsewhere arrive
 * over realtime and refresh the page.
 */
export function useRequestCommand(requestId: string) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const [busy, setBusy] = useState(false);
  const lastChange = useRef(0);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: privacyKeys.requests });
    void queryClient.invalidateQueries({ queryKey: privacyKeys.overview });
  };

  useRealtimeEvent<{ id?: string }>(
    facilityId ? `facility:${facilityId}` : null,
    'privacy.request.updated',
    (payload) => {
      if (payload?.id === requestId && Date.now() - lastChange.current > ECHO_MS) {
        void queryClient.invalidateQueries({ queryKey: privacyKeys.request(requestId) });
      }
    },
  );

  const run = async <T>(action: () => Promise<T>, options: CommandOptions): Promise<CommandResult<T>> => {
    setBusy(true);
    lastChange.current = Date.now();
    try {
      const value = await action();
      lastChange.current = Date.now();
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
          error.status === 409 ? 'Someone else changed this request' : options.failure,
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

export type RequestCommand = ReturnType<typeof useRequestCommand>;
