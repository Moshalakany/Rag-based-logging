import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ChatRequest, Message, ChatStreamEvent, Citation, HealthResponse, LogSourceInfo, IngestRequest } from './models/chat.model';
import {
  ErrorAnalysisSession,
  ErrorAnalysisSessionSummary,
  ErrorCorrelationTrace,
  ErrorAnalysisProgressEvent,
  CreateErrorAnalysisRequest,
} from './models/error-analysis.model';
import { RagApiService } from './services/ragapi.service';
import { MarkdownModule } from 'ngx-markdown';
import { Subscription } from 'rxjs';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule, MarkdownModule],
  template: `
    <div class="container">
      <div class="header">
        <h1>Intelligent Logs Inspector</h1>
        <p>AI log analysis with retrieval-augmented intelligence & distributed error root-cause diagnosis</p>
      </div>

      <!-- Top Navigation Tabs -->
      <div class="tab-nav">
        <button
          class="tab-btn"
          [class.active]="activeTab === 'chat'"
          (click)="setActiveTab('chat')"
        >
          💬 Chat & Log Ingestion
        </button>
        <button
          class="tab-btn"
          [class.active]="activeTab === 'error-analysis'"
          (click)="setActiveTab('error-analysis')"
        >
          🚨 Error Correlation & Root Cause Analysis
          <span *ngIf="isAnalysisRunning" class="pulse-indicator"></span>
        </button>
      </div>

      <!-- ════════════════════════════════════════════════════════════════ -->
      <!-- TAB 1: EXISTING CHAT & INGESTION                                -->
      <!-- ════════════════════════════════════════════════════════════════ -->
      <div class="content" *ngIf="activeTab === 'chat'">
        <!-- Chat Panel -->
        <div class="panel">
          <div class="panel-header">Chat with Logs</div>
          <div class="panel-body">
            <div *ngIf="!apiHealth" class="error-box">
              ⚠️ API is not reachable. Make sure the backend is running on http://localhost:5000
            </div>

            <div *ngIf="apiHealth" class="info-box">
              ✅ Connected to API. Session ID: {{ sessionId.substring(0, 8) }}...
            </div>

            <div class="chat-messages">
              <div
                *ngFor="let msg of messages"
                [class.user]="msg.role === 'user'"
                [class.assistant]="msg.role === 'assistant'"
                class="message"
              >
                <ng-container *ngIf="msg.role === 'assistant'; else userMessage">
                  <markdown [data]="msg.content"></markdown>
                </ng-container>
                <ng-template #userMessage>
                  <div>{{ msg.content }}</div>
                </ng-template>
                <div *ngIf="msg.citations && msg.citations.length > 0" class="citations">
                  <div class="citations-title">📎 Citations:</div>
                  <div *ngFor="let citation of msg.citations" class="citation-item">
                    [{{ citation.id }}] {{ citation.timestamp }} | {{ citation.severity }} |
                    {{ citation.service_name }} ({{ citation.source_id }})
                  </div>
                </div>
              </div>

              <div *ngIf="isLoading" class="message loading">
                <span class="spinner" aria-hidden="true"></span>
                <span>Analyzing logs...</span>
              </div>
            </div>

            <div class="collection-bar" *ngIf="lastIngestResult?.collection_name">
              <span>🔍 Searching: <strong>{{ collectionName || 'log_chunks (default)' }}</strong></span>
              <span class="hint">(last ingest: {{ lastIngestResult.collection_name }})</span>
            </div>

            <div class="input-group">
              <input
                [(ngModel)]="collectionName"
                placeholder="Collection (default: log_chunks)"
                type="text"
                class="collection-input"
              />
              <input
                [(ngModel)]="currentQuestion"
                (keyup.enter)="askQuestion()"
                [disabled]="isLoading || !apiHealth"
                placeholder="Ask a question about logs..."
                type="text"
              />
              <button (click)="askQuestion()" [disabled]="isLoading || !apiHealth || !currentQuestion.trim()">
                Send
              </button>
            </div>
          </div>
        </div>

        <!-- Control Panel -->
        <div class="panel">
          <div class="panel-header">Controls & Settings</div>
          <div class="panel-body">
            <div class="ingest-section">
              <h3>Ingest Logs</h3>
              <h4>Ingest first to load and populate the vector database with your logs, then ask questions about them in the chat panel</h4>
              <div class="time-window">
                <label>From (local time):</label>
                <input type="datetime-local" [(ngModel)]="ingestFromUtc" />
                <label>To (local time):</label>
                <input type="datetime-local" [(ngModel)]="ingestToUtc" />
                <button (click)="clearTimeWindow()" class="btn-clear" *ngIf="ingestFromUtc || ingestToUtc">Clear</button>
                <span class="hint">Times are sent as UTC after conversion</span>
              </div>
              <button (click)="triggerIngest()" [disabled]="isIngesting">
                {{ isIngesting ? '⏳ Ingesting...' : '📥 Ingest Now' }}
              </button>
              <div *ngIf="lastIngestResult" class="ingest-result">
                <strong>Last Ingest Result:</strong>
                <br />
                Collection: {{ lastIngestResult.collection_name || 'log_chunks (default)' }}
                <br />
                Logs Read: {{ lastIngestResult.raw_logs_read }}
                <br />
                Chunks Created: {{ lastIngestResult.chunks_created }}
                <br />
                Vectors Upserted: {{ lastIngestResult.vectors_upserted }}
                <br />
                <span *ngIf="lastIngestResult.window_from_utc">
                  Window: {{ lastIngestResult.window_from_utc }} → {{ lastIngestResult.window_to_utc }}
                  <br />
                </span>
                Time: {{ lastIngestResult.completed_at_utc }}
              </div>
            </div>

            <div class="sources-section" *ngIf="availableSources.length > 0">
              <h3>Configured Log Sources</h3>
              <div *ngFor="let source of availableSources" class="source-item">
                <span class="source-kind">{{ source.sourceKind }}</span>
                <span class="source-id">{{ source.id }}</span>
                <span class="source-type">({{ source.type }})</span>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- ════════════════════════════════════════════════════════════════ -->
      <!-- TAB 2: ERROR CORRELATION & ROOT CAUSE ANALYSIS                  -->
      <!-- ════════════════════════════════════════════════════════════════ -->
      <div class="error-analysis-container" *ngIf="activeTab === 'error-analysis'">
        <!-- Session Control Bar -->
        <div class="ea-control-bar panel">
          <div class="ea-bar-row">
            <!-- Session Selector -->
            <div class="ea-session-picker">
              <label>Select Analysis Session:</label>
              <select [(ngModel)]="selectedSessionId" (change)="onSessionSelectChange()">
                <option value="__NEW__">➕ Start New Analysis Session...</option>
                <option *ngFor="let s of errorSessions" [value]="s.sessionId">
                  [{{ s.status }}] {{ s.name }} ({{ s.errorsFound }} errors, {{ s.correlatedTracesCount }} traces)
                </option>
              </select>
            </div>

            <!-- Quick Action Buttons for Selected Session -->
            <div class="ea-actions" *ngIf="selectedSession && selectedSessionId !== '__NEW__'">
              <span class="status-badge" [ngClass]="selectedSession.status.toLowerCase()">
                {{ selectedSession.status }}
              </span>
              <button
                *ngIf="selectedSession.status === 'Running'"
                (click)="stopActiveSession()"
                [disabled]="isStoppingSession"
                class="btn-danger"
              >
                🛑 Stop Session
              </button>
              <button
                (click)="deleteCurrentSession()"
                [disabled]="isDeletingSession"
                class="btn-outline-danger"
              >
                🗑️ Delete Session
              </button>
              <button (click)="refreshCurrentSession()" class="btn-secondary" title="Reload from server">
                🔄 Refresh
              </button>
            </div>
          </div>

          <!-- New Session Form (shown when creating new session or selectedSessionId === '__NEW__') -->
          <div class="ea-new-form" *ngIf="selectedSessionId === '__NEW__'">
            <div class="ea-form-grid">
              <div class="form-group">
                <label>Session Name (optional):</label>
                <input
                  type="text"
                  [(ngModel)]="errorAnalysisName"
                  placeholder="e.g. Incident-Payment-Timeout"
                />
              </div>
              <div class="form-group">
                <label>From (local time):</label>
                <input type="datetime-local" [(ngModel)]="errorFromUtc" />
              </div>
              <div class="form-group">
                <label>To (local time):</label>
                <input type="datetime-local" [(ngModel)]="errorToUtc" />
              </div>
              <div class="form-group form-actions">
                <button
                  (click)="startErrorAnalysis()"
                  [disabled]="isStartingAnalysis"
                  class="btn-primary-glow"
                >
                  {{ isStartingAnalysis ? '⏳ Initializing...' : '🚀 Start Root Cause Analysis' }}
                </button>
                <button type="button" (click)="setSampleLogsWindow()" class="btn-secondary" title="Fills May 19, 2026 where sample logs are dated">
                  📅 Set Sample Date (May 2026)
                </button>
                <button (click)="clearErrorTimeWindow()" class="btn-clear" *ngIf="errorFromUtc || errorToUtc">
                  Clear Dates (All Logs)
                </button>
              </div>
            </div>
            <p class="form-hint">
              ℹ️ The pipeline filters all logs for errors (HTTP 5xx, ERROR severity, unhandled exceptions), extracts their Correlation IDs, gathers all matching logs across all components (Ocelot, ACL, TMS, Identity, SourceOfFund), vectorizes them into an isolated Qdrant session collection, and generates an AI Root-Cause deduction for every incident.
            </p>
          </div>
        </div>

        <!-- Live Progress Banner (shown if running or actively loaded) -->
        <div class="ea-progress-card panel" *ngIf="selectedSession && (selectedSession.status === 'Running' || activeProgress)">
          <div class="ea-progress-header">
            <div class="stage-tag">
              <span class="spinner-small" *ngIf="selectedSession.status === 'Running'"></span>
              Stage: <strong>{{ activeProgress?.stage || selectedSession.progressStage }}</strong>
            </div>
            <div class="progress-pct">
              {{ activeProgress?.percentComplete ?? (selectedSession.status === 'Completed' ? 100 : 0) }}%
            </div>
          </div>

          <!-- Progress Bar -->
          <div class="progress-track">
            <div
              class="progress-fill"
              [style.width.%]="activeProgress?.percentComplete ?? (selectedSession.status === 'Completed' ? 100 : 15)"
            ></div>
          </div>

          <div class="ea-progress-message">
            {{ activeProgress?.message || selectedSession.progressMessage }}
          </div>

          <!-- Live Metric Chips -->
          <div class="ea-stat-chips">
            <div class="chip">
              <span class="chip-num">{{ activeProgress?.totalRawScanned ?? selectedSession.totalRawScanned }}</span>
              <span class="chip-label">Logs Scanned</span>
            </div>
            <div class="chip chip-danger">
              <span class="chip-num">{{ activeProgress?.errorsFound ?? selectedSession.errorsFound }}</span>
              <span class="chip-label">Errors Detected</span>
            </div>
            <div class="chip chip-info">
              <span class="chip-num">{{ activeProgress?.correlatedTracesCount ?? selectedSession.correlatedTracesCount }}</span>
              <span class="chip-label">Correlation Traces</span>
            </div>
            <div class="chip chip-purple">
              <span class="chip-num">{{ activeProgress?.vectorsUpserted ?? selectedSession.vectorsUpserted }}</span>
              <span class="chip-label">Vectors Stored</span>
            </div>
            <div class="chip chip-success">
              <span class="chip-num">{{ activeProgress?.rcaCompletedCount ?? selectedSession.rcaCompletedCount }}</span>
              <span class="chip-label">AI Root Causes</span>
            </div>
          </div>
        </div>

        <!-- Main Dashboard View: Traces Grid + Session Chat -->
        <div class="ea-dashboard-layout" *ngIf="selectedSession && selectedSessionId !== '__NEW__'">
          <!-- Left: Error Correlation Traces & AI RCA Cards -->
          <div class="ea-traces-column">
            <div class="column-header">
              <h2>
                Correlated Error Incidents
                <span class="counter-badge">{{ filteredTraces.length }}</span>
              </h2>
              <input
                type="text"
                [(ngModel)]="traceFilterSearch"
                placeholder="🔍 Filter by Correlation ID or Service..."
                class="search-input"
              />
            </div>

            <!-- Empty State -->
            <div *ngIf="selectedSession.traces.length === 0" class="empty-state">
              <span class="empty-icon">🔎</span>
              <h3>No Error Traces Found</h3>
              <p *ngIf="selectedSession.status === 'Running'">
                Scanning logs and correlating transactions across components...
              </p>
              <p *ngIf="selectedSession.status === 'Completed'" class="empty-msg">
                {{ selectedSession.progressMessage }}
              </p>
              <p *ngIf="selectedSession.status === 'Stopped'">
                Session was stopped before any error traces were correlated.
              </p>
              <div class="empty-actions" *ngIf="selectedSession.status === 'Completed' && (selectedSession.fromUtc || selectedSession.toUtc)">
                <button (click)="reanalyzeAllLogs()" class="btn-primary-glow">
                  ⚡ Re-run without Date Filter (All Logs)
                </button>
                <button (click)="reanalyzeSampleLogs()" class="btn-secondary">
                  📅 Re-run for Sample Logs (May 19, 2026)
                </button>
              </div>
            </div>

            <!-- Trace Cards List -->
            <div class="traces-list">
              <div
                *ngFor="let trace of filteredTraces"
                class="trace-card"
                [class.expanded]="expandedTraces[trace.correlationId]"
              >
                <!-- Card Header -->
                <div class="trace-header" (click)="toggleTrace(trace.correlationId)">
                  <div class="trace-header-left">
                    <span class="cid-badge">
                      🔗 {{ trace.correlationId }}
                    </span>
                    <button
                      class="btn-copy"
                      (click)="$event.stopPropagation(); copyToClipboard(trace.correlationId)"
                      title="Copy Correlation ID"
                    >
                      📋
                    </button>
                    <span class="error-badge">
                      {{ trace.errorCount }} {{ trace.errorCount === 1 ? 'Error' : 'Errors' }}
                    </span>
                  </div>

                  <div class="trace-header-right">
                    <span class="service-chain">
                      <span *ngFor="let s of trace.servicesInvolved; let last = last" class="service-tag">
                        {{ s }}<span *ngIf="!last" class="chain-arrow"> ➔ </span>
                      </span>
                    </span>
                    <span class="chevron">{{ expandedTraces[trace.correlationId] ? '▲' : '▼' }}</span>
                  </div>
                </div>

                <!-- Card Body -->
                <div class="trace-body" *ngIf="expandedTraces[trace.correlationId]">
                  <!-- AI Root Cause Card -->
                  <div class="rca-box" *ngIf="trace.rcaResult">
                    <div class="rca-header">
                      <span class="ai-badge">🤖 AI Root Cause Deduction</span>
                      <span class="rca-culprit" *ngIf="trace.rcaResult.culpritComponent">
                        🚨 First Failed Component: <strong>{{ trace.rcaResult.culpritComponent }}</strong>
                      </span>
                    </div>

                    <div class="rca-section">
                      <div class="rca-label">📌 Incident Summary:</div>
                      <div class="rca-value">{{ trace.rcaResult.summary }}</div>
                    </div>

                    <div class="rca-section">
                      <div class="rca-label">🔍 Root Cause:</div>
                      <markdown class="rca-markdown" [data]="trace.rcaResult.rootCause"></markdown>
                    </div>

                    <div class="rca-grid">
                      <div class="rca-section">
                        <div class="rca-label">💥 Blast Radius & Impact:</div>
                        <div class="rca-value">{{ trace.rcaResult.impact }}</div>
                      </div>
                      <div class="rca-section">
                        <div class="rca-label">💡 Recommended Remediation:</div>
                        <div class="rca-value highlight">{{ trace.rcaResult.suggestedFix }}</div>
                      </div>
                    </div>
                  </div>

                  <!-- Pending RCA Indicator -->
                  <div class="rca-box pending" *ngIf="!trace.rcaResult && selectedSession.status === 'Running'">
                    <span class="spinner-small"></span>
                    <span>AI Root Cause Analysis in progress for this correlation trace...</span>
                  </div>

                  <!-- Timeline Section Toggle -->
                  <div class="timeline-toggle-bar">
                    <button
                      class="btn-toggle-timeline"
                      (click)="toggleTimeline(trace.correlationId)"
                    >
                      {{ expandedTimelines[trace.correlationId] ? 'Hide' : 'Show' }} Cross-Component Log Timeline ({{ trace.logs.length }} events)
                    </button>
                    <span class="time-range-hint">
                      {{ trace.startTimeUtc | date : 'HH:mm:ss.SSS' }} → {{ trace.endTimeUtc | date : 'HH:mm:ss.SSS' }}
                    </span>
                  </div>

                  <!-- Chronological Timeline -->
                  <div class="timeline-container" *ngIf="expandedTimelines[trace.correlationId]">
                    <div
                      *ngFor="let log of trace.logs"
                      class="timeline-item"
                      [ngClass]="'sev-' + log.severity.toLowerCase()"
                    >
                      <div class="tl-time">{{ log.timestampUtc | date : 'HH:mm:ss.SSS' }}</div>
                      <div class="tl-service">{{ log.serviceName }}</div>
                      <div class="tl-sev">{{ log.severity }}</div>
                      <div class="tl-code" *ngIf="log.statusCode">[{{ log.statusCode }}]</div>
                      <div class="tl-msg">{{ log.message }}</div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>

          <!-- Right: Session-Scoped Chat Panel -->
          <div class="ea-chat-column panel">
            <div class="panel-header">
              💬 Chat with Session Errors
              <span class="hint-small">Scoped to {{ selectedSession.collectionName }}</span>
            </div>
            <div class="panel-body">
              <div class="chat-messages session-chat-box">
                <div *ngIf="sessionMessages.length === 0" class="chat-welcome">
                  Ask targeted questions about the errors in this session (e.g. "Which transaction took the longest?", "Explain why TMS failed", "Show SQL errors").
                </div>

                <div
                  *ngFor="let msg of sessionMessages"
                  [class.user]="msg.role === 'user'"
                  [class.assistant]="msg.role === 'assistant'"
                  class="message"
                >
                  <ng-container *ngIf="msg.role === 'assistant'; else userSessionMsg">
                    <markdown [data]="msg.content"></markdown>
                  </ng-container>
                  <ng-template #userSessionMsg>
                    <div>{{ msg.content }}</div>
                  </ng-template>
                  <div *ngIf="msg.citations && msg.citations.length > 0" class="citations">
                    <div class="citations-title">📎 Correlated Citations:</div>
                    <div *ngFor="let citation of msg.citations" class="citation-item">
                      [{{ citation.id }}] {{ citation.timestamp }} | {{ citation.severity }} |
                      {{ citation.service_name }}
                    </div>
                  </div>
                </div>

                <div *ngIf="isSessionChatLoading" class="message loading">
                  <span class="spinner" aria-hidden="true"></span>
                  <span>Investigating session vectors...</span>
                </div>
              </div>

              <div class="input-group">
                <input
                  [(ngModel)]="sessionQuestion"
                  (keyup.enter)="askSessionQuestion()"
                  [disabled]="isSessionChatLoading"
                  placeholder="Ask about these errors..."
                  type="text"
                />
                <button
                  (click)="askSessionQuestion()"
                  [disabled]="isSessionChatLoading || !sessionQuestion.trim()"
                >
                  Send
                </button>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
      /* Tab Navigation */
      .tab-nav {
        display: flex;
        gap: 12px;
        margin-bottom: 24px;
        border-bottom: 2px solid #1f2937;
        padding-bottom: 8px;
      }
      .tab-btn {
        background: transparent;
        border: none;
        color: #94a3b8;
        font-size: 1.05rem;
        font-weight: 600;
        padding: 10px 20px;
        cursor: pointer;
        border-radius: 8px;
        transition: all 0.2s ease;
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .tab-btn:hover {
        color: #f8fafc;
        background: rgba(255, 255, 255, 0.05);
      }
      .tab-btn.active {
        color: #38bdf8;
        background: rgba(56, 189, 248, 0.12);
        border-bottom: 3px solid #38bdf8;
      }
      .pulse-indicator {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: #ef4444;
        display: inline-block;
        box-shadow: 0 0 8px #ef4444;
        animation: pulse 1.5s infinite;
      }
      @keyframes pulse {
        0% { transform: scale(0.9); opacity: 1; }
        50% { transform: scale(1.4); opacity: 0.5; }
        100% { transform: scale(0.9); opacity: 1; }
      }

      /* Error Analysis Layout */
      .error-analysis-container {
        display: flex;
        flex-direction: column;
        gap: 20px;
      }

      .ea-control-bar {
        padding: 20px;
      }
      .ea-bar-row {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 16px;
        flex-wrap: wrap;
      }
      .ea-session-picker {
        display: flex;
        align-items: center;
        gap: 12px;
        flex: 1;
        min-width: 320px;
      }
      .ea-session-picker label {
        font-weight: 600;
        color: #cbd5e1;
        font-size: 0.95rem;
      }
      .ea-session-picker select {
        flex: 1;
        padding: 8px 14px;
        background: #1e293b;
        color: #f8fafc;
        border: 1px solid #334155;
        border-radius: 8px;
        font-size: 0.95rem;
      }
      .ea-actions {
        display: flex;
        align-items: center;
        gap: 10px;
      }

      /* Status Badges */
      .status-badge {
        font-size: 0.8rem;
        font-weight: 700;
        padding: 4px 12px;
        border-radius: 20px;
        text-transform: uppercase;
        letter-spacing: 0.05em;
      }
      .status-badge.running {
        background: rgba(56, 189, 248, 0.2);
        color: #38bdf8;
        border: 1px solid #38bdf8;
      }
      .status-badge.completed {
        background: rgba(34, 197, 94, 0.2);
        color: #22c55e;
        border: 1px solid #22c55e;
      }
      .status-badge.stopped {
        background: rgba(245, 158, 11, 0.2);
        color: #f59e0b;
        border: 1px solid #f59e0b;
      }
      .status-badge.failed {
        background: rgba(239, 68, 68, 0.2);
        color: #ef4444;
        border: 1px solid #ef4444;
      }

      /* Buttons */
      .btn-primary-glow {
        background: linear-gradient(135deg, #2563eb 0%, #1d4ed8 100%);
        color: #ffffff;
        border: none;
        padding: 10px 22px;
        border-radius: 8px;
        font-weight: 600;
        cursor: pointer;
        box-shadow: 0 4px 14px rgba(37, 99, 235, 0.4);
        transition: all 0.2s ease;
      }
      .btn-primary-glow:hover:not(:disabled) {
        transform: translateY(-1px);
        box-shadow: 0 6px 20px rgba(37, 99, 235, 0.6);
      }
      .btn-primary-glow:disabled {
        opacity: 0.6;
        cursor: not-allowed;
      }
      .btn-danger {
        background: #dc2626;
        color: #ffffff;
        border: none;
        padding: 8px 16px;
        border-radius: 6px;
        font-weight: 600;
        cursor: pointer;
      }
      .btn-danger:hover {
        background: #b91c1c;
      }
      .btn-outline-danger {
        background: transparent;
        color: #f87171;
        border: 1px solid #ef4444;
        padding: 8px 14px;
        border-radius: 6px;
        cursor: pointer;
      }
      .btn-outline-danger:hover {
        background: rgba(239, 68, 68, 0.15);
      }
      .btn-secondary {
        background: #334155;
        color: #f8fafc;
        border: none;
        padding: 8px 14px;
        border-radius: 6px;
        cursor: pointer;
      }
      .btn-secondary:hover {
        background: #475569;
      }

      /* New Session Form */
      .ea-new-form {
        margin-top: 18px;
        padding-top: 18px;
        border-top: 1px solid #1f2937;
      }
      .ea-form-grid {
        display: flex;
        gap: 16px;
        align-items: flex-end;
        flex-wrap: wrap;
      }
      .form-group {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .form-group label {
        font-size: 0.85rem;
        color: #94a3b8;
        font-weight: 500;
      }
      .form-group input {
        padding: 8px 12px;
        background: #1e293b;
        color: #f8fafc;
        border: 1px solid #334155;
        border-radius: 6px;
        font-size: 0.9rem;
      }
      .form-actions {
        flex-direction: row;
        align-items: center;
        gap: 8px;
      }
      .form-hint {
        margin-top: 12px;
        font-size: 0.82rem;
        color: #94a3b8;
        line-height: 1.5;
      }

      /* Live Progress Banner */
      .ea-progress-card {
        padding: 20px;
        background: #0b1329;
        border-left: 4px solid #38bdf8;
      }
      .ea-progress-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        margin-bottom: 10px;
      }
      .stage-tag {
        font-size: 0.95rem;
        color: #f8fafc;
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .progress-pct {
        font-size: 1.1rem;
        font-weight: 700;
        color: #38bdf8;
      }
      .progress-track {
        height: 8px;
        background: #1e293b;
        border-radius: 4px;
        overflow: hidden;
        margin-bottom: 12px;
      }
      .progress-fill {
        height: 100%;
        background: linear-gradient(90deg, #38bdf8 0%, #3b82f6 100%);
        transition: width 0.4s ease;
      }
      .ea-progress-message {
        font-size: 0.9rem;
        color: #94a3b8;
        margin-bottom: 16px;
      }
      .ea-stat-chips {
        display: flex;
        gap: 12px;
        flex-wrap: wrap;
      }
      .chip {
        background: #1e293b;
        padding: 8px 16px;
        border-radius: 8px;
        display: flex;
        flex-direction: column;
        border: 1px solid #334155;
      }
      .chip-num {
        font-size: 1.3rem;
        font-weight: 700;
        color: #f8fafc;
      }
      .chip-label {
        font-size: 0.75rem;
        color: #94a3b8;
        text-transform: uppercase;
      }
      .chip-danger .chip-num { color: #f87171; }
      .chip-info .chip-num { color: #38bdf8; }
      .chip-purple .chip-num { color: #c084fc; }
      .chip-success .chip-num { color: #4ade80; }

      /* Dashboard Layout */
      .ea-dashboard-layout {
        display: grid;
        grid-template-columns: minmax(0, 1.6fr) minmax(0, 1fr);
        gap: 24px;
        align-items: start;
      }
      .ea-traces-column {
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .column-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 16px;
      }
      .column-header h2 {
        font-size: 1.25rem;
        color: #f8fafc;
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .counter-badge {
        background: #2563eb;
        color: #ffffff;
        font-size: 0.8rem;
        padding: 2px 8px;
        border-radius: 12px;
      }
      .search-input {
        padding: 6px 12px;
        background: #1e293b;
        color: #f8fafc;
        border: 1px solid #334155;
        border-radius: 6px;
        font-size: 0.85rem;
        width: 260px;
      }

      /* Trace Card */
      .trace-card {
        background: #0f172a;
        border: 1px solid #1e293b;
        border-radius: 12px;
        overflow: hidden;
        transition: border-color 0.2s ease;
      }
      .trace-card:hover {
        border-color: #334155;
      }
      .trace-header {
        padding: 16px 20px;
        background: #131d36;
        cursor: pointer;
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 12px;
        user-select: none;
      }
      .trace-header-left {
        display: flex;
        align-items: center;
        gap: 10px;
        flex-wrap: wrap;
      }
      .cid-badge {
        font-family: monospace;
        font-weight: 700;
        color: #38bdf8;
        font-size: 0.95rem;
      }
      .btn-copy {
        background: transparent;
        border: none;
        cursor: pointer;
        font-size: 0.9rem;
        padding: 2px;
      }
      .error-badge {
        background: #7f1d1d;
        color: #fca5a5;
        font-size: 0.75rem;
        font-weight: 700;
        padding: 2px 8px;
        border-radius: 4px;
        text-transform: uppercase;
      }
      .trace-header-right {
        display: flex;
        align-items: center;
        gap: 12px;
      }
      .service-tag {
        font-size: 0.8rem;
        color: #cbd5e1;
        background: #1e293b;
        padding: 2px 8px;
        border-radius: 4px;
        border: 1px solid #334155;
      }
      .chain-arrow {
        color: #64748b;
        margin: 0 2px;
      }
      .chevron {
        color: #94a3b8;
        font-size: 0.85rem;
      }

      /* Trace Body & RCA Card */
      .trace-body {
        padding: 20px;
        display: flex;
        flex-direction: column;
        gap: 16px;
        background: #0f172a;
      }
      .rca-box {
        background: #16203c;
        border: 1px solid #23345e;
        border-radius: 10px;
        padding: 18px;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .rca-box.pending {
        display: flex;
        flex-direction: row;
        align-items: center;
        gap: 12px;
        color: #94a3b8;
        font-style: italic;
      }
      .rca-header {
        display: flex;
        justify-content: space-between;
        align-items: center;
        border-bottom: 1px solid #23345e;
        padding-bottom: 10px;
        flex-wrap: wrap;
        gap: 8px;
      }
      .ai-badge {
        font-size: 0.95rem;
        font-weight: 700;
        color: #38bdf8;
      }
      .rca-culprit {
        font-size: 0.88rem;
        color: #f87171;
        background: rgba(239, 68, 68, 0.1);
        padding: 3px 10px;
        border-radius: 4px;
        border: 1px solid rgba(239, 68, 68, 0.3);
      }
      .rca-section {
        display: flex;
        flex-direction: column;
        gap: 4px;
      }
      .rca-label {
        font-size: 0.82rem;
        font-weight: 600;
        color: #94a3b8;
        text-transform: uppercase;
        letter-spacing: 0.05em;
      }
      .rca-value {
        font-size: 0.95rem;
        color: #e2e8f0;
        line-height: 1.5;
      }
      .rca-value.highlight {
        color: #4ade80;
        font-weight: 500;
      }
      .rca-grid {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 16px;
      }
      .rca-markdown {
        color: #cbd5e1;
        font-size: 0.92rem;
        line-height: 1.6;
      }

      /* Timeline */
      .timeline-toggle-bar {
        display: flex;
        justify-content: space-between;
        align-items: center;
      }
      .btn-toggle-timeline {
        background: #1e293b;
        color: #93c5fd;
        border: 1px solid #3b82f6;
        padding: 6px 14px;
        border-radius: 6px;
        font-size: 0.85rem;
        font-weight: 600;
        cursor: pointer;
      }
      .btn-toggle-timeline:hover {
        background: #2563eb;
        color: #ffffff;
      }
      .time-range-hint {
        font-size: 0.8rem;
        color: #64748b;
        font-family: monospace;
      }
      .timeline-container {
        display: flex;
        flex-direction: column;
        gap: 6px;
        max-height: 380px;
        overflow-y: auto;
        padding: 12px;
        background: #090e1a;
        border-radius: 8px;
        border: 1px solid #1e293b;
      }
      .timeline-item {
        display: grid;
        grid-template-columns: 95px 110px 65px 50px 1fr;
        gap: 8px;
        font-size: 0.82rem;
        font-family: monospace;
        padding: 6px 10px;
        border-radius: 4px;
        align-items: center;
        background: rgba(255, 255, 255, 0.02);
      }
      .timeline-item.sev-error {
        background: rgba(239, 68, 68, 0.15);
        border-left: 3px solid #ef4444;
      }
      .timeline-item.sev-warning {
        background: rgba(245, 158, 11, 0.1);
        border-left: 3px solid #f59e0b;
      }
      .timeline-item.sev-info {
        border-left: 3px solid #3b82f6;
      }
      .tl-time { color: #94a3b8; }
      .tl-service { color: #38bdf8; font-weight: 600; }
      .tl-sev { font-weight: 700; }
      .sev-error .tl-sev { color: #f87171; }
      .sev-warning .tl-sev { color: #fbbf24; }
      .sev-info .tl-sev { color: #60a5fa; }
      .tl-code { color: #f43f5e; font-weight: 700; }
      .tl-msg { color: #e2e8f0; word-break: break-word; }

      /* Session Chat */
      .ea-chat-column {
        position: sticky;
        top: 20px;
        max-height: 85vh;
      }
      .session-chat-box {
        min-height: 340px;
        max-height: 520px;
      }
      .chat-welcome {
        color: #64748b;
        font-size: 0.9rem;
        line-height: 1.5;
        padding: 20px;
        text-align: center;
        font-style: italic;
      }
      .hint-small {
        font-size: 0.75rem;
        color: #64748b;
        margin-left: 8px;
        font-weight: normal;
      }

      /* Empty State */
      .empty-state {
        text-align: center;
        padding: 40px;
        background: #0f172a;
        border-radius: 12px;
        border: 1px dashed #334155;
      }
      .empty-icon {
        font-size: 2.5rem;
        display: block;
        margin-bottom: 12px;
      }
      .empty-state h3 {
        color: #f8fafc;
        margin-bottom: 8px;
      }
      .empty-state p {
        color: #94a3b8;
        font-size: 0.9rem;
      }
      .empty-msg {
        max-width: 600px;
        margin: 0 auto 16px;
        color: #cbd5e1 !important;
        line-height: 1.5;
      }
      .empty-actions {
        display: flex;
        gap: 12px;
        justify-content: center;
        align-items: center;
        flex-wrap: wrap;
        margin-top: 14px;
      }

      .spinner-small {
        display: inline-block;
        width: 14px;
        height: 14px;
        border: 2px solid rgba(255, 255, 255, 0.3);
        border-top-color: #38bdf8;
        border-radius: 50%;
        animation: spin 0.8s linear infinite;
      }
      @keyframes spin {
        to { transform: rotate(360deg); }
      }
    `,
  ],
})
export class AppComponent implements OnInit, OnDestroy {
  // Navigation
  activeTab: 'chat' | 'error-analysis' = 'chat';

