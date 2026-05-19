import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ChatRequest, Message, ChatStreamEvent, Citation } from './models/chat.model';
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
              <h4>ingest first to load and populate the vector database with your logs, then ask questions about them in the chat panel</h4>
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
  private streamingMessage: Message | null = null;

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
