import { Component, type ErrorInfo, type ReactNode } from 'react';

interface ErrorBoundaryProps {
  children: ReactNode;
  title?: string;
}

interface ErrorBoundaryState {
  error: Error | null;
  componentStack: string | null;
}

/**
 * Sem isto, qualquer exceção de render desmonta a aplicação inteira e a tela fica em branco,
 * sem indicar o que falhou.
 */
export default class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null, componentStack: null };

  static getDerivedStateFromError(error: Error): Partial<ErrorBoundaryState> {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('[FinOps] Falha ao renderizar:', error, info.componentStack);
    this.setState({ componentStack: info.componentStack ?? null });
  }

  private handleReset = () => {
    this.setState({ error: null, componentStack: null });
  };

  render() {
    const { error, componentStack } = this.state;
    if (!error) {
      return this.props.children;
    }

    return (
      <div className="page">
        <div className="alert alert--warning">
          <strong>{this.props.title ?? 'Não foi possível exibir esta tela.'}</strong>
          <p>{error.message}</p>
          <details>
            <summary>Detalhes técnicos</summary>
            <pre className="error-boundary-stack">{`${error.stack ?? error.message}${componentStack ?? ''}`}</pre>
          </details>
          <button type="button" className="filter-control filter-control--button" onClick={this.handleReset}>
            Tentar novamente
          </button>
        </div>
      </div>
    );
  }
}
