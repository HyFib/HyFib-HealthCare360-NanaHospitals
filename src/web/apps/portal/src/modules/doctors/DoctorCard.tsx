import { Avatar, makeStyles, tokens } from '@fluentui/react-components';
import { CalendarLtrRegular, ChevronRightRegular, LocalLanguageRegular, VideoRegular } from '@fluentui/react-icons';
import { CardLink } from '@/components/links';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { PublicDoctor } from '@/lib/types';
import { describeWhen } from '@/lib/when';

const useStyles = makeStyles({
  card: { alignItems: 'flex-start', padding: '16px' },
  body: { display: 'flex', flexDirection: 'column', gap: '6px', flex: 1, minWidth: 0 },
  name: { margin: 0, fontSize: tokens.fontSizeBase500, lineHeight: tokens.lineHeightBase500, fontWeight: tokens.fontWeightSemibold },
  line: { color: tokens.colorNeutralForeground2, overflowWrap: 'anywhere' },
  meta: { display: 'flex', alignItems: 'center', gap: '6px', color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase300 },
  next: { display: 'flex', alignItems: 'center', gap: '6px', color: tokens.colorStatusSuccessForeground1, fontWeight: tokens.fontWeightSemibold },
  none: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', flexWrap: 'wrap', gap: '8px', alignItems: 'center', marginTop: '4px' },
  fee: { fontWeight: tokens.fontWeightSemibold },
  chevron: { color: tokens.colorNeutralForeground3, fontSize: '20px', alignSelf: 'center', flexShrink: 0 },
});

/** One doctor in the directory: who, what they treat, languages, fee and the next free time. */
export function DoctorCard({ doctor, video, now }: { doctor: PublicDoctor; video: boolean; now: number }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const next = video ? doctor.nextTelehealthAt : doctor.nextAvailableAt;
  const specialty = [doctor.specialtyName, doctor.departmentName].filter((part, index, all) => part && all.indexOf(part) === index).join(' · ');

  return (
    <CardLink to="/doctors/$doctorId" params={{ doctorId: doctor.id }} search={video ? { mode: 'video' } : {}} className={styles.card}>
      <Avatar name={doctor.name} size={56} color="colorful" aria-hidden />
      <span className={styles.body}>
        <h2 className={styles.name}>{doctor.name}</h2>
        {doctor.qualification && <span className={styles.line}>{doctor.qualification}</span>}
        {specialty && <span className={styles.line}>{specialty}</span>}
        {doctor.languages.length > 0 && (
          <span className={styles.meta}>
            <LocalLanguageRegular aria-hidden />
            {t('doctors.card.languages', { languages: doctor.languages.join(', ') })}
          </span>
        )}
        {next ? (
          <span className={styles.next}>
            <CalendarLtrRegular aria-hidden />
            {t(video ? 'doctors.card.nextVideo' : 'doctors.card.next', { when: describeWhen(next, f, t, now) })}
          </span>
        ) : (
          <span className={styles.none}>{t('doctors.card.noSlots')}</span>
        )}
        <span className={styles.footer}>
          <span className={styles.fee}>{t('doctors.card.fee', { amount: f.inr(doctor.feeNew, { whole: true }) })}</span>
          {doctor.offersTelehealth && (
            <StatusPill tone="brand" icon={<VideoRegular />}>
              {t('doctors.card.video')}
            </StatusPill>
          )}
        </span>
      </span>
      <ChevronRightRegular className={styles.chevron} aria-hidden />
    </CardLink>
  );
}