  // ── Tab 1 State ──
  messages: Message[] = [];
  currentQuestion: string = '';
  isLoading: boolean = false;
  isIngesting: boolean = false;
  apiHealth: boolean = false;
  sessionId: string = '';
  lastIngestResult: any = null;
  availableSources: LogSourceInfo[] = [];
  ingestFromUtc: string = '';
  ingestToUtc: string = '';
  collectionName: string = '';
  private streamingMessage: Message | null = null;

  // ── Tab 2 State (Error Correlation & RCA) ──
  errorSessions: ErrorAnalysisSessionSummary[] = [];
  selectedSessionId: string = '__NEW__';
  selectedSession: ErrorAnalysisSession | null = null;
  errorAnalysisName: string = '';
  errorFromUtc: string = '';
  errorToUtc: string = '';
  isStartingAnalysis: boolean = false;
  isStoppingSession: boolean = false;
  isDeletingSession: boolean = false;
  isAnalysisRunning: boolean = false;
  activeProgress: ErrorAnalysisProgressEvent | null = null;
  traceFilterSearch: string = '';

  expandedTraces: { [correlationId: string]: boolean } = {};
  expandedTimelines: { [correlationId: string]: boolean } = {};

  // Tab 2 Chat
  sessionMessages: Message[] = [];
  sessionQuestion: string = '';
  isSessionChatLoading: boolean = false;
  private streamingSessionMessage: Message | null = null;

