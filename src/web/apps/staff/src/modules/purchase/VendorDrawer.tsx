import {
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
  Subtitle2,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, normalizePhone, todayIso } from '@hc360/core';
import {
  applyServerErrors,
  DateField,
  FormGrid,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { gstStateName } from '@/modules/inventory/gst';
import { INDIAN_STATES } from '@/modules/patients/validation';
import { purchaseApi, purchaseKeys, purchaseQueries, type VendorDetail } from './api';

const GSTIN = /^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$/;
const PAN = /^[A-Z]{5}[0-9]{4}[A-Z]$/;

const schema = z
  .object({
    code: z
      .string()
      .trim()
      .min(1, 'Enter a short code.')
      .max(24)
      .regex(/^[A-Za-z0-9\-_/]+$/, "Use letters, digits, '-', '_' or '/'."),
    name: z.string().trim().min(1, 'Enter the vendor name.').max(200),
    gstin: z
      .string()
      .trim()
      .toUpperCase()
      .refine((v) => !v || GSTIN.test(v), 'GSTIN has 15 characters, e.g. 27AAACN1234F1Z5.'),
    pan: z
      .string()
      .trim()
      .toUpperCase()
      .refine((v) => !v || PAN.test(v), 'PAN has 5 letters, 4 digits and a letter.'),
    drugLicenseNumber: z.string().trim().max(64),
    drugLicenseValidUntil: z.string().nullable(),
    contactPerson: z.string().trim().max(120),
    phone: z
      .string()
      .trim()
      .refine((v) => !v || normalizePhone(v) !== null, 'Enter a valid phone number.'),
    email: z
      .string()
      .trim()
      .max(256)
      .refine((v) => !v || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v), 'Enter a valid email address.'),
    line1: z.string().trim().max(200),
    line2: z.string().trim().max(200),
    city: z.string().trim().max(100),
    district: z.string().trim().max(100),
    state: z.string().nullable(),
    pinCode: z
      .string()
      .trim()
      .refine((v) => !v || /^[1-9]\d{5}$/.test(v), 'PIN code must be 6 digits.'),
    paymentTermsDays: z.number({ error: 'Enter the credit days (0 for cash).' }).int().min(0).max(365),
    rating: z.number({ error: 'Enter 0 to 5.' }).min(0).max(5),
    notes: z.string().trim().max(1000),
    isActive: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (v.gstin && v.pan && v.gstin.length >= 12 && v.gstin.slice(2, 12) !== v.pan) {
      ctx.addIssue({
        code: 'custom',
        path: ['pan'],
        message: 'The PAN does not match the one inside the GSTIN (characters 3 to 12).',
      });
    }
  });

type VendorValues = z.infer<typeof schema>;

function toValues(v: VendorDetail | null): VendorValues {
  return {
    code: v?.code ?? '',
    name: v?.name ?? '',
    gstin: v?.gstin ?? '',
    pan: v?.pan ?? '',
    drugLicenseNumber: v?.drugLicenseNumber ?? '',
    drugLicenseValidUntil: v?.drugLicenseValidUntil ?? null,
    contactPerson: v?.contactPerson ?? '',
    phone: v?.phone ?? '',
    email: v?.email ?? '',
    line1: v?.address.line1 ?? '',
    line2: v?.address.line2 ?? '',
    city: v?.address.city ?? '',
    district: v?.address.district ?? '',
    state: v?.address.state ?? null,
    pinCode: v?.address.pinCode ?? '',
    paymentTermsDays: v?.paymentTermsDays ?? 30,
    rating: v?.rating ?? 0,
    notes: v?.notes ?? '',
    isActive: v?.isActive ?? true,
  };
}

/** Vendor master editor: GSTIN (decides CGST + SGST or IGST), PAN, drug licence (Form 20B/21B) and credit terms. */
export function VendorDrawer({
  vendorId,
  canManage,
  onClose,
}: {
  vendorId: string | null;
  canManage: boolean;
  onClose: () => void;
}) {
  const existing = useQuery({ ...purchaseQueries.vendor(vendorId ?? ''), enabled: vendorId !== null, staleTime: 0 });
  // Filled from the vendor as read now, not from an older copy kept from before (REL-04).
  if (vendorId && (!existing.isFetchedAfterMount || !existing.data)) {
    return (
      <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
        <DrawerBody>
          {existing.error ? (
            <MessageBar intent="error">
              <MessageBarBody>
                {isApiError(existing.error) ? existing.error.userMessage : 'Could not load the vendor.'}
              </MessageBarBody>
            </MessageBar>
          ) : (
            <Spinner label="Loading…" />
          )}
        </DrawerBody>
      </OverlayDrawer>
    );
  }
  return <VendorForm vendor={existing.data ?? null} canManage={canManage} onClose={onClose} />;
}

