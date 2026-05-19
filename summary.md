Created 0 todos

# LogRAG POC — Detailed Technical Explanation (Markdown)

## 1. What this project does

This system is a **local, end-to-end RAG pipeline for logs**:

1. Reads raw logs from configured sources  
2. Parses and normalizes them into a shared schema  
3. Chunks log content for retrieval  
4. Creates embeddings with Ollama (`nomic-embed-text`)  
5. Stores vectors + metadata in Qdrant  
6. Answers natural-language questions using retrieved log context + Ollama LLM (`llama3.2`)  
7. Streams answers to clients (API + Angular UI)

You now confirmed ingestion works with:

```json
{"rawLogsRead":4,"chunksCreated":4,"vectorsUpserted":4,...}
```

---

## 2. Runtime components and responsibilities

| Component | Port | Responsibility |
|---|---:|---|
| ASP.NET Core API (`LogRag.Api`) | 5000 | Ingest + chat orchestration |
| Ollama | 11434 | Embeddings + generation |
| Qdrant | 6333 | Vector storage and filtered ANN retrieval |
| Angular UI (`lograg-ui`) | 4200 | Demo chat and ingestion controls |

---

## 3. Backend architecture by layer (mapped to your implementation)

## Layer 1 — Log source adapter

**File:** `LogRag.Api\Sources\LogSources.cs`

- Uses `ILogSource` and `ILogSourceRegistry`.
- Current implementation: `FileLogSource` (reads line-by-line from configured file).
- Extension model: add new adapter class (e.g., Kafka, RabbitMQ, Syslog stream) implementing `ILogSource`; registry wiring pattern remains unchanged.

**Why this matters:** source onboarding is isolated from the rest of the pipeline.

---

## Layer 2 — Ingestion pipeline

**File:** `LogRag.Api\Ingestion\Pipeline.cs`

Pipeline stages:

1. **Parser (`GenericLogParser`)**
   - Tries multiple patterns:
     - JSON logs
     - CSV-like logs
     - Regex rules from config (`Parser.RegexRules`)
     - key=value style fallback
   - Produces `ParsedLogEntry`.

2. **Normalizer (`LogNormalizer`)**
   - Creates canonical envelope:
     - `timestampUtc`, `severity`, `serviceName`, `traceId`, `message`
   - Preserves extra fields in payload.
   - Computes stable `logHash` (SHA-based) to support dedup semantics.

3. **Chunker (`SlidingWindowLogChunker`)**
   - Splits normalized log text into chunks using token-window logic.
   - Uses overlap (`ChunkSizeTokens=512`, `OverlapTokens=64`) to preserve context continuity.
   - Produces `LogChunk` entries with full metadata retained.

4. **Orchestrator (`IngestionOrchestrator`)**
   - Coordinates full run:
     - Ensure Qdrant collection
     - Read/parse/normalize/chunk
     - Embed all chunks
     - Upsert vectors to Qdrant
     - Apply retention delete

5. **Scheduler (`IngestionHostedService`)**
   - Background loop support for periodic ingestion.
   - Current config:
     - `EnableDailyBatch=true`
     - `RunBatchOnStartup=false`
     - `EnableStreaming=false`

---

## Layer 3 — Embeddings via Ollama

**File:** `LogRag.Api\Embedding\OllamaEmbeddingService.cs`

- Uses `OllamaSharp`.
- Calls `/api/embeddings` via client with model `nomic-embed-text`.
- Batch + parallel flow:
  - Batches texts by configured `BatchSize`
  - Limits concurrent embedding calls by `MaxParallelBatches`
- Maintains output order to match chunk-to-vector mapping correctly.

**Current config:**
- `Embedding.BaseUrl = http://localhost:11434`
- `Embedding.Model = nomic-embed-text`

---

## Layer 4 — Vector store in Qdrant

**File:** `LogRag.Api\VectorStore\QdrantVectorStore.cs`

Responsibilities:

1. **Ensure collection**
   - Creates collection if missing:
     - size = 768
     - distance = cosine

2. **Upsert points**
   - Each point contains:
     - `id` (deterministic UUID generated from chunk id)
     - `vector`
     - payload metadata:
       - timestamp, severity, service_name, trace_id, source_id, source_type, message, log_hash, extra fields

3. **Filtered ANN search**
   - Supports payload filters (`service_name`, `severity`, `source_type`, time range) combined with vector search.

4. **Retention cleanup**
   - Deletes points older than configured cutoff (`RetentionDays=30`) by timestamp filter.

---

## Layer 5 — Retrieval and context build

**Files:**  
- `LogRag.Api\Query\RagQuery.cs` (query engine)  
- `LogRag.Api\Query\ContextBuilder` / `MarkdownResponseShaper`

Flow:

