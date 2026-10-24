import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Spinner,
} from '@fluentui/react-components';
import { ArrowClockwiseRegular, DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  applyServerErrors,
  CloseRequestScope,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import {
  CODE_HINT,
  isCodeInUse,
  isStale,
  orderSetSchema,
  toOrderSetRequest,
  useSpecialtyOptions,
  type OrderSetValues,
} from './catalogue';
import { PickedServices } from './catalogueParts';
import { ordersApi, type OrderSetDto } from './ordersApi';

const useStyles = makeStyles({ form: { display: 'flex', flexDirection: 'column', gap: '12px' } });

/**
 * Create or edit an order set (EMR-05): a named group of tests and services a doctor orders in one go. `orderSet` is
 * the set as the list has it now; after someone else's change is reloaded, the form opens again on the new version.
 */
export function OrderSetDrawer({
  open,
  orderSet,
  onClose,
  onReload,
}: {
  open: boolean;
  orderSet: OrderSetDto | null;
  onClose: () => void;
  onReload: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <OverlayDrawer open={open} position="end" size="medium" onOpenChange={closing.onOpenChange}>
      {open && (
        <CloseRequestScope value={closing}>
          <OrderSetForm
            key={orderSet ? `${orderSet.id}-${orderSet.version}` : 'new'}
            orderSet={orderSet}
            onClose={onClose}
            onReload={onReload}
          />
        </CloseRequestScope>
      )}
    </OverlayDrawer>
  );
}

function OrderSetForm({
  orderSet,
  onClose,
  onReload,
}: {
  orderSet: OrderSetDto | null;
  onClose: () => void;
  onReload: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [stale, setStale] = useState(false);
  const form = useForm<OrderSetValues>({
    resolver: zodResolver(orderSetSchema),
    defaultValues: {
      code: orderSet?.code ?? '',
      name: orderSet?.name ?? '',
      specialtyCode: orderSet?.specialtyCode ?? null,
      description: orderSet?.description ?? '',
      services: orderSet?.services ?? [],
      isActive: orderSet?.isActive ?? true,
    },
  });
  const specialties = useSpecialtyOptions(orderSet?.specialtyCode);

  const save = useMutation({
    mutationFn: (values: OrderSetValues) =>
      ordersApi.saveOrderSet(orderSet?.id ?? null, toOrderSetRequest(values, orderSet?.version ?? null)),
    onSuccess: (saved) => {
      notify.success(orderSet ? 'Order set saved' : 'Order set added', saved.name);
      void queryClient.invalidateQueries({ queryKey: ['emr', 'order-sets'] });
      onClose();
    },
    onError: (error) => {
      if (isCodeInUse(error)) {
        form.setError('code', { message: error.userMessage });
      } else if (isStale(error)) {
        setStale(true);
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the order set');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
          }
        >
          {orderSet ? orderSet.name : 'New order set'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          {stale && (
            <MessageBar intent="warning" layout="multiline" politeness="assertive">
              <MessageBarBody>
                <MessageBarTitle>Someone else changed this order set</MessageBarTitle>
                Reload to see their change, then make yours again. Nothing was saved.
              </MessageBarBody>
              <MessageBarActions>
                <Button icon={<ArrowClockwiseRegular />} onClick={onReload}>
                  Reload
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}
          <TextField
            control={form.control}
            name="code"
            label="Code"
            required
            autoFocus={!orderSet}
            maxLength={32}
            hint={CODE_HINT}
          />
          <TextField control={form.control} name="name" label="Name" required maxLength={200} />
          <SelectField
            control={form.control}
            name="specialtyCode"
            label="Speciality"
            placeholder="Every speciality"
            options={specialties.options}
            hint="Doctors of this speciality see it first; with none, every doctor sees it."
          />
          <TextAreaField
            control={form.control}
            name="description"
            label="Description"
            rows={2}
            maxLength={500}
            placeholder="e.g. Baseline work-up for newly diagnosed diabetes"
          />
          <Controller
            control={form.control}
            name="services"
            render={({ field, fieldState }) => (
              <PickedServices
                label="Services"
                services={field.value}
                onChange={field.onChange}
                max={40}
                error={fieldState.error?.message}
              />
            )}
          />
          <SwitchField control={form.control} name="isActive" label="Active (offered when ordering)" />
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button
          type="submit"
          appearance="primary"
          disabled={save.isPending || stale}
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
