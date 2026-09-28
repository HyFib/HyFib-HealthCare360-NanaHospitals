import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Radio,
  RadioGroup,
  Spinner,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatInr, isApiError } from '@hc360/core';
import { applyServerErrors, FormGrid, NumberField, SelectField, SwitchField, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { canteenApi, canteenKeys, GST_RATES, MENU_CATEGORIES, type MenuItemDto, type StockMovementKind } from './api';
import { calculateLine } from './billCalculator';

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code, e.g. TEA.')
    .max(24)
    .regex(/^[A-Za-z0-9-]+$/, 'Use letters, digits or dashes.'),
  name: z.string().trim().min(1, 'Enter the item name.').max(100),
  category: z.enum(MENU_CATEGORIES),
  price: z
    .number({ message: 'Enter the price.' })
    .gt(0, 'The price must be more than zero.')
    .max(10_000)
    .refine((v) => Math.round(v * 100) === v * 100, 'Use at most two decimals.'),
  gstRate: z.string(),
  priceIncludesGst: z.boolean(),
  isVeg: z.boolean(),
  isAvailable: z.boolean(),
  trackStock: z.boolean(),
  displayOrder: z.number({ message: 'Enter a number.' }).int().min(0).max(10_000),
  description: z.string().trim().max(300),
});

type MenuValues = z.infer<typeof schema>;

/** Add or edit a menu item. Prices are normally GST-inclusive, as shown on the canteen board (5 % restaurant GST). */
export function MenuItemDrawer({ item, onClose }: { item: MenuItemDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<MenuValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: item?.code ?? '',
      name: item?.name ?? '',
      category: item?.category ?? 'Snacks',
      price: item?.price ?? (null as unknown as number),
      gstRate: String(item?.gstRate ?? 5),
      priceIncludesGst: item?.priceIncludesGst ?? true,
      isVeg: item?.isVeg ?? true,
      isAvailable: item?.isAvailable ?? true,
      trackStock: item?.trackStock ?? false,
      displayOrder: item?.displayOrder ?? 0,
      description: item?.description ?? '',
    },
  });
  const [price, gstRate, inclusive] = useWatch({ control: form.control, name: ['price', 'gstRate', 'priceIncludesGst'] });
  const preview =
    typeof price === 'number' && price > 0
      ? calculateLine({ menuItemId: 'preview', itemName: 'preview', quantity: 1, unitPrice: price, gstRate: Number(gstRate), priceIncludesGst: inclusive })
      : null;

  const save = useMutation({
    mutationFn: async (v: MenuValues): Promise<void> => {
      const body = { ...v, gstRate: Number(v.gstRate), description: v.description || null, version: item?.version ?? null };
      if (item) {
        await canteenApi.updateMenuItem(item.id, body);
      } else {
        await canteenApi.createMenuItem(body);
      }
    },
    onSuccess: () => {
      notify.success(item ? 'Menu item saved' : 'Menu item added');
      void queryClient.invalidateQueries({ queryKey: canteenKeys.all });
      onClose();
    },
    onError: (error) => applyServerErrors(error, form.setError),
  });
  const serverError = save.error && !(isApiError(save.error) && save.error.isValidation) ? save.error : null;

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}>
            {item ? `Edit ${item.name}` : 'Add menu item'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12, paddingBottom: 12 }}>
            <FormGrid columns={2} minColumnWidth={180}>
              <TextField control={form.control} name="code" label="Code" required maxLength={24} autoFocus={!item} />
              <TextField control={form.control} name="name" label="Name" required maxLength={100} placeholder="Masala dosa" />
              <SelectField control={form.control} name="category" label="Category" options={MENU_CATEGORIES.map((c) => ({ value: c, label: c }))} />
              <NumberField control={form.control} name="price" label="Price (₹)" required min={0.01} max={10_000} step={0.5} />
              <SelectField control={form.control} name="gstRate" label="GST rate" options={GST_RATES.map((r) => ({ value: String(r), label: `${r}%` }))} />
              <NumberField control={form.control} name="displayOrder" label="Display order" min={0} max={10_000} />
            </FormGrid>
            <SwitchField control={form.control} name="priceIncludesGst" label="Price includes GST (as on the menu board)" />
            {preview && (
              <Caption1 style={{ color: tokens.colorNeutralForeground3 }}>
                One item: taxable {formatInr(preview.taxableAmount)} + CGST {formatInr(preview.cgst)} + SGST {formatInr(preview.sgst)} = {formatInr(preview.lineTotal)}
              </Caption1>
            )}
            <SwitchField control={form.control} name="isVeg" label="Vegetarian" />
            <SwitchField control={form.control} name="isAvailable" label="On today's menu" />
            <SwitchField control={form.control} name="trackStock" label="Track stock (packaged items such as water and biscuits)" />
            <TextAreaField control={form.control} name="description" label="Description" rows={2} maxLength={300} />
            {serverError && (
              <MessageBar intent="error">
                <MessageBarBody>{isApiError(serverError) ? serverError.userMessage : 'Could not save the item.'}</MessageBarBody>
              </MessageBar>
            )}
          </div>
        </DrawerBody>
        <DrawerFooter>
          <Button type="submit" appearance="primary" disabled={save.isPending} icon={save.isPending ? <Spinner size="tiny" /> : undefined}>
            {item ? 'Save' : 'Add item'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </DrawerFooter>
      </form>
    </OverlayDrawer>
  );
}