  // Subscriptions & Timers
  private progressSubscription: Subscription | null = null;
  private pollingTimer: any = null;

  constructor(private ragApiService: RagApiService) {
    this.sessionId = this.generateSessionId();
  }

  ngOnInit(): void {
    this.checkApiHealth();
    this.loadErrorSessions();
  }

  ngOnDestroy(): void {
    this.stopProgressStreaming();
    if (this.pollingTimer) {
      clearInterval(this.pollingTimer);
    }
  }

  setActiveTab(tab: 'chat' | 'error-analysis'): void {
    this.activeTab = tab;
    if (tab === 'error-analysis') {
      this.loadErrorSessions();
    }
  }

  checkApiHealth(): void {
    this.ragApiService.health().subscribe(
      (response: HealthResponse) => {
        this.apiHealth = true;
        this.availableSources = response.sources || [];
      },
      () => {
        this.apiHealth = false;
        this.availableSources = [];
      }
    );
  }

  // ════════════════════════════════════════════════════════════════
  // TAB 1 METHODS
  // ════════════════════════════════════════════════════════════════

  askQuestion(): void {
    if (!this.currentQuestion.trim() || this.isLoading) {
      return;
    }

    const question = this.currentQuestion.trim();
    this.currentQuestion = '';

    this.messages.push({
      role: 'user',
      content: question,
      timestamp: new Date(),
    });

    this.streamingMessage = {
      role: 'assistant',
      content: '',
      timestamp: new Date(),
      citations: [],
    };
    this.messages.push(this.streamingMessage);

    this.isLoading = true;
    let fullResponse = '';
    let citations: Citation[] = [];

    const request: ChatRequest = {
      session_id: this.sessionId,
      question,
      top_k: 8,
      collection_name: this.collectionName || undefined,
    };

    this.ragApiService.streamChat(request).subscribe(
      (event: ChatStreamEvent) => {
        if (event.type === 'token' && event.content) {
          fullResponse += event.content;
          if (this.streamingMessage) {
            this.streamingMessage.content = fullResponse;
          }
        } else if (event.type === 'final') {
          if (event.content) {
            fullResponse = event.content;
          }
          if (event.metadata?.citations) {
            citations = event.metadata.citations;
          }
          if (this.streamingMessage) {
            this.streamingMessage.content = fullResponse;
            this.streamingMessage.citations = citations;
          }
        }
      },
      () => {
        this.isLoading = false;
        if (this.streamingMessage && !this.streamingMessage.content) {
          this.messages = this.messages.filter((msg) => msg !== this.streamingMessage);
        }
        this.streamingMessage = null;
      },
      () => {
        this.isLoading = false;
        if (this.streamingMessage) {
          this.streamingMessage.content = fullResponse;
          this.streamingMessage.citations = citations;
        }
        this.streamingMessage = null;
      }
    );
  }

