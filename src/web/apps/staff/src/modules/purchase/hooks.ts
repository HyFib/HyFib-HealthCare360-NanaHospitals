import { useSession } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { usePermission } from '@/lib/permissions';
import { facilityQuery } from '@/modules/inventory/api';

/**
 * The hospital's GSTIN (facility master, needs the masters permission that every staff role has by default). It
 * decides CGST + SGST versus IGST in purchase previews; `undefined` while loading, `null` when unknown.
 */
export function useFacilityGstin(): string | null | undefined {
  const facilityId = useSession((s) => s.user?.facilityId);
  const canReadMasters = usePermission('masters.read');
  const facility = useQuery({ ...facilityQuery(facilityId), enabled: Boolean(facilityId) && canReadMasters });
  if (!canReadMasters || !facilityId || facility.error) {
    return null;
  }
  return facility.data ? facility.data.gstin : undefined;
}
