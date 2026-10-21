import { useEffect } from 'react';
import type { FieldValues, Path, PathValue, UseFormReturn } from 'react-hook-form';

/**
 * A LOINC name is the name LOINC gives its code, and ABDM health records carry the code only with it. When the code is
 * changed by hand the name is cleared, for the new code's to be entered; loading a test into the form keeps both. The
 * server refuses a changed code sent with the old code's name.
 */
export function useLoincNameFollowsCode<T extends FieldValues & { loinc: string; loincName: string }>(
  form: UseFormReturn<T>,
): void {
  useEffect(() => {
    const subscription = form.watch((_, { name, type }) => {
      if (name === 'loinc' && type === 'change') {
        form.setValue('loincName' as Path<T>, '' as PathValue<T, Path<T>>, { shouldDirty: true });
      }
    });
    return () => subscription.unsubscribe();
  }, [form]);
}
