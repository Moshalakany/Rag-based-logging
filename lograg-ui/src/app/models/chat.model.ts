export interface ChatRequest {
  session_id?: string;
  question: string;
  top_k?: number;
  filter?: QueryFilter;
  collection_name?: string;
}

export interface QueryFilter {
  service_name?: string;
  severity?: string;
  source_type?: string;
  from_utc?: string;
  to_utc?: string;
}

export interface ChatStreamEvent {
  type: 'meta' | 'token' | 'final';
  content?: string;
  metadata?: any;
}

export interface Citation {
  id: number;
  timestamp: string;
  severity: string;
  service_name: string;
  trace_id: string;
  source_id: string;
}

export interface Message {
  role: 'user' | 'assistant';
  content: string;
  timestamp: Date;
  citations?: Citation[];
}

export interface IngestionResult {
  raw_logs_read: number;
  chunks_created: number;
  vectors_upserted: number;
  completed_at_utc: string;
  collection_name?: string;
  window_from_utc?: string;
  window_to_utc?: string;
}

export interface IngestRequest {
  from_utc?: string;
  to_utc?: string;
  collection_name?: string;
}

export interface HealthResponse {
  status: string;
  sources?: LogSourceInfo[];
}

export interface LogSourceInfo {
  id: string;
  type: string;
  sourceKind: string;
}