1. Embed user question in same vector space.
2. Search Qdrant with ANN + optional metadata filters.
3. Deduplicate and rank retrieved chunks.
4. Build a structured prompt context containing cited chunks and metadata.
5. Hand context to LLM layer.

---

## Layer 6 — LLM answer generation (Ollama)

**File:** `LogRag.Api\Llm\OllamaLlmClient.cs`  
**Config:** `Llm.Model = llama3.2`

- Uses Ollama generate API with your system prompt.
- System prompt constrains behavior:
  - answer from provided context
  - cite log evidence
  - say when no match is found
- Supports token streaming for real-time UX.

---

## Layer 7 — API + chat interface

**File:** `LogRag.Api\Program.cs`

Endpoints:

- `GET /health` — service health
- `POST /ingest` — trigger full ingestion run
- `POST /chat` — streaming response endpoint (`text/event-stream`)

Conversation/session:

- `InMemorySessionManager` stores turn history for multi-turn context.
- `ChatService` orchestrates retrieve → prompt → stream → shape response.

---

## 4. Frontend integration (Angular POC)

**Key file:** `lograg-ui\src\app\services\ragapi.service.ts`

Important implementation detail:

- Chat is sent as **POST** to `/api/chat` and streamed back as SSE-style frames.
- The service uses `fetch` + stream reader and manually parses `data: ...\n\n` events.
- This matches backend behavior exactly (`POST /chat`, streaming body).

UI behavior:

- Health check (`/api/health`)
- Ingest button (`/api/ingest`)
- Chat panel with streaming tokens and citations
- Optional filters included in chat request body

---

## 5. End-to-end execution flow

## A) Ingestion flow (`POST /ingest`)

1. API receives ingest request.
2. Orchestrator ensures Qdrant collection exists.
3. Sources stream raw log lines.
4. Parser detects shape and extracts fields.
5. Normalizer builds canonical envelope + payload.
6. Chunker emits chunk records.
7. Embedder requests vectors from Ollama.
8. Vector store upserts all points to Qdrant.
9. Retention cleanup removes old points.
10. API returns summary JSON (`rawLogsRead/chunksCreated/vectorsUpserted`).

## B) Chat flow (`POST /chat`)

1. API receives question + optional filters.
2. Question embedding generated (Ollama embedding model).
3. Qdrant ANN search runs with optional filter clause.
4. Top chunks are ranked/deduped.
5. Context builder assembles prompt with citations.
6. LLM generation starts (`llama3.2`).
7. API streams token events (`data: {...}\n\n`).
8. Angular client incrementally renders response.

---

## 6. Current configuration state (important)

From `appsettings.json`:

- Embeddings: `nomic-embed-text`
- LLM: `llama3.2`
- Qdrant: `http://localhost:6333`
- Retention: `30` days
- Scheduler startup ingest: disabled (`RunBatchOnStartup=false`)  
  (manual `/ingest` remains available and working)

---

## 7. Why your earlier 500 happened and why it works now

You previously saw generic 500s with empty body.  
Key improvements now:

1. `/ingest` route returns explicit JSON error details (`error/detail/inner`) on exceptions.
2. Ingest stages throw contextual errors (e.g., “Failed while upserting vectors to Qdrant”).
3. Qdrant point IDs were adjusted to deterministic UUID format, improving compatibility across Qdrant versions.

Result: ingestion now succeeds and is observable.

---

## 8. Operational validation checklist (practical)

Use these checks in order:

1. Ollama models available:
   - `nomic-embed-text`
   - `llama3.2`

2. Qdrant reachable:
```powershell
Invoke-RestMethod http://localhost:6333/collections
```

3. API health:
```powershell
Invoke-RestMethod http://localhost:5000/health
```

4. Ingest test:
```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:5000/ingest
```

5. Chat stream test:
```powershell
curl.exe -N -X POST http://localhost:5000/chat -H "Content-Type: application/json" -d "{\"question\":\"What payment errors happened?\",\"top_k\":5}"
```

6. UI test:
- Open `http://localhost:4200`
- Click **Ingest Now**
- Ask a question
- Confirm streamed response + citations

---

## 9. Extensibility points

If you evolve this POC to production, the clean extension points are:

- **Source adapters:** implement new `ILogSource`
- **Parser rules:** add regex/schema config without code changes
- **Storage:** swap or augment vector backend behind `IVectorStore`
- **Session persistence:** replace in-memory session manager with Redis-backed implementation
- **Security:** add auth, role-based source filters, and PII redaction pre-embedding
- **Observability:** add structured tracing/metrics around ingest stages and chat latency

---

## 10. Summary of system behavior in one sentence

This project is a fully local semantic log assistant: it ingests heterogeneous logs, converts them into filterable vectors in Qdrant via Ollama embeddings, retrieves relevant evidence for natural-language questions, and streams grounded LLM answers through a .NET API to an Angular chat UI.