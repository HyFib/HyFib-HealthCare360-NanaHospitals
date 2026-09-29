import { useQuery } from '@tanstack/react-query';
import { patientQueries } from './api';

/**
 * A patient's photo as an image source, or null while it loads, when the patient has none, and when it cannot be read
 * (the banner then shows initials). Cached per photo, so the many screens showing one patient fetch it once.
 */
export function usePatientPhotoUrl(fileId: string | null | undefined): string | null {
  const photo = useQuery({ ...patientQueries.photo(fileId ?? ''), enabled: Boolean(fileId) });
  return fileId ? (photo.data ?? null) : null;
}
