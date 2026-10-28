import { Avatar, Button, Caption1, makeStyles, Spinner, Text, tokens } from '@fluentui/react-components';
import { ArrowRightRegular } from '@fluentui/react-icons';
import { LogoMark } from '@/shell/BrandMark';
import type { DemoMeta } from './install';
import { MORNING, PERSONAS } from './personas';

// Wide: the teal column (what this is, then the morning) beside the roles. Narrow: what this is, the roles, and the
// morning last, so a phone reaches the roles at once.
const useStyles = makeStyles({
  root: {
    minHeight: '100%',
    display: 'grid',
    gridTemplateColumns: 'minmax(320px, 0.85fr) minmax(0, 1.6fr)',
    gridTemplateRows: 'auto 1fr',
    gridTemplateAreas: '"head roles" "morning roles"',
    backgroundColor: tokens.colorNeutralBackground2,
    '@media (max-width: 960px)': {
      gridTemplateColumns: 'minmax(0, 1fr)',
      gridTemplateRows: 'auto',
      gridTemplateAreas: '"head" "roles" "morning"',
    },
  },
  // The sign-in page's teal (brand 50 to 70), in two parts that meet at brand 60: white text keeps at least 5.8:1.
  brand: {
    gridArea: 'head',
    background: 'linear-gradient(180deg, #064F6B 0%, #045E80 100%)',
    color: '#fff',
    padding: '40px max(28px, env(safe-area-inset-left, 0px)) 28px',
    display: 'flex',
    flexDirection: 'column',
    gap: '24px',
  },
  morningPanel: {
    gridArea: 'morning',
    background: 'linear-gradient(180deg, #045E80 0%, #016D95 100%)',
    color: '#fff',
    padding: '4px max(28px, env(safe-area-inset-left, 0px)) 32px',
    display: 'flex',
    flexDirection: 'column',
    gap: '24px',
    '@media (max-width: 960px)': { paddingTop: '32px' },
  },
  wordmark: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  eyebrow: {
    alignSelf: 'flex-start',
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.12em',
    textTransform: 'uppercase',
    border: '1px solid rgba(255,255,255,0.6)',
    borderRadius: tokens.borderRadiusCircular,
    padding: '2px 10px',
  },
  title: {
    margin: 0,
    fontSize: '34px',
    lineHeight: '42px',
    fontWeight: tokens.fontWeightSemibold,
    textWrap: 'balance',
  },
  lead: {
    margin: 0,
    color: 'rgba(255,255,255,0.9)',
    fontSize: tokens.fontSizeBase400,
    lineHeight: tokens.lineHeightBase400,
    maxWidth: '34em',
  },
  morning: { display: 'grid', gap: '12px', margin: 0, padding: 0, listStyle: 'none' },
  morningTitle: {
    margin: 0,
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: '0.06em',
    textTransform: 'uppercase',
    color: 'rgba(255,255,255,0.85)',
  },
  // A timeline: the time in a column of its own, what happened beside it.
  moment: { display: 'grid', gridTemplateColumns: '3.4em minmax(0, 1fr)', gap: '10px', alignItems: 'baseline' },
  momentTime: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'end',
  },
  momentWhat: {
    color: 'rgba(255,255,255,0.88)',
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase300,
  },
  footer: { marginTop: 'auto', color: 'rgba(255,255,255,0.85)', fontSize: tokens.fontSizeBase200 },
  roles: {
    gridArea: 'roles',
    padding: '40px max(24px, env(safe-area-inset-right, 0px)) 48px max(24px, env(safe-area-inset-left, 0px))',
    display: 'flex',
    flexDirection: 'column',
    gap: '20px',
    minWidth: 0,
  },
  rolesHead: { display: 'flex', flexDirection: 'column', gap: '6px', maxWidth: '46em' },
  rolesTitle: {
    margin: 0,
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  loading: { display: 'flex', alignItems: 'center', gap: '8px', color: tokens.colorNeutralForeground2 },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(min(100%, 300px), 1fr))', gap: '16px' },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '14px',
    padding: '18px',
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
    minWidth: 0,
  },
  who: { display: 'flex', alignItems: 'center', gap: '12px' },
  whoText: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  name: { margin: 0, fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  places: { display: 'grid', gap: '4px', margin: 0, padding: 0, listStyle: 'none' },
  place: {
    width: '100%',
    justifyContent: 'flex-start',
    textAlign: 'start',
    alignItems: 'flex-start',
    padding: '6px 8px',
    height: 'auto',
    minHeight: '32px',
    fontWeight: tokens.fontWeightRegular,
  },
  placeText: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  placeLabel: { fontWeight: tokens.fontWeightSemibold, color: tokens.colorBrandForeground1 },
  open: { marginTop: 'auto', alignSelf: 'flex-start' },
});

