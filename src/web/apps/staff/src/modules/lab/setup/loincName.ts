import { useEffect } from 'react';
import type { FieldValues, Path, PathValue, UseFormReturn } from 'react-hook-form';

/**
 * A LOINC name is the name LOINC gives its code, and ABDM health records carry the code only with it. When the code is
 * changed by hand to another code the name is cleared, for the new code's to be entered; changed back to the code the
 * test was loaded with, it gets that code's name again. Loading a test into the form keeps both. The server refuses a
 * changed code sent with the old code's name.
 */
export function useLoincNameFollowsCode<T extends FieldValues & { loinc: string; loincName: string }>(
  form: UseFormReturn<T>,
): void {
  useEffect(() => {
    const name = 'loincName' as Path<T>;
    const subscription = form.watch((values, { name: changed, type }) => {
      if (changed !== 'loinc' || type !== 'change') {
        return;
      }
      const loaded = form.formState.defaultValues;
      const code = (values.loinc ?? '').trim();
      if (code === (loaded?.loinc ?? '').trim()) {
        if (!values.loincName) {
          form.setValue(name, (loaded?.loincName ?? '') as PathValue<T, Path<T>>, { shouldDirty: true });
        }
      } else if (values.loincName) {
        form.setValue(name, '' as PathValue<T, Path<T>>, { shouldDirty: true });
      }
    });
    return () => subscription.unsubscribe();
  }, [form]);
}
