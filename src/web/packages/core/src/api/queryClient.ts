import { QueryClient } from '@tanstack/react-query';
import { isApiError } from './errors';

/**
 * Query defaults for a WiFi LAN: data is fresh for 30 s, refetched when the window regains focus or the network
 * comes back, and client errors (4xx) are never retried (the API client already retries transient failures).
 */
export function createHc360QueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        gcTime: 5 * 60_000,
        refetchOnWindowFocus: true,
        refetchOnReconnect: true,
        retry: (failureCount, error) => {
          if (isApiError(error) && error.status >= 400 && error.status < 500) {
            return false;
          }
          return failureCount < 2;
        },
      },
      mutations: {
        retry: false,
      },
    },
  });
}
