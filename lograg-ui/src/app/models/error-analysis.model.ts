export interface TraceLogItem {
  timestampUtc: string;
  severity: string;
  serviceName: string;
  sourceId: string;
  correlationId: string;
  message: string;
  statusCode?: number | null;
  rawText: string;
}

export interface TraceRcaResult {
  summary: string;
  culpritComponent: string;
  rootCause: string;
  impact: string;
  suggestedFix: string;
  completedAtUtc: string;
}

export interface ErrorCorrelationTrace {
  correlationId: string;
  servicesInvolved: string[];
  startTimeUtc: string;
  endTimeUtc: string;
  errorCount: number;
  logs: TraceLogItem[];
  rcaResult?: TraceRcaResult | null;
}

export interface ErrorAnalysisSession {
  sessionId: string;
  name: string;
  fromUtc?: string | null;
  toUtc?: string | null;
  status: 'Running' | 'Completed' | 'Stopped' | 'Failed';
  progressStage: string;
  progressMessage: string;
  collectionName: string;
  createdAtUtc: string;
  completedAtUtc?: string | null;
  totalRawScanned: number;
  errorsFound: number;
  correlatedTracesCount: number;
  vectorsUpserted: number;
  rcaCompletedCount: number;
  percentComplete?: number;
  errorMessage?: string | null;
  traces: ErrorCorrelationTrace[];
}

export interface ErrorAnalysisSessionSummary {
  sessionId: string;
  name: string;
  fromUtc?: string | null;
  toUtc?: string | null;
  status: 'Running' | 'Completed' | 'Stopped' | 'Failed';
  progressStage: string;
  createdAtUtc: string;
  completedAtUtc?: string | null;
  errorsFound: number;
  correlatedTracesCount: number;
  rcaCompletedCount: number;
  percentComplete?: number;
}

export interface CreateErrorAnalysisRequest {
  name?: string;
  fromUtc?: string | null;
  toUtc?: string | null;
  maxTracesToAnalyze?: number | null;
}

export interface ErrorAnalysisProgressEvent {
  sessionId: string;
  stage: string;
  message: string;
  totalRawScanned: number;
  errorsFound: number;
  correlatedTracesCount: number;
  vectorsUpserted: number;
  rcaCompletedCount: number;
  rcaTotalCount: number;
  percentComplete: number;
  timestampUtc: string;
}
