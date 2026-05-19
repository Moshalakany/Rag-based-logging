import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ChatRequest, Message, ChatStreamEvent, Citation } from './models/chat.model';
import { RagApiService } from './services/ragapi.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="container">
      <div class="header">
        <h1>🔍 LogRAG</h1>
        <p>AI-Powered Log Analyzer with Retrieval-Augmented Generation</p>
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
                <div>{{ msg.content }}</div>
                <div *ngIf="msg.citations && msg.citations.length > 0" class="citations">
                  <div class="citations-title">📎 Citations:</div>
                  <div *ngFor="let citation of msg.citations" class="citation-item">
                    [{{ citation.id }}] {{ citation.timestamp }} | {{ citation.severity }} |
                    {{ citation.service_name }} ({{ citation.source_id }})
                  </div>
                </div>
              </div>

              <div *ngIf="isLoading" class="message loading">
                ⏳ Analyzing logs...
              </div>
            </div>

            <div class="input-group">
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
              <button (click)="triggerIngest()" [disabled]="isIngesting">
                {{ isIngesting ? '⏳ Ingesting...' : '📥 Ingest Now' }}
              </button>
              <div *ngIf="lastIngestResult" class="ingest-result">
                <strong>Last Ingest Result:</strong>
                <br />
                Logs Read: {{ lastIngestResult.raw_logs_read }}
                <br />
                Chunks Created: {{ lastIngestResult.chunks_created }}
                <br />
                Vectors Upserted: {{ lastIngestResult.vectors_upserted }}
                <br />
                Time: {{ lastIngestResult.completed_at_utc }}
              </div>
            </div>

            <div class="filter-section">
              <h3>Filters (Optional)</h3>
              <div class="filter-item">
                <label>Service Name</label>
                <input
                  [(ngModel)]="filter.service_name"
                  placeholder="e.g., payments"
                  type="text"
                />
              </div>
              <div class="filter-item">
                <label>Severity</label>
                <select [(ngModel)]="filter.severity">
                  <option value="">Any</option>
                  <option value="DEBUG">DEBUG</option>
                  <option value="INFO">INFO</option>
                  <option value="WARNING">WARNING</option>
                  <option value="ERROR">ERROR</option>
                  <option value="CRITICAL">CRITICAL</option>
                </select>
              </div>
              <div class="filter-item">
                <label>Source Type</label>
                <input
                  [(ngModel)]="filter.source_type"
                  placeholder="e.g., app, syslog"
                  type="text"
                />
              </div>
            </div>

            <div class="tips-section">
              <h3>ℹ️ Tips</h3>
              <ul>
                <li>Ask questions in natural language</li>
                <li>Use filters to narrow down results</li>
                <li>Ingest logs before asking questions</li>
                <li>Session persists across questions</li>
              </ul>
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [],
})
export class AppComponent implements OnInit {
  messages: Message[] = [];
  currentQuestion: string = '';
  isLoading: boolean = false;
  isIngesting: boolean = false;
  apiHealth: boolean = false;
  sessionId: string = '';
  lastIngestResult: any = null;
  filter: any = {};

  constructor(private ragApiService: RagApiService) {
    this.sessionId = this.generateSessionId();
  }

  ngOnInit(): void {
    this.checkApiHealth();
  }

  checkApiHealth(): void {
    this.ragApiService.health().subscribe(
      () => {
        this.apiHealth = true;
      },
      () => {
        this.apiHealth = false;
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

    this.isLoading = true;
    let fullResponse = '';
    let citations: Citation[] = [];

    const request: ChatRequest = {
      session_id: this.sessionId,
      question,
      top_k: 8,
      filter: Object.keys(this.filter).some((k) => this.filter[k]) ? this.filter : undefined,
    };

    this.ragApiService.streamChat(request).subscribe(
      (event: ChatStreamEvent) => {
        if (event.type === 'token' && event.content) {
          fullResponse += event.content;
        } else if (event.type === 'final') {
          if (event.content) {
            fullResponse = event.content;
          }
          if (event.metadata?.citations) {
            citations = event.metadata.citations;
          }
        }
      },
      () => {
        this.isLoading = false;
      },
      () => {
        this.isLoading = false;
        if (fullResponse) {
          this.messages.push({
            role: 'assistant',
            content: fullResponse,
            timestamp: new Date(),
            citations,
          });
        }
      }
    );
  }

  triggerIngest(): void {
    this.isIngesting = true;
    this.ragApiService.ingest().subscribe(
      (result) => {
        this.lastIngestResult = result;
        this.isIngesting = false;
      },
      () => {
        this.isIngesting = false;
      }
    );
  }

  private generateSessionId(): string {
    return 'session_' + Math.random().toString(36).substr(2, 9);
  }
}
