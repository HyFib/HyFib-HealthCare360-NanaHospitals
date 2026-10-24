import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  SwitchField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { ICD10PCS_RELEASE, mrdApi, mrdKeys, type ProcedureCodeDto } from './api';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/** The server's rules (`SaveProcedureCodeRequestValidator`), checked before saving. */
const procedureSchema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter the code.')
    .max(16, 'Use at most 16 characters.')
    .regex(/^[A-Za-z0-9][A-Za-z0-9.]*$/, 'Use letters, digits and dots.'),
  title: z.string().trim().min(1, 'Enter the title.').max(300, 'Use at most 300 characters.'),
  system: z.string().trim().max(32, 'Use at most 32 characters.'),
  category: z.string().trim().max(64, 'Use at most 64 characters.'),
  keywords: z.string().trim().max(300, 'Use at most 300 characters.'),
  isActive: z.boolean(),
});

type ProcedureValues = z.infer<typeof procedureSchema>;

/** Adding a code to the procedure catalogue, or changing one (`code` null: closed). */
export function ProcedureCodeDrawer({ code, onClose }: { code: ProcedureCodeDto | 'new' | null; onClose: () => void }) {
  const closing = useCloseRequests({ onClose });
  return (
    <OverlayDrawer open={code !== null} position="end" size="medium" onOpenChange={closing.onOpenChange}>
      {code !== null && (
        <CloseRequestScope value={closing}>
          <ProcedureForm key={code === 'new' ? 'new' : code.id} code={code === 'new' ? null : code} onClose={onClose} />
        </CloseRequestScope>
      )}
    </OverlayDrawer>
  );
}

function ProcedureForm({ code, onClose }: { code: ProcedureCodeDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<ProcedureValues>({
    resolver: zodResolver(procedureSchema),
    defaultValues: {
      code: code?.code ?? '',
      title: code?.title ?? '',
      system: code?.system ?? ICD10PCS_RELEASE,
      category: code?.category ?? '',
      keywords: code?.keywords ?? '',
      isActive: code?.isActive ?? true,
    },
  });
  const save = useMutation({
    mutationFn: (v: ProcedureValues) =>
      mrdApi.saveProcedure(code?.id ?? null, {
        code: v.code.trim(),
        title: v.title.trim(),
        system: v.system.trim() || null,
        category: v.category.trim() || null,
        keywords: v.keywords.trim() || null,
        isActive: v.isActive,
        version: code?.version,
      }),
    onSuccess: (saved) => {
      notify.success(`${saved.code} saved`);
      void queryClient.invalidateQueries({ queryKey: mrdKeys.all });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'procedure.exists') {
        form.setError('code', { message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the code');
      }
    },
  });
  const fromFile = code?.source === 'Import' || code?.source === 'Seed';
  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
          }
        >
          {code ? code.code : 'Add procedure code'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          {fromFile && (
            <MessageBar intent="info">
              <MessageBarBody>
                This code comes from {code?.source === 'Seed' ? 'the starter list' : 'the CMS ICD-10-PCS file'}. Search
                words and the Active switch can be changed freely. If you change the code, title, system or category, it
                becomes this hospital&rsquo;s own code and later imports leave it as it is.
              </MessageBarBody>
            </MessageBar>
          )}
          <TextField
            control={form.control}
            name="code"
            label="Code"
            required
            autoFocus
            maxLength={16}
            placeholder="e.g. 0DTJ4ZZ"
          />
          <TextField control={form.control} name="title" label="Title" required maxLength={300} />
          <TextField
            control={form.control}
            name="system"
            label="Code system"
            maxLength={32}
            hint="ICD-10-PCS unless the hospital codes this procedure in another system"
          />
          <TextField
            control={form.control}
            name="category"
            label="Category"
            maxLength={64}
            hint="Speciality grouping for browsing, e.g. General surgery, Obstetrics"
          />
          <TextField
            control={form.control}
            name="keywords"
            label="Search words"
            maxLength={300}
            hint="Shorthand and synonyms coders type, e.g. lap chole, LSCS"
          />
          <SwitchField control={form.control} name="isActive" label="Active (offered in searches)" />
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button
          type="submit"
          appearance="primary"
          disabled={save.isPending}
          icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
        >
          Save
        </Button>
        <Button onClick={guard.requestClose}>Cancel</Button>
      </DrawerFooter>
      {guard.dialogs}
    </form>
  );
}