const KIND_HINT: Record<StockMovementKind, string> = {
  Receipt: 'Stock received from the supplier',
  Adjustment: 'Correction after a physical count (use a minus sign to reduce)',
  Wastage: 'Damaged, expired or spilled stock',
};

/** Receive, adjust or write off stock of a packaged item. */
export function StockDialog({ item, onClose }: { item: MenuItemDto; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<StockMovementKind>('Receipt');
  const [quantity, setQuantity] = useState('');
  const [reason, setReason] = useState('');
  const q = Number(quantity);
  const validQuantity = quantity !== '' && Number.isInteger(q) && q !== 0 && Math.abs(q) <= 10_000 && (kind === 'Adjustment' || q > 0);
  const after = validQuantity ? item.stockOnHand + (kind === 'Wastage' ? -q : q) : null;
  const valid = validQuantity && after !== null && after >= 0 && (kind === 'Receipt' || reason.trim().length > 0);

  const move = useMutation({
    mutationFn: () => canteenApi.moveStock(item.id, { kind, quantity: q, reason: reason.trim() || null }),
    onSuccess: (updated) => {
      notify.success(`${updated.name}: ${updated.stockOnHand} in stock`);
      void queryClient.invalidateQueries({ queryKey: canteenKeys.all });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !move.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !move.isPending) {
              move.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Stock of {item.name}
              <Caption1 block style={{ color: tokens.colorNeutralForeground3 }}>
                {item.stockOnHand} in stock now
              </Caption1>
            </DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Field label="What happened" hint={KIND_HINT[kind]}>
                <RadioGroup layout="horizontal" value={kind} onChange={(_, d) => setKind(d.value as StockMovementKind)}>
                  <Radio value="Receipt" label="Received" />
                  <Radio value="Adjustment" label="Count correction" />
                  <Radio value="Wastage" label="Wastage" />
                </RadioGroup>
              </Field>
              <Field label="Quantity" required validationMessage={after !== null ? `Stock after: ${after}` : undefined} validationState={after !== null && after < 0 ? 'error' : 'none'}>
                <Input type="number" inputMode="numeric" value={quantity} onChange={(_, d) => setQuantity(d.value)} autoFocus />
              </Field>
              <Field label="Reason" required={kind !== 'Receipt'}>
                <Textarea value={reason} onChange={(_, d) => setReason(d.value)} maxLength={300} rows={2} placeholder={kind === 'Receipt' ? 'e.g. Invoice number' : 'e.g. Bottles damaged in transit'} />
              </Field>
              {move.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{isApiError(move.error) ? move.error.userMessage : 'The stock could not be updated.'}</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={move.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={!valid || move.isPending} icon={move.isPending ? <Spinner size="tiny" /> : undefined}>
                Save stock
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