function VendorForm({
  vendor,
  canManage,
  onClose,
}: {
  vendor: VendorDetail | null;
  canManage: boolean;
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<VendorValues>({
    resolver: zodResolver(schema),
    defaultValues: toValues(vendor),
    mode: 'onTouched',
  });
  // The version the form was filled from, not a newer one read since (REL-04).
  const [loadedVersion] = useState(vendor?.version ?? null);
  const [gstin, validUntil] = useWatch({ control: form.control, name: ['gstin', 'drugLicenseValidUntil'] });
  const readOnly = !canManage;
  const state = GSTIN.test((gstin ?? '').toUpperCase()) ? gstStateName(gstin) : null;
  const licenceExpired = Boolean(validUntil && validUntil < todayIso());

  const save = useMutation({
    mutationFn: (v: VendorValues) =>
      purchaseApi.saveVendor(vendor?.id ?? null, {
        code: v.code,
        name: v.name,
        gstin: v.gstin || null,
        pan: v.pan || null,
        drugLicenseNumber: v.drugLicenseNumber || null,
        drugLicenseValidUntil: v.drugLicenseValidUntil,
        contactPerson: v.contactPerson || null,
        phone: v.phone || null,
        email: v.email || null,
        address: {
          line1: v.line1 || null,
          line2: v.line2 || null,
          city: v.city || null,
          district: v.district || null,
          state: v.state,
          pinCode: v.pinCode || null,
          country: 'India',
        },
        paymentTermsDays: v.paymentTermsDays,
        rating: v.rating,
        notes: v.notes || null,
        isActive: v.isActive,
        version: loadedVersion,
      }),
    onSuccess: () => {
      notify.success(vendor ? 'Vendor saved' : 'Vendor added');
      void queryClient.invalidateQueries({ queryKey: purchaseKeys.all });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'vendor.duplicate_code') {
        form.setError('code', { type: 'server', message: error.userMessage });
      } else if (isApiError(error) && error.code === 'vendor.duplicate_gstin') {
        form.setError('gstin', { type: 'server', message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the vendor');
      }
    },
  });

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={guard.onOpenChange}>
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {vendor ? vendor.name : 'Add vendor'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            <FormGrid columns={2}>
              <TextField
                control={form.control}
                name="code"
                label="Vendor code"
                required
                autoFocus={!vendor}
                disabled={readOnly}
                maxLength={24}
              />
              <TextField control={form.control} name="name" label="Name" required disabled={readOnly} maxLength={200} />
            </FormGrid>
            <Subtitle2 as="h3">Tax</Subtitle2>
            <FormGrid columns={2}>
              <TextField
                control={form.control}
                name="gstin"
                label="GSTIN"
                disabled={readOnly}
                maxLength={15}
                hint={
                  state
                    ? `Registered in ${state}. Decides CGST + SGST (same state) or IGST.`
                    : 'Leave blank for unregistered vendors'
                }
              />
              <TextField
                control={form.control}
                name="pan"
                label="PAN"
                disabled={readOnly}
                maxLength={10}
                hint="Must match characters 3 to 12 of the GSTIN"
              />
            </FormGrid>
            <Subtitle2 as="h3">Drug licence</Subtitle2>
            <FormGrid columns={2}>
              <TextField
                control={form.control}
                name="drugLicenseNumber"
                label="Licence number (Form 20B / 21B)"
                disabled={readOnly}
                maxLength={64}
              />
              <DateField control={form.control} name="drugLicenseValidUntil" label="Valid until" disabled={readOnly} />
            </FormGrid>
            {licenceExpired && (
              <MessageBar intent="warning">
                <MessageBarBody>
                  The licence has expired: medicines cannot be received from this vendor until it is renewed.
                </MessageBarBody>
              </MessageBar>
            )}
            <Subtitle2 as="h3">Contact</Subtitle2>
            <FormGrid columns={3}>
              <TextField control={form.control} name="contactPerson" label="Contact person" disabled={readOnly} />
              <TextField control={form.control} name="phone" label="Phone" type="tel" disabled={readOnly} />
              <TextField control={form.control} name="email" label="Email" type="email" disabled={readOnly} />
              <TextField control={form.control} name="line1" label="Address" disabled={readOnly} />
              <TextField control={form.control} name="line2" label="Area" disabled={readOnly} />
              <TextField control={form.control} name="city" label="City" disabled={readOnly} />
              <TextField control={form.control} name="district" label="District" disabled={readOnly} />
              <SelectField
                control={form.control}
                name="state"
                label="State"
                placeholder="—"
                disabled={readOnly}
                options={INDIAN_STATES.map((s) => ({ value: s, label: s }))}
              />
              <TextField control={form.control} name="pinCode" label="PIN code" maxLength={6} disabled={readOnly} />
            </FormGrid>
            <Subtitle2 as="h3">Terms</Subtitle2>
            <FormGrid columns={2}>
              <NumberField
                control={form.control}
                name="paymentTermsDays"
                label="Credit days"
                min={0}
                max={365}
                disabled={readOnly}
                hint="Default for new purchase orders"
              />
              <NumberField
                control={form.control}
                name="rating"
                label="Rating (0 to 5)"
                min={0}
                max={5}
                step={0.5}
                disabled={readOnly}
                hint="Quality, timeliness and pricing"
              />
            </FormGrid>
            <TextAreaField
              control={form.control}
              name="notes"
              label="Notes"
              rows={2}
              maxLength={1000}
              disabled={readOnly}
            />
            <SwitchField
              control={form.control}
              name="isActive"
              label="Active (can be ordered from)"
              disabled={readOnly}
            />
            {vendor?.pan && <Caption1>The PAN is kept out of logs and audit trails.</Caption1>}
          </div>
        </DrawerBody>
        <DrawerFooter>
          {canManage && (
            <Button
              type="submit"
              appearance="primary"
              disabled={save.isPending}
              icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
            >
              {vendor ? 'Save' : 'Add vendor'}
            </Button>
          )}
          <Button onClick={guard.requestClose}>{canManage ? 'Cancel' : 'Close'}</Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}
