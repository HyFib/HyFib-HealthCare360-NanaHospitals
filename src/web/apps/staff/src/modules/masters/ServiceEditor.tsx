import { Link, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  FormGrid,
  FullRow,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  type OptionItem,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm, type Control } from 'react-hook-form';
import { useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { SERVICE_CATEGORIES, SERVICE_CATEGORY_LABEL } from '@/modules/billing/labels';
import {
  mastersApi,
  mastersQueries,
  refreshAfterMasterChange,
  type LocalLanguageItem,
  type ServiceItemDto,
} from './api';
import {
  PERMANENT_CODE_HINT,
  serviceNameLanguages,
  serviceSchema,
  toServiceForm,
  toServiceRequest,
  type ServiceForm,
} from './forms';
import { gstRateLabel, SERVICE_GST_RATES } from './labels';
import { LoadingDrawer, MasterDrawer } from './MasterDrawer';
import { EditorForm, EditorSection } from './parts';
import { showSaveError } from './saveErrors';
import { useEditorGuard } from './useEditorGuard';

/** Add a service to the catalogue (`serviceId` null), or change one. */
export function ServiceEditorDrawer({ serviceId, onClose }: { serviceId: string | null; onClose: () => void }) {
  const existing = useQuery({ ...mastersQueries.service(serviceId ?? ''), enabled: serviceId !== null });
  // The form has a name field for each facility's language, so it waits for them (none if they can't be read).
  const languages = useQuery(mastersQueries.localLanguages());
  // Filled from the service as read now, not from an older copy kept from before (REL-04).
  if (serviceId !== null && (!existing.isFetchedAfterMount || !existing.data)) {
    return <LoadingDrawer error={existing.error} what="service" onClose={onClose} />;
  }
  if (!languages.isFetched) {
    return <LoadingDrawer error={null} what="service" onClose={onClose} />;
  }
  return <ServiceEditor service={existing.data ?? null} languages={languages.data ?? []} onClose={onClose} />;
}

function ServiceEditor({
  service,
  languages,
  onClose,
}: {
  service: ServiceItemDto | null;
  languages: readonly LocalLanguageItem[];
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const [named] = useState(() => serviceNameLanguages(service, languages));
  const form = useForm<ServiceForm>({
    resolver: zodResolver(serviceSchema),
    defaultValues: toServiceForm(
      service,
      named.map((l) => l.code),
    ),
    mode: 'onTouched',
  });
  // The version the form was filled from, not a newer one read since (REL-04).
  const [loadedVersion] = useState(service?.version ?? null);
  const save = useMutation({
    mutationFn: (values: ServiceForm) =>
      mastersApi.saveService(service?.id ?? null, toServiceRequest(values, service, loadedVersion)),
    onSuccess: () => {
      notify.success(service ? 'Service saved' : 'Service added', 'New bills and orders use it from now on.');
      refreshAfterMasterChange(queryClient);
    },
    onError: (error) =>
      showSaveError(error, { form, notify, what: 'service', onConflict: () => refreshAfterMasterChange(queryClient) }),
  });
  const guard = useEditorGuard({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  const departmentOptions: OptionItem[] = (departments.data ?? []).map((d) => ({ value: d.id, label: d.name }));
  if (service?.departmentId && !departmentOptions.some((o) => o.value === service.departmentId)) {
    departmentOptions.unshift({ value: service.departmentId, label: 'Current department (not offered here)' });
  }

  return (
    <MasterDrawer
      title={service ? service.name : 'New service'}
      guard={guard}
      busy={save.isPending}
      submitLabel={service ? 'Save' : 'Add service'}
      onSubmit={form.handleSubmit((values) => save.mutate(values, { onSuccess: guard.close }))}
    >
      <ServiceFields
        control={form.control}
        editing={service !== null}
        departmentOptions={departmentOptions}
        currentTaxRate={service?.taxRate ?? null}
        languages={named}
      />
    </MasterDrawer>
  );
}

/**
 * A billable service: its name and code on bills, its name in each facility's local language for the rate list, its
 * category and department, base price and GST.
 */
export function ServiceFields({
  control,
  editing,
  departmentOptions,
  currentTaxRate,
  languages,
}: {
  control: Control<ServiceForm>;
  editing: boolean;
  departmentOptions: OptionItem[];
  currentTaxRate: number | null;
  /** The languages to name it in ({@link serviceNameLanguages}), each with its field in the form. */
  languages: readonly LocalLanguageItem[];
}) {
  const canPrice = usePermission('billing.tariffs.manage');
  const navigate = useNavigate();
  const rates: number[] = [...SERVICE_GST_RATES];
  if (currentTaxRate !== null && !rates.includes(currentTaxRate)) {
    rates.push(currentTaxRate);
    rates.sort((a, b) => a - b);
  }

  return (
    <EditorForm>
      <EditorSection title="Service">
        <FormGrid columns={2}>
          <FullRow>
            <TextField
              control={control}
              name="name"
              label="Name"
              required
              autoFocus={!editing}
              maxLength={200}
              hint="Printed on bills, such as Complete blood count."
            />
          </FullRow>
          {languages.map((language) => (
            <FullRow key={language.code}>
              <TextField
                control={control}
                name={`localNames.${language.code}`}
                label={`Name in ${language.name}`}
                maxLength={200}
                hint={`Printed under the English name on the rate list of each facility whose local language is ${language.name}.`}
              />
            </FullRow>
          ))}
          {languages.length === 0 && (
            <FullRow>
              <MessageBar intent="info">
                <MessageBarBody>
                  A facility&apos;s rate list prints each service&apos;s name in the local language too. Enter the
                  facility&apos;s state in Setup › Facilities to name services in its language here.
                </MessageBarBody>
              </MessageBar>
            </FullRow>
          )}
          <TextField
            control={control}
            name="code"
            label="Code"
            required
            disabled={editing}
            maxLength={32}
            hint={editing ? PERMANENT_CODE_HINT : "Such as LAB-CBC. It can't be changed later."}
          />
          <TextField
            control={control}
            name="shortName"
            label="Short name"
            maxLength={64}
            hint="Printed where space is short, such as CBC."
          />
          <SelectField
            control={control}
            name="category"
            label="Category"
            required
            options={SERVICE_CATEGORIES.map((c) => ({ value: c, label: SERVICE_CATEGORY_LABEL[c] }))}
          />
          <SelectField
            control={control}
            name="departmentId"
            label="Department"
            placeholder="None"
            options={departmentOptions}
            hint="Whose income the service is, in reports and Tally."
          />
        </FormGrid>
      </EditorSection>

      <EditorSection title="Price and tax">
        <FormGrid columns={2}>
          <NumberField
            control={control}
            name="basePrice"
            label="Base price (₹)"
            required
            min={0}
            step={10}
            hint="Before GST, for patients without a tariff plan."
          />
          <SelectField
            control={control}
            name="taxRate"
            label="GST"
            required
            options={rates.map((rate) => ({ value: String(rate), label: gstRateLabel(rate) }))}
            hint="Most hospital services are exempt."
          />
          <TextField
            control={control}
            name="sacCode"
            label="SAC code"
            maxLength={8}
            hint="The GST service code; 999312 for hospital services."
          />
        </FormGrid>
        <SwitchField control={control} name="isDiscountAllowed" label="Discounts allowed" />
        <SwitchField
          control={control}
          name="isPriceEditable"
          label="The billing counter can change the price"
          hint="For services priced case by case, such as a procedure."
        />
        <MessageBar intent="info">
          <MessageBarBody>
            Corporate, insurance and scheme patients pay their tariff plan&apos;s price.{' '}
            {canPrice ? (
              <Link as="button" type="button" inline onClick={() => void navigate({ to: '/billing/tariffs' })}>
                Set plan prices in Tariffs.
              </Link>
            ) : (
              'Plan prices are set in Billing.'
            )}
          </MessageBarBody>
        </MessageBar>
      </EditorSection>

      <EditorSection title="Description and use">
        <TextAreaField control={control} name="description" label="Description" rows={3} maxLength={1000} />
        <SwitchField
          control={control}
          name="isActive"
          label="In use"
          hint="A service not in use can't be added to new bills or orders. Bills already made keep it."
        />
      </EditorSection>
    </EditorForm>
  );
}