export interface LandingProps {
  meta: DemoMeta;
  ready: boolean;
  error: string | null;
  onChoose: (role: string, path?: string) => void;
}

/** A mobile number as a patient types it: 90000 01021. */
const mobileNumber = (phone: string) =>
  phone
    .replace(/\D/g, '')
    .slice(-10)
    .replace(/^(\d{5})/, '$1 ');

/** The demo's front page: what it is, the morning it recorded, and who you can be in it. */
export function Landing({ meta, ready, error, onChoose }: LandingProps) {
  const styles = useStyles();
  const people = new Map(meta.roles.map((r) => [r.role, r]));
  return (
    <div className={styles.root}>
      <section className={styles.brand} aria-labelledby="demo-title">
        <span className={styles.wordmark}>
          <LogoMark size={32} />
          HealthCare360
        </span>
        <span className={styles.eyebrow}>Demo</span>
        <h1 id="demo-title" className={styles.title}>
          A morning at {meta.facility}
        </h1>
        <p className={styles.lead}>
          This is the HealthCare360 staff app and patient portal, unchanged, replaying a morning recorded from their own
          server: patients in the outpatient clinic, on the wards and in casualty. Choose who to be. Every screen of
          that role is here, and nothing you change is saved.
        </p>
      </section>

      <main className={styles.roles}>
        <div className={styles.rolesHead}>
          <h2 className={styles.rolesTitle}>Choose who to be</h2>
          <Text className={styles.muted}>
            Each member of staff sees the menu and screens of their own role, and the patient their own portal. Sign in
            as one, or go straight to a screen where their part of the morning shows.
          </Text>
          {!ready && !error && (
            <span className={styles.loading}>
              <Spinner size="tiny" aria-hidden /> Loading the recorded morning…
            </span>
          )}
          {error && <Text role="alert">{error}</Text>}
        </div>
        <div className={styles.grid}>
          {PERSONAS.filter((p) => people.has(p.role)).map((persona) => {
            const person = people.get(persona.role)!;
            const headingId = `role-${persona.role}`;
            return (
              <section key={persona.role} className={styles.card} aria-labelledby={headingId}>
                <div className={styles.who}>
                  <Avatar name={person.displayName} color="colorful" size={40} aria-hidden />
                  <span className={styles.whoText}>
                    <h3 id={headingId} className={styles.name}>
                      {person.displayName}
                    </h3>
                    <Caption1 className={styles.muted}>
                      {person.app === 'portal' && person.phone
                        ? `Patient portal · mobile ${mobileNumber(person.phone)}`
                        : `${person.roleName} · user name ${person.userName}`}
                    </Caption1>
                  </span>
                </div>
                <Text>{persona.does}</Text>
                <ul className={styles.places} aria-label={`Where ${person.displayName}'s morning shows`}>
                  {persona.places.map((place) => (
                    <li key={place.path}>
                      <Button
                        appearance="subtle"
                        className={styles.place}
                        disabled={!ready}
                        onClick={() => onChoose(persona.role, place.path)}
                      >
                        <span className={styles.placeText}>
                          <span className={styles.placeLabel}>{place.label}</span>
                          <Caption1>{place.note}</Caption1>
                        </span>
                      </Button>
                    </li>
                  ))}
                </ul>
                <Button
                  className={styles.open}
                  appearance="primary"
                  icon={<ArrowRightRegular />}
                  iconPosition="after"
                  disabled={!ready}
                  onClick={() => onChoose(persona.role)}
                >
                  Sign in as {person.displayName}
                </Button>
              </section>
            );
          })}
        </div>
      </main>

      <section className={styles.morningPanel} aria-labelledby="demo-morning">
        <h2 id="demo-morning" className={styles.morningTitle}>
          The morning so far
        </h2>
        <ol className={styles.morning}>
          {MORNING.map((m) => (
            <li key={m.time} className={styles.moment}>
              <span className={styles.momentTime}>{m.time}</span>
              <span className={styles.momentWhat}>{m.what}</span>
            </li>
          ))}
        </ol>
        <span className={styles.footer}>
          Recorded {meta.recordedOn}, India Standard Time. The hospital, its staff and its patients are made up.
        </span>
      </section>
    </div>
  );
}