  triggerIngest(): void {
    this.isIngesting = true;
    const body: IngestRequest = {};
    if (this.ingestFromUtc) {
      body.from_utc = new Date(this.ingestFromUtc + ':00').toISOString();
    }
    if (this.ingestToUtc) {
      body.to_utc = new Date(this.ingestToUtc + ':00').toISOString();
    }
    this.ragApiService.ingest(body).subscribe(
      (result) => {
        this.lastIngestResult = result;
        this.isIngesting = false;
      },
      () => {
        this.isIngesting = false;
      }
    );
  }

  clearTimeWindow(): void {
    this.ingestFromUtc = '';
    this.ingestToUtc = '';
  }

  // ════════════════════════════════════════════════════════════════
  // TAB 2 METHODS (Error Analysis & RCA)
  // ════════════════════════════════════════════════════════════════

  loadErrorSessions(): void {
    this.ragApiService.listErrorSessions().subscribe((sessions) => {
      this.errorSessions = sessions;
      if (this.selectedSessionId === '__NEW__' && sessions.length > 0 && !this.selectedSession) {
        // Auto-select most recent session if available
        this.selectedSessionId = sessions[0].sessionId;
        this.loadSessionDetails(this.selectedSessionId);
      }
    });
  }

  onSessionSelectChange(): void {
    if (this.selectedSessionId === '__NEW__') {
      this.selectedSession = null;
      this.stopProgressStreaming();
      this.sessionMessages = [];
    } else {
      this.loadSessionDetails(this.selectedSessionId);
    }
  }

