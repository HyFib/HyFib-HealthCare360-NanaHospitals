import { ErrorState } from '@hc360/ui';
import { Component, type ErrorInfo, type ReactNode } from 'react';

interface State {
  error: unknown;
}

/** Keeps one failing widget from taking down the whole page. */
export class ErrorBoundary extends Component<{ children: ReactNode; fallback?: ReactNode }, State> {
  override state: State = { error: null };

  static getDerivedStateFromError(error: unknown): State {
    return { error };
  }

  override componentDidCatch(error: unknown, info: ErrorInfo): void {
    console.error('Widget failed', error, info.componentStack);
  }

  override render(): ReactNode {
    if (this.state.error) {
      return (
        this.props.fallback ?? (
          <ErrorState
            error={this.state.error}
            title="This panel could not be shown"
            onRetry={() => this.setState({ error: null })}
          />
        )
      );
    }
    return this.props.children;
  }
}
