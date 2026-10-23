import { useState } from 'react';

/**
 * The version of a record that an editor's values are based on, sent with its save (REL-04). It is the version the
 * editor was filled from, and it moves on only with this user's own saves (`setVersion`), never with a refetch: a
 * change someone else saved meanwhile is refused by the server rather than overwritten.
 *
 * `own` is the record as returned by the latest change this user saved elsewhere on the same screen (vitals added under
 * an anaesthesia record, a station closed on a check-up), which moves the record's version on too. The editor takes
 * that record's version only when `unchanged(own)` says the record still holds exactly the values the editor was
 * filled from or last saved, for the fields it edits (see `followIfUnchanged`).
 *
 * So the version an editor saves on always holds, for the fields it edits, the values its form is based on. A change
 * someone else made to those fields, before this user's own change or along with it, is never adopted: the save stays
 * on the older version and is refused instead of overwriting it. Changes to the rest of the record, which the save
 * does not write, are followed whoever made them.
 */
export function useBaseVersion<V extends number | null, R extends { version: number } = { version: number }>(
  filled: V,
  own: R | null = null,
  unchanged: (returned: R) => boolean = () => false,
) {
  const [version, setVersion] = useState<V | number>(filled);
  const [followed, setFollowed] = useState(own);
  if (own !== followed) {
    setFollowed(own);
    if (own && unchanged(own)) {
      setVersion(own.version);
    }
  }
  return [version, setVersion] as const;
}

/**
 * Whether `returned` holds exactly what `base` holds for `fields` (by default every field of `base`): the `unchanged`
 * test of `useBaseVersion`. `base` is the record the editor was filled from or last saved, never its unsaved edits.
 */
export function followIfUnchanged<T extends object>(
  base: T,
  returned: T,
  fields: readonly (keyof T)[] = Object.keys(base) as (keyof T)[],
): boolean {
  return fields.every((field) => JSON.stringify(base[field]) === JSON.stringify(returned[field]));
}