  loadSessionDetails(sessionId: string): void {
    this.stopProgressStreaming();
    this.sessionMessages = [];
    this.ragApiService.getErrorSession(sessionId).subscribe((session) => {
      this.selectedSession = session;
      this.isAnalysisRunning = session.status === 'Running';

      // Auto-expand the first trace if available
      if (session.traces && session.traces.length > 0) {
        this.expandedTraces[session.traces[0].correlationId] = true;
      }

      if (session.status === 'Running') {
        this.startProgressStreaming(sessionId);
      }
    });
  }

  startErrorAnalysis(): void {
    this.isStartingAnalysis = true;
    const req: CreateErrorAnalysisRequest = {
      name: this.errorAnalysisName.trim() || undefined,
      fromUtc: this.errorFromUtc ? new Date(this.errorFromUtc + ':00').toISOString() : undefined,
      toUtc: this.errorToUtc ? new Date(this.errorToUtc + ':00').toISOString() : undefined,
    };

    this.ragApiService.createErrorSession(req).subscribe(
      (session) => {
        this.isStartingAnalysis = false;
        this.selectedSession = session;
        this.selectedSessionId = session.sessionId;
        this.isAnalysisRunning = true;
        this.loadErrorSessions();
        this.startProgressStreaming(session.sessionId);
      },
      (err) => {
        this.isStartingAnalysis = false;
        alert('Failed to start error analysis session: ' + (err.error?.message || err.message));
      }
    );
  }

