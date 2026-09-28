import { Toast, ToastBody, ToastTitle, useToastController } from '@fluentui/react-components';
import { TOASTER_ID } from '@hc360/ui';
import { useMemo, type ReactNode } from 'react';
import type { MessageKey, Translate } from '@/i18n';
import { describeError } from './errors';

type Intent = 'success' | 'info' | 'warning' | 'error';

export interface PortalToast {
  success(title: string, body?: ReactNode): void;
  info(title: string, body?: ReactNode): void;
  warning(title: string, body?: ReactNode): void;
  /** A failure in the patient's language (the server's own titles are English). */
  failure(error: unknown, t: Translate, title?: MessageKey): void;
}

/** Short confirmations and failures, announced politely to screen readers by the Fluent toaster. */
export function useToast(): PortalToast {
  const { dispatchToast } = useToastController(TOASTER_ID);
  return useMemo(() => {
    const show = (intent: Intent, title: string, body?: ReactNode) =>
      dispatchToast(
        <Toast>
          <ToastTitle>{title}</ToastTitle>
          {body && <ToastBody>{body}</ToastBody>}
        </Toast>,
        { intent, timeout: intent === 'error' ? 8000 : 5000 },
      );
    return {
      success: (title, body) => show('success', title, body),
      info: (title, body) => show('info', title, body),
      warning: (title, body) => show('warning', title, body),
      failure: (error, t, title = 'errors.saveTitle') => {
        const friendly = describeError(error, t, title);
        show('error', friendly.title, friendly.message);
      },
    };
  }, [dispatchToast]);
}
