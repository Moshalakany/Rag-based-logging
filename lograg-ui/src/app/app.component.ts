import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ChatRequest, Message, ChatStreamEvent, Citation, HealthResponse, LogSourceInfo, IngestRequest } from './models/chat.model';
import { RagApiService } from './services/ragapi.service';
import { MarkdownModule } from 'ngx-markdown';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule, MarkdownModule],
  template: `
    <div class="container">
      <div class="header">
        <h1> Intelligent Logs inspector</h1>
        <p>Modern AI log analysis with retrieval-augmented intelligence</p>
      </div>

      <div class="content">
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
              <div *ngFor="let msg of messages" [class.user]="msg.role === 'user'" [class.assistant]="msg.role === 'assistant'" class="message">
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
              <h4>ingest first to load and populate the vector database with your logs, then ask questions about them in the chat panel</h4>
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
    </div>
  `,
  styles: [
    `
      .time-window {
        display: flex;
        align-items: center;
        gap: 8px;
        margin-bottom: 8px;
        flex-wrap: wrap;
      }
      .time-window label {
        font-size: 0.85rem;
        font-weight: 500;
        color: #d1d5db;
      }
      .time-window input {
        padding: 4px 8px;
        border: 1px solid #374151;
        border-radius: 6px;
        background: #1f2937;
        color: #f3f4f6;
        font-size: 0.85rem;
      }
      .btn-clear {
        background: transparent;
        border: 1px solid #4b5563;
        color: #9ca3af;
        padding: 4px 10px;
        border-radius: 4px;
        cursor: pointer;
        font-size: 0.8rem;
      }
      .btn-clear:hover {
        background: #374151;
        color: #f3f4f6;
      }
      .hint {
        font-size: 0.7rem;
        color: #6b7280;
        margin-left: 4px;
      }
      .collection-bar {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 6px 0;
        font-size: 0.8rem;
        color: #9ca3af;
        border-bottom: 1px solid #374151;
        margin-bottom: 4px;
      }
      .collection-input {
        flex: 0 0 200px;
        min-width: 160px;
      }
    `,
  ],
})
export class AppComponent implements OnInit {
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

  constructor(private ragApiService: RagApiService) {
    this.sessionId = this.generateSessionId();
  }

  ngOnInit(): void {
    this.checkApiHealth();
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
      // datetime-local returns "YYYY-MM-DDTHH:MM" — append seconds
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

  private generateSessionId(): string {
    return 'session_' + Math.random().toString(36).substr(2, 9);
  }
}