  startProgressStreaming(sessionId: string): void {
    this.stopProgressStreaming();

    this.progressSubscription = this.ragApiService
      .streamErrorSessionProgress(sessionId)
      .subscribe({
        next: (event) => {
          this.activeProgress = event;
          if (this.selectedSession && this.selectedSession.sessionId === sessionId) {
            this.selectedSession.progressStage = event.stage;
            this.selectedSession.progressMessage = event.message;
            this.selectedSession.totalRawScanned = event.totalRawScanned;
            this.selectedSession.errorsFound = event.errorsFound;
            this.selectedSession.correlatedTracesCount = event.correlatedTracesCount;
            this.selectedSession.vectorsUpserted = event.vectorsUpserted;
            this.selectedSession.rcaCompletedCount = event.rcaCompletedCount;

            if (event.stage === 'Completed') {
              this.selectedSession.status = 'Completed';
              this.isAnalysisRunning = false;
              this.loadSessionDetails(sessionId); // reload complete trace results
              this.loadErrorSessions();
            } else if (event.stage === 'Stopped') {
              this.selectedSession.status = 'Stopped';
              this.isAnalysisRunning = false;
            } else if (event.stage === 'Failed') {
              this.selectedSession.status = 'Failed';
              this.isAnalysisRunning = false;
            }
          }
        },
        error: () => {
          // If SSE disconnects while running, fall back to REST refresh
          if (this.isAnalysisRunning) {
            this.startPolling(sessionId);
          }
        },
        complete: () => {
          if (this.selectedSession && this.selectedSession.status === 'Running') {
            this.loadSessionDetails(sessionId);
          }
        },
      });
  }

