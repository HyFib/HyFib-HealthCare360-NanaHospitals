import { Button, Caption1, makeStyles, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, MessageBar, MessageBarBody, MessageBarTitle, Text, tokens } from '@fluentui/react-components';
import { ClipboardHeartRegular, DropRegular, EditRegular, MoreHorizontalRegular, PersonProhibitedRegular } from '@fluentui/react-icons';
import { formatDate, formatDateOnly, formatDateTime, formatPhone, todayIso } from '@hc360/core';
import { DataTable, LoadingState, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { bloodQueries, type DonorDetail, type DonorScreeningDto } from '@/modules/blood-bank/api';
import { BloodGroupBadge, OutcomeBadge } from '@/modules/blood-bank/BloodBadges';
import { CollectDialog, DeferDialog, DonorDrawer, ScreeningDialog } from '@/modules/blood-bank/DonorDialogs';
import { bagTypeLabel, donationStatusLabel, donationTypeLabel, groupLabel } from '@/modules/blood-bank/labels';
import { Detail, DetailGrid, NoAccess, RecordLoadError } from '@/modules/ot/shared/feedback';
import { useNow } from '@/modules/ot/shared/hooks';

export const Route = createFileRoute('/_app/blood-bank/donors/$id')({
  component: DonorPage,
});

const useStyles = makeStyles({
  badges: { display: 'inline-flex', gap: '8px', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  reasons: { margin: 0, paddingLeft: '18px' },
  mono: { fontFamily: tokens.fontFamilyMonospace },
});

function DonorPage() {
  const { id } = Route.useParams();
  const canManage = usePermission('bloodbank.donors.manage');
  const donor = useQuery({ ...bloodQueries.donor(id), enabled: canManage });
  if (!canManage) {
    return (
      <Page>
        <NoAccess what="donor records" />
      </Page>
    );
  }
  if (donor.isLoading) {
    return <LoadingState label="Loading the donor…" />;
  }
  if (donor.error || !donor.data) {
    return (
      <Page maxWidth={900}>
        <RecordLoadError error={donor.error} onRetry={() => void donor.refetch()} what="donor" />
      </Page>
    );
  }
  return <DonorView donor={donor.data} />;
}

/** Latest eligible screening that can still be used for a collection (unused, within 12 hours). */
function usableScreening(donor: DonorDetail, now: number): DonorScreeningDto | null {
  const latest = donor.screenings[0];
  if (!latest || latest.outcome !== 'Eligible' || latest.donationId) {
    return null;
  }
  return now - Date.parse(latest.screenedAt) <= 12 * 3_600_000 ? latest : null;
}

function DonorView({ donor }: { donor: DonorDetail }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const now = useNow(60_000);
  const today = todayIso();
  const [dialog, setDialog] = useState<'screen' | 'edit' | 'defer' | null>(null);
  const [collecting, setCollecting] = useState<DonorScreeningDto | null>(null);
  const screening = usableScreening(donor, now);
  const deferred = donor.isPermanentlyDeferred || Boolean(donor.deferredUntil && donor.deferredUntil > today);
  const waiting = !deferred && donor.nextEligibleOn !== null && donor.nextEligibleOn > today;

  return (
    <Page>
      <PageHeader
        title={donor.fullName}
        subtitle={`Donor ${donor.donorNumber} · ${donor.age} · ${donor.gender} · ${donor.donationCount} donation${donor.donationCount === 1 ? '' : 's'}${
          donor.lastDonationAt ? ` · last ${formatDate(donor.lastDonationAt)}` : ''
        }`}
        breadcrumbs={[
          { label: 'Blood stock', onClick: () => void navigate({ to: '/blood-bank' }) },
          { label: 'Donors', onClick: () => void navigate({ to: '/blood-bank/donors' }) },
          { label: donor.donorNumber },
        ]}
        actions={
          <>
            <span className={styles.badges}>
              <BloodGroupBadge group={donor.bloodGroup} note={donor.bloodGroupConfirmed ? 'confirmed by the laboratory' : 'not yet confirmed'} />
              {!donor.bloodGroupConfirmed && donor.bloodGroup !== 'Unknown' && <Caption1 className={styles.muted}>not lab-confirmed</Caption1>}
            </span>
            {screening ? (
              <Button appearance="primary" icon={<DropRegular />} onClick={() => setCollecting(screening)}>
                Collect donation
              </Button>
            ) : (
              <Button appearance="primary" icon={<ClipboardHeartRegular />} disabled={deferred || !donor.isActive} onClick={() => setDialog('screen')}>
                Screen donor
              </Button>
            )}
            <Button icon={<EditRegular />} onClick={() => setDialog('edit')}>
              Edit
            </Button>
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button icon={<MoreHorizontalRegular />} aria-label="More actions" />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {screening && (
                    <MenuItem icon={<ClipboardHeartRegular />} onClick={() => setDialog('screen')}>
                      Screen again
                    </MenuItem>
                  )}
                  {!donor.isPermanentlyDeferred && (
                    <MenuItem icon={<PersonProhibitedRegular />} onClick={() => setDialog('defer')}>
                      Defer donor
                    </MenuItem>
                  )}
                </MenuList>
              </MenuPopover>
            </Menu>
          </>
        }
      />

      {deferred ? (
        <MessageBar intent={donor.isPermanentlyDeferred ? 'error' : 'warning'} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>{donor.isPermanentlyDeferred ? 'Permanently deferred' : `Deferred until ${formatDateOnly(donor.deferredUntil)}`}</MessageBarTitle>
            {donor.deferralReason ?? ''}
          </MessageBarBody>
        </MessageBar>
      ) : waiting ? (
        <MessageBar intent="info">
          <MessageBarBody>
            Can donate whole blood again from {formatDateOnly(donor.nextEligibleOn)} (minimum interval {donor.gender === 'Female' ? 120 : 90} days).
          </MessageBarBody>
        </MessageBar>
      ) : screening ? (
        <MessageBar intent="success">
          <MessageBarBody>
            Screened eligible at {formatDateTime(screening.screenedAt)}: collect up to {screening.maxBagVolumeMl ?? 350} ml within 12 hours of screening.
          </MessageBarBody>
        </MessageBar>
      ) : null}

      <Section title="Donor details">
        <DetailGrid>
          <Detail label="Date of birth">{formatDateOnly(donor.dateOfBirth)}</Detail>
          <Detail label="Mobile">{formatPhone(donor.phone)}</Detail>
          <Detail label="Email">{donor.email}</Detail>
          <Detail label="Address">{[donor.address.line1, donor.address.city, donor.address.district, donor.address.state, donor.address.pinCode].filter(Boolean).join(', ')}</Detail>
          <Detail label="Occupation">{donor.occupation}</Detail>
          <Detail label="Contact for drives">{donor.consentToNotify ? 'Yes' : 'No'}</Detail>
          <Detail label="Notes">{donor.notes}</Detail>
          <Detail label="Status">{donor.isActive ? 'Active' : 'Inactive'}</Detail>
        </DetailGrid>
      </Section>

      <Section title="Screenings" description="Newest first. Deferral reasons are shown as recorded." flush>
        <DataTable
          ariaLabel="Donor screenings"
          rows={donor.screenings}
          getRowId={(s) => s.id}
          size="small"
          columns={[
            { key: 'when', header: 'Screened', render: (s) => `${formatDateTime(s.screenedAt)}${s.screenedByName ? ` · ${s.screenedByName}` : ''}` },
            { key: 'outcome', header: 'Outcome', render: (s) => <OutcomeBadge outcome={s.outcome} /> },
            {
              key: 'values',
              header: 'Hb · weight · BP · pulse · temp',
              priority: 'low',
              render: (s) => `${s.haemoglobinGdl} g/dL · ${s.weightKg} kg · ${s.systolic}/${s.diastolic} · ${s.pulse} · ${s.temperatureC} °C`,
            },
            {
              key: 'reasons',
              header: 'Reasons',
              render: (s) =>
                s.reasons.length ? (
                  <ul className={styles.reasons}>
                    {s.reasons.map((r) => (
                      <li key={r}>{r}</li>
                    ))}
                  </ul>
                ) : s.donationId ? (
                  'Donated'
                ) : (
                  `Up to ${s.maxBagVolumeMl ?? 350} ml`
                ),
            },
          ]}
          emptyTitle="Not screened yet"
        />
      </Section>

      <Section title="Donations" flush>
        <DataTable
          ariaLabel="Donations"
          rows={donor.donations}
          getRowId={(d) => d.id}
          onRowClick={(d) => void navigate({ to: '/blood-bank/donations/$id', params: { id: d.id } })}
          size="small"
          columns={[
            { key: 'bag', header: 'Bag', render: (d) => <Text className={styles.mono}>{d.bagNumber}</Text> },
            { key: 'when', header: 'Collected', render: (d) => formatDateTime(d.collectedAt) },
            { key: 'what', header: 'Bag · volume', priority: 'low', render: (d) => `${bagTypeLabel[d.bagType]} · ${d.volumeMl} ml · ${donationTypeLabel[d.type]}` },
            { key: 'group', header: 'Group', render: (d) => groupLabel(d.bloodGroup) },
            {
              key: 'tti',
              header: 'Screening',
              render: (d) =>
                d.ttiReactive ? (
                  <StatusBadge status="Reactive" tone="danger" label="Reactive" />
                ) : d.ttiComplete ? (
                  <StatusBadge status="Cleared" tone="success" label="Cleared" />
                ) : (
                  <StatusBadge status="Pending" tone="warning" label="Pending" />
                ),
            },
            { key: 'status', header: 'Status', render: (d) => donationStatusLabel[d.status] },
          ]}
          emptyTitle="No donations yet"
        />
      </Section>

      {dialog === 'screen' && <ScreeningDialog donor={donor} onClose={() => setDialog(null)} onCollect={(s) => { setDialog(null); setCollecting(s); }} />}
      {dialog === 'edit' && <DonorDrawer donor={donor} onClose={() => setDialog(null)} />}
      {dialog === 'defer' && <DeferDialog donor={donor} onClose={() => setDialog(null)} />}
      {collecting && <CollectDialog donor={donor} screening={collecting} onClose={() => setCollecting(null)} />}
    </Page>
  );
}
