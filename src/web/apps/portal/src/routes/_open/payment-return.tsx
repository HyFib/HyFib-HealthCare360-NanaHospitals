import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { ButtonLink } from '@/components/links';
import { PortalPage } from '@/components/PortalPage';
import { Notice, PageSpinner } from '@/components/States';
import { useT } from '@/i18n';
import { peekAnyPendingPayment } from '@/lib/payments';

/**
 * Where the payment gateway sends the patient back after its own (hosted) payment page: the gateway has one return
 * address for the whole hospital (`integrations.payment.paymentLinkCallbackUrl`). The bill or appointment this tab was
 * paying for is opened, and its page confirms the payment with the gateway. Someone who paid a link the hospital sent
 * by SMS has nothing remembered here, so they are thanked: the gateway's webhook records their payment anyway.
 */
export const Route = createFileRoute('/_open/payment-return')({
  component: PaymentReturnPage,
});

function PaymentReturnPage() {
  const t = useT();
  const navigate = useNavigate();
  const [pending] = useState(peekAnyPendingPayment);

  useEffect(() => {
    if (pending?.kind === 'bill') {
      void navigate({ to: '/bills/$billId', params: { billId: pending.billId }, replace: true });
    } else if (pending?.kind === 'appointment') {
      void navigate({
        to: '/appointments/$appointmentId',
        params: { appointmentId: pending.appointmentId },
        replace: true,
      });
    }
  }, [navigate, pending]);

  if (pending) {
    return <PageSpinner label={t('payment.return.opening')} />;
  }
  return (
    <PortalPage title={t('payment.return.title')} width="narrow">
      <Notice intent="success">{t('payment.return.body')}</Notice>
      <ButtonLink appearance="primary" to="/bills">
        {t('nav.bills')}
      </ButtonLink>
    </PortalPage>
  );
}
