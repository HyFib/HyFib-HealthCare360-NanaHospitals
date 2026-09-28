import { Button, Caption1, makeStyles, ProgressBar, Text, tokens } from '@fluentui/react-components';
import { FoodRegular } from '@fluentui/react-icons';
import { todayIso } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { toIstInput } from '@/modules/inpatient/time';
import { dietaryQueries } from './api';
import { currentSlot, MEAL_SLOT_LABEL } from './rules';

const useStyles = makeStyles({
  rows: { display: 'flex', flexDirection: 'column', gap: '10px' },
  row: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '2px 12px', alignItems: 'center' },
  full: { gridColumn: '1 / -1' },
  muted: { color: tokens.colorNeutralForeground3 },
  review: { color: tokens.colorPaletteMarigoldForeground1, fontWeight: tokens.fontWeightSemibold },
});

/** The meal round in progress: delivered versus planned, ward by ward, and diets waiting for review. */
export function MealRoundWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canManageMeals = usePermission('dietary.meals.manage');
  const [slot] = useState(() => currentSlot(toIstInput(new Date()).slice(11, 16)));
  const today = todayIso();
  const plans = useQuery(dietaryQueries.mealPlans(today, null, slot));
  const canReadOrders = usePermission('dietary.orders.read');
  const review = useQuery({ ...dietaryQueries.orders({ needsReview: true }), enabled: canReadOrders });
  const wards = (plans.data ?? []).map((w) => {
    const live = w.items.filter((m) => m.status !== 'Cancelled');
    const done = live.filter((m) => m.status !== 'Planned').length;
    return { key: w.wardId ?? 'none', name: w.wardName ?? 'No ward', total: live.length, done };
  });

  return (
    <Section
      title={`${MEAL_SLOT_LABEL[slot]} round`}
      description="Today"
      actions={
        <Button size="small" appearance="subtle" icon={<FoodRegular />} onClick={() => void navigate({ to: '/dietary/meal-plans', search: { slot } })}>
          Meal plans
        </Button>
      }
    >
      {plans.error ? (
        <ErrorState error={plans.error} onRetry={() => void plans.refetch()} />
      ) : !plans.data ? (
        <Caption1 className={styles.muted}>Loading…</Caption1>
      ) : (
        <div className={styles.rows}>
          {wards.length === 0 ? (
            <Text className={styles.muted}>No meals planned for this round. {canManageMeals ? 'Generate today’s plan on the Meal plans page.' : ''}</Text>
          ) : (
            wards.map((w) => (
              <div key={w.key} className={styles.row}>
                <Text weight="semibold">{w.name}</Text>
                <Caption1>
                  {w.done}/{w.total} served
                </Caption1>
                <div className={styles.full}>
                  <ProgressBar value={w.total ? w.done / w.total : 0} thickness="large" color={w.done === w.total ? 'success' : 'brand'} aria-label={`${w.name}: ${w.done} of ${w.total} served`} />
                </div>
              </div>
            ))
          )}
          {review.data && review.data.length > 0 && (
            <Link to="/dietary" search={{ review: true }} className={styles.review}>
              {review.data.length} diet orders need review
            </Link>
          )}
        </div>
      )}
    </Section>
  );
}
