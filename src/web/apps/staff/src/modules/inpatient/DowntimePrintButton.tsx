import { Button, Spinner } from '@fluentui/react-components';
import { PrintRegular } from '@fluentui/react-icons';
import { useNotify } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { useLetterhead } from '@/lib/letterhead';
import { printDowntimePack } from './downtimePrint';
import { wardCareApi } from './wardCareApi';

/**
 * Prints what a ward needs to work without HC360 (downtime procedure, step 2): one ward's sheet, or with no ward every
 * ward's as one pack. Read fresh for each print, so the medicines due are the next 8 hours from now.
 */
export function DowntimePrintButton({ wardId, label }: { wardId: string | null; label: string }) {
  const notify = useNotify();
  const letterhead = useLetterhead();
  const print = useMutation({
    mutationFn: async () => printDowntimePack(await wardCareApi.downtimePack(wardId), letterhead),
    onError: (error) => notify.error(error, 'Could not print the downtime sheet'),
  });
  return (
    <Button
      icon={print.isPending ? <Spinner size="tiny" aria-hidden /> : <PrintRegular />}
      disabled={print.isPending}
      onClick={() => print.mutate()}
    >
      {label}
    </Button>
  );
}
