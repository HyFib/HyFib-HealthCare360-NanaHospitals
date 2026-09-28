/** RFC 9457 Problem Details as produced by the HC360 server. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  errors?: Record<string, string[]>;
  data?: unknown;
  traceId?: string;
  referenceId?: string;
}

/** Error thrown by the API client. `status` 0 means the server could not be reached (WiFi/network). */
export class ApiError extends Error {
  readonly status: number;
  readonly title: string;
  readonly detail?: string;
  readonly code?: string;
  readonly fieldErrors: Record<string, string[]>;
  readonly data?: unknown;
  readonly referenceId?: string;

  constructor(problem: ProblemDetails & { status: number }) {
    super(problem.detail ?? problem.title ?? 'Request failed');
    this.name = 'ApiError';
    this.status = problem.status;
    this.title = problem.title ?? 'Request failed';
    this.detail = problem.detail;
    this.code = problem.code;
    this.fieldErrors = problem.errors ?? {};
    this.data = problem.data;
    this.referenceId = problem.referenceId ?? problem.traceId;
  }

  get isNetworkError(): boolean {
    return this.status === 0;
  }

  get isValidation(): boolean {
    return this.status === 400 && Object.keys(this.fieldErrors).length > 0;
  }

  get isConflict(): boolean {
    return this.status === 409;
  }

  get isForbidden(): boolean {
    return this.status === 403;
  }

  /** The best single sentence to show a user. */
  get userMessage(): string {
    if (this.isValidation) {
      return Object.values(this.fieldErrors)[0]?.[0] ?? this.title;
    }
    return this.detail ?? this.title;
  }
}

export async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails = {};
  try {
    if ((response.headers.get('content-type') ?? '').includes('json')) {
      problem = (await response.json()) as ProblemDetails;
    }
  } catch {
    // Body was not JSON — fall back to a status-based title.
  }

  return new ApiError({ ...problem, status: response.status, title: problem.title ?? defaultTitle(response.status) });
}

function defaultTitle(status: number): string {
  switch (status) {
    case 401:
      return 'Please sign in again';
    case 403:
      return 'You do not have permission for this action';
    case 404:
      return 'Not found';
    case 409:
      return 'This record was changed elsewhere';
    case 429:
      return 'Too many requests — please wait a moment';
    default:
      return status >= 500 ? 'The server had a problem' : 'Request failed';
  }
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError;
}