  private startPolling(sessionId: string): void {
    if (this.pollingTimer) {
      clearInterval(this.pollingTimer);
    }
    this.pollingTimer = setInterval(() => {
      this.ragApiService.getErrorSession(sessionId).subscribe((s) => {
        this.selectedSession = s;
        if (s.status !== 'Running') {
          this.isAnalysisRunning = false;
          clearInterval(this.pollingTimer);
          this.pollingTimer = null;
          this.loadErrorSessions();
        }
      });
    }, 2500);
  }

  stopProgressStreaming(): void {
    if (this.progressSubscription) {
      this.progressSubscription.unsubscribe();
      this.progressSubscription = null;
    }
    if (this.pollingTimer) {
      clearInterval(this.pollingTimer);
      this.pollingTimer = null;
    }
    this.activeProgress = null;
  }

  stopActiveSession(): void {
    if (!this.selectedSession) {
      return;
    }
    if (!confirm(`Are you sure you want to stop the analysis for session "${this.selectedSession.name}"? This will cancel the ongoing scan and drop its Qdrant collection.`)) {
      return;
    }

    this.isStoppingSession = true;
    this.ragApiService.stopErrorSession(this.selectedSession.sessionId).subscribe({
      next: () => {
        this.isStoppingSession = false;
        this.isAnalysisRunning = false;
        if (this.selectedSession) {
          this.selectedSession.status = 'Stopped';
          this.selectedSession.progressStage = 'Stopped';
        }
        this.stopProgressStreaming();
        this.loadErrorSessions();
      },
      error: (err) => {
        this.isStoppingSession = false;
        alert('Failed to stop session: ' + err.message);
      },
    });
  }

