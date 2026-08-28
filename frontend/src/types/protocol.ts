export const PROTOCOL_VERSION = 1;

export type SourceType = "xlsx" | "google_sheet";
export type WorkflowTab = "setup" | "import" | "review" | "export" | "settings";

export interface AppConfig {
  model: string;
  baseUrl: string;
  apiStyle: "responses" | "chat";
  requestsPerMinute: number;
  useCache: boolean;
  apiKeyPresent: boolean;
  apiKeyHint: string;
  apiKeyRequired: boolean;
  rosterSourceType?: "xlsx" | "google_sheet";
  rosterXlsxPath?: string;
  rosterGoogleSheetUrl?: string;
  rosterSheetName?: string;
}

export interface Member {
  id: number;
  name: string;
  rank: string;
  joinedOn: string | null;
  totalHeroPower: number | null;
}

export interface Alternative {
  memberId: number;
  name: string;
  score: number;
}

export interface Observation {
  id: string;
  sourceFile: string;
  day: string;
  rank: number;
  rawPlayerId: number | null;
  rawName: string;
  points: number;
  extractionConfidence: number;
  isPinnedRow: boolean;
  matchedMemberId: number | null;
  matchedMemberName: string | null;
  matchMethod: string;
  matchConfidence: number;
  issue: string | null;
  alternatives: Alternative[];
}

export interface ExtractionResult {
  path: string;
  error: string | null;
  extraction: Record<string, unknown> | null;
}

export interface AppSummary {
  memberCount: number;
  screenshotCount: number;
  observationCount: number;
  unmatchedCount: number;
  failedFileCount: number;
  avatarMemberCount: number;
  avatarSampleCount: number;
}

export interface AppState {
  config: AppConfig;
  members: Member[];
  memberSource: string;
  memberWarnings: string[];
  screenshots: string[];
  extractions: ExtractionResult[];
  observations: Observation[];
  issues: string[];
  summary: AppSummary;
}

export interface RequestEnvelope {
  id: string;
  command: string;
  payload?: Record<string, unknown>;
}

export interface ResponseEnvelope<T = unknown> {
  id: string | null;
  type: "response";
  payload: T;
}

export interface ErrorEnvelope {
  id: string | null;
  type: "error";
  error: { code: string; message: string };
}

export interface EventEnvelope<T = unknown> {
  type: "event";
  event: string;
  operationId?: string;
  payload: T;
}

export type BackendMessage = ResponseEnvelope | ErrorEnvelope | EventEnvelope;
