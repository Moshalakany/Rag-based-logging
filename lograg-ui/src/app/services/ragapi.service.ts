import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ChatRequest, ChatStreamEvent, IngestionResult } from '../models/chat.model';

@Injectable({
  providedIn: 'root',
})
export class RagApiService {
  private apiUrl = '/api';

  constructor(private http: HttpClient) {}

  health(): Observable<any> {
    return this.http.get(`${this.apiUrl}/health`);
  }

  ingest(): Observable<IngestionResult> {
    return this.http.post<IngestionResult>(`${this.apiUrl}/ingest`, {});
  }

  streamChat(request: ChatRequest): Observable<ChatStreamEvent> {
    return new Observable((observer) => {
      const abortController = new AbortController();

      (async () => {
        try {
          const response = await fetch(`${this.apiUrl}/chat`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(request),
            signal: abortController.signal,
          });

          if (!response.ok) {
            throw new Error(`Chat request failed with status ${response.status}`);
          }

          if (!response.body) {
            throw new Error('Chat stream response body is empty');
          }

          const reader = response.body.getReader();
          const decoder = new TextDecoder();
          let buffer = '';

          while (true) {
            const { done, value } = await reader.read();
            if (done) {
              break;
            }

            buffer += decoder.decode(value, { stream: true });
            buffer = this.emitSseEvents(buffer, observer);
          }

          buffer += decoder.decode();
          this.emitSseEvents(buffer, observer);
          observer.complete();
        } catch (error) {
          if (error instanceof DOMException && error.name === 'AbortError') {
            return;
          }
          observer.error(error);
        }
      })();

      return () => {
        abortController.abort();
      };
    });
  }

  private emitSseEvents(buffer: string, observer: { next: (event: ChatStreamEvent) => void }): string {
    while (true) {
      const separatorMatch = /\r?\n\r?\n/.exec(buffer);
      if (!separatorMatch || separatorMatch.index < 0) {
        return buffer;
      }

      const separatorIndex = separatorMatch.index;
      const separatorLength = separatorMatch[0].length;
      const rawEvent = buffer.slice(0, separatorIndex).trim();
      buffer = buffer.slice(separatorIndex + separatorLength);

      if (!rawEvent.startsWith('data:')) {
        continue;
      }

      const payload = rawEvent.slice(5).trim();
      if (!payload) {
        continue;
      }

      const parsed = JSON.parse(payload) as ChatStreamEvent;
      observer.next(parsed);
    }
  }
}