  deleteCurrentSession(): void {
    if (!this.selectedSession) {
      return;
    }
    if (!confirm(`Delete session "${this.selectedSession.name}" permanently? Its isolated Qdrant vector collection will be dropped.`)) {
      return;
    }

    this.isDeletingSession = true;
    const sid = this.selectedSession.sessionId;
    this.ragApiService.deleteErrorSession(sid).subscribe({
      next: () => {
        this.isDeletingSession = false;
        this.stopProgressStreaming();
        this.selectedSession = null;
        this.selectedSessionId = '__NEW__';
        this.loadErrorSessions();
      },
      error: (err) => {
        this.isDeletingSession = false;
        alert('Failed to delete session: ' + err.message);
      },
    });
  }

  refreshCurrentSession(): void {
    if (this.selectedSession) {
      this.loadSessionDetails(this.selectedSession.sessionId);
    }
  }

  clearErrorTimeWindow(): void {
    this.errorFromUtc = '';
    this.errorToUtc = '';
  }

  setSampleLogsWindow(): void {
    this.errorFromUtc = '2026-05-18T00:00';
    this.errorToUtc = '2026-05-20T23:59';
  }

  reanalyzeAllLogs(): void {
    this.selectedSessionId = '__NEW__';
    this.selectedSession = null;
    this.clearErrorTimeWindow();
    this.errorAnalysisName = 'Error-Analysis-All-Logs';
    this.startErrorAnalysis();
  }

  reanalyzeSampleLogs(): void {
    this.selectedSessionId = '__NEW__';
    this.selectedSession = null;
    this.setSampleLogsWindow();
    this.errorAnalysisName = 'Error-Analysis-May2026';
    this.startErrorAnalysis();
  }

  toggleTrace(correlationId: string): void {
    this.expandedTraces[correlationId] = !this.expandedTraces[correlationId];
  }

  toggleTimeline(correlationId: string): void {
    this.expandedTimelines[correlationId] = !this.expandedTimelines[correlationId];
  }

  copyToClipboard(text: string): void {
    navigator.clipboard.writeText(text).then(() => {
      // feedback can be added if needed
    });
  }

  get filteredTraces(): ErrorCorrelationTrace[] {
    if (!this.selectedSession || !this.selectedSession.traces) {
      return [];
    }
    if (!this.traceFilterSearch.trim()) {
      return this.selectedSession.traces;
    }
    const q = this.traceFilterSearch.trim().toLowerCase();
    return this.selectedSession.traces.filter(
      (t) =>
        t.correlationId.toLowerCase().includes(q) ||
        t.servicesInvolved.some((s) => s.toLowerCase().includes(q)) ||
        (t.rcaResult?.culpritComponent && t.rcaResult.culpritComponent.toLowerCase().includes(q))
    );
  }

  // ── Tab 2 Session Chat ──
  askSessionQuestion(): void {
    if (!this.selectedSession || !this.sessionQuestion.trim() || this.isSessionChatLoading) {
      return;
    }

    const question = this.sessionQuestion.trim();
    this.sessionQuestion = '';

    this.sessionMessages.push({
      role: 'user',
      content: question,
      timestamp: new Date(),
    });

    this.streamingSessionMessage = {
      role: 'assistant',
      content: '',
      timestamp: new Date(),
      citations: [],
    };
    this.sessionMessages.push(this.streamingSessionMessage);

    this.isSessionChatLoading = true;
    let fullResponse = '';
    let citations: Citation[] = [];

    const request: ChatRequest = {
      question,
      top_k: 8,
    };

    this.ragApiService.streamErrorSessionChat(this.selectedSession.sessionId, request).subscribe({
      next: (event: ChatStreamEvent) => {
        if (event.type === 'token' && event.content) {
          fullResponse += event.content;
          if (this.streamingSessionMessage) {
            this.streamingSessionMessage.content = fullResponse;
          }
        } else if (event.type === 'final') {
          if (event.content) {
            fullResponse = event.content;
          }
          if (event.metadata?.citations) {
            citations = event.metadata.citations;
          }
          if (this.streamingSessionMessage) {
            this.streamingSessionMessage.content = fullResponse;
            this.streamingSessionMessage.citations = citations;
          }
        }
      },
      error: () => {
        this.isSessionChatLoading = false;
        if (this.streamingSessionMessage && !this.streamingSessionMessage.content) {
          this.sessionMessages = this.sessionMessages.filter((m) => m !== this.streamingSessionMessage);
        }
        this.streamingSessionMessage = null;
      },
      complete: () => {
        this.isSessionChatLoading = false;
        if (this.streamingSessionMessage) {
          this.streamingSessionMessage.content = fullResponse;
          this.streamingSessionMessage.citations = citations;
        }
        this.streamingSessionMessage = null;
      },
    });
  }

  private generateSessionId(): string {
    return 'session_' + Math.random().toString(36).substr(2, 9);
  }
}
