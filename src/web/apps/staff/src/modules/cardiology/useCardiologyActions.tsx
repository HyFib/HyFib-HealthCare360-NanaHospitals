import { Caption1 } from '@fluentui/react-components';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { ReasonDialog } from '@/modules/lab/common/ReasonDialog';
import { cardiologyApi, cardiologyKeys } from './api';
import {
  AttachFilesDialog,
  CompleteCardiologyDialog,
  ScheduleCardiologyDialog,
  StartCardiologyDialog,
  type CardiologyStudyRef,
} from './StudyDialogs';
import type { CardiologyAction } from './workflow';

/**
 * Technician actions on a cardiology study (schedule, start, attach the tracing, complete, cancel) with their dialogs,
 * shared by the study list and the study page.
 */
export function useCardiologyActions(): {
  run: (action: CardiologyAction, study: CardiologyStudyRef) => void;
  dialogs: ReactNode;
} {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState<{ action: CardiologyAction; study: CardiologyStudyRef } | null>(null);

  const done = (message: string, body?: string) => {
    setOpen(null);
    notify.success(message, body);
    void queryClient.invalidateQueries({ queryKey: cardiologyKeys.all });
  };

  const cancel = useMutation({
    mutationFn: ({ study, reason }: { study: CardiologyStudyRef; reason: string }) =>
      cardiologyApi.cancel(study.id, reason),
    onSuccess: () => done('Study cancelled'),
    onError: (error) => notify.error(error, 'The study could not be cancelled'),
  });

  const close = () => setOpen(null);
  const study = open?.study;

  const dialogs = study ? (
    <>
      {open.action === 'schedule' && (
        <ScheduleCardiologyDialog study={study} onClose={close} onDone={() => done('Study scheduled')} />
      )}
      {open.action === 'start' && (
        <StartCardiologyDialog study={study} onClose={close} onDone={() => done('Recording started')} />
      )}
      {open.action === 'attach' && (
        <AttachFilesDialog study={study} onClose={close} onDone={() => done('Tracing attached')} />
      )}
      {open.action === 'complete' && (
        <CompleteCardiologyDialog
          study={study}
          onClose={close}
          onDone={() => done('Study completed', 'It is on the cardiologist’s reporting worklist.')}
        />
      )}
      {open.action === 'cancel' && (
        <ReasonDialog
          key={study.id}
          open
          title={`Cancel ${study.studyName}?`}
          description={<Caption1>The charge is reversed on the bill. A reported study cannot be cancelled.</Caption1>}
          suggestions={[
            'Patient declined',
            'Doctor cancelled the order',
            'Patient not fit for the test',
            'Duplicate order',
          ]}
          confirmLabel="Cancel study"
          destructive
          busy={cancel.isPending}
          onCancel={close}
          onConfirm={(reason) => cancel.mutate({ study, reason })}
        />
      )}
    </>
  ) : null;

  return { run: (action, target) => setOpen({ action, study: target }), dialogs };
}
