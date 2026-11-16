import { useCallback, useEffect, useRef, useState } from 'react';

// ---- types: mirror of the engine's records (camelCase JSON) ----------------------------------------------------

/** What a treasure was. kind: a hunt kind ('jam', 'pirate-shop'...), 'gizmo' (item says which), 'golden' (unknown prize), 'unknown'. */
export interface TreasureTag { kind: string; item?: string; name?: string; prizeKey?: string; golden: boolean; team: boolean }
export interface MatchSummary {
  matchId: string; when: string; startUtc?: string; playlist?: string; durationSeconds?: number; placement?: number;
  totalPlayers?: number; eliminations?: number; chests: number; chestsDestroyed: number; ammoBoxes: number;
  otherContainers: number; teamObserved: number; counted: boolean; notCountedReason?: string; possiblyIncomplete: boolean;
  identityReliable: boolean; parserVersion: number; playerName?: string; replayFile?: string; sessionId?: number;
  treasures?: TreasureTag[];
}
export interface SessionTotals { id?: number; startedUtc: string; chests: number; matches: number }
export interface Totals {
  chests: number; matches: number; average: number; chestsDestroyed: number; ammoBoxes: number; otherContainers: number;
  possiblyIncompleteMatches: number; best?: MatchSummary; lastMatch?: MatchSummary; session: SessionTotals;
  manualChests: number; treasures: number;
}

// ---- treasure hunt ----
export interface ManualEntry { id: string; chests: number; atUtc: string; weekFromUtc?: string }
export interface HuntHour { start: string; end: string; boosts: Record<string, number>; confirmed: boolean }
export interface HuntOdds { kind: string; oneIn: number; weekly: boolean }
export interface ChanceView { kind: string; oneIn: number; weekly: boolean; p: number; logNever: number; expected: number; chests: number; atPace?: number }
export interface HuntPrizeView { id: string; kind: string; name: string; image: string; obtained?: 'auto' | 'manual' }
export interface HuntWeekView { number: number; from: string; to: string; title: string; prizes: HuntPrizeView[] }
export interface TreasureView {
  matchId: string; matchWhen: string; tag: TreasureTag; timeSeconds?: number; timeExact: boolean; sourceKind?: string;
  sourceDestroyed: boolean; sourceIsChest: boolean; sourceCertain: boolean; teammate?: string;
}
export interface SourceCount { kind: string; destroyed: boolean; isChest: boolean; count: number }
export interface HuntReport {
  revision: number; oddsSource: string; hoursSource: string; nowUtc: string; huntStart: string; huntEnd: string; over: boolean;
  hours: HuntHour[]; odds: HuntOdds[]; week?: HuntWeekView; weekChests: number; weekManual: number; seasonChests: number; manual: number;
  boostedChests: number; pirateHalfway: number; weekChances: ChanceView[]; seasonChances: ChanceView[]; gizmosFound: number;
  chestsSinceGizmo?: number; pirateShop?: HuntPrizeView; weeks: HuntWeekView[]; collectionGot: number; collectionTotal: number;
  finds: TreasureView[]; treasures: number; teamTreasures: number; sources: SourceCount[]; byKind: Record<string, number>;
  destroyed: number; matches: number; chests: number; perMatch: number; manualEntries: ManualEntry[];
}
export interface ActivityItem { id: number; atUtc: string; kind: string; data: Record<string, any> }
export interface OverlaySettings {
  enabled: boolean; locked: boolean; opacity: number; scale: number; x?: number; y?: number; showPersonal: boolean;
  showSquad: boolean; showSession: boolean; showLastMatch: boolean; showMembers: boolean; squadScope: 'combined' | 'shared';
  toggleHotkey: string; lockHotkey: string;
  theme: 'aurora' | 'rift'; layout: 'row' | 'column' | 'compact'; align?: 'left' | 'center' | 'right'; items: OverlayItem[]; showIcons: boolean; showStatus: boolean;
  showResult: boolean; resultSeconds: number;
}
export type OverlayItem = 'personal' | 'squad' | 'squadShared' | 'session' | 'squadSession' | 'last' | 'treasures' | 'teamTreasures';
export const OVERLAY_ITEMS: OverlayItem[] = ['personal', 'squad', 'squadShared', 'session', 'squadSession', 'last', 'treasures', 'teamTreasures'];
export interface AppSettings {
  demosFolder: string; demosFolderIsDefault: boolean; profile?: string; language: 'en' | 'fr'; timeFormat: 'auto' | '12' | '24';
  onboardingDone: boolean; minimizeToTray: boolean; startMinimized: boolean; launchWithWindows: boolean; launchWithFortnite: boolean;
  overlay: OverlaySettings;
}
export interface PendingFile { fileName: string; reason: 'recording' | 'settling' }
export interface FileOutcome { fileName: string; status: string; match?: { matchId: string; chests: number }; replaced: boolean; detail?: string }
export interface TrackingStatus {
  state: 'idle' | 'scanning' | 'paused'; folderFound: boolean; replayFiles: number; scanIndex: number; scanTotal: number;
  currentFile?: string; pending: PendingFile[]; lastOutcome?: FileOutcome; lastOutcomeUtc?: string; lastScanUtc?: string;
}
export interface ImportStatus { files: number; counted: number; duplicates: number; notCounted: number; incomplete: number; errors: number; waiting: number }
export interface Profile { key: string; name: string; matches: number; chests: number; lastPlayedUtc: string }
export interface SyncStatus {
  state: 'disabled' | 'connecting' | 'online' | 'offline'; server?: string; userId?: string; displayName?: string; squadId?: string;
  lastError?: string; lastSyncUtc?: string; pending: number; refused: number;
}
export interface Account { playerId: string; playerName: string }
export interface Member {
  userId: string; displayName: string; role: string; online: boolean; joinedUtc: string; lastSyncUtc?: string; lastSeenUtc?: string;
  accounts: Account[]; lifetimeChests: number; lifetimeMatches: number; sharedChests: number;
  lastMatch?: { matchId: string; startUtc?: string; chests: number };
  bestMatch?: { matchId: string; startUtc?: string; chests: number };
  /** Absent from servers older than treasure sync. */
  lifetimeTreasures?: number; sharedTreasures?: number; treasuresUnknownMatches?: number;
}
export interface SharedContribution { userId: string; playerId: string; playerName: string; chests: number; chestsDestroyed: number; ammoBoxes: number; parserVersion: number; receivedUtc: string; possiblyIncomplete: boolean; treasures?: number }
export interface SquadSession {
  startUtc: string; endUtc: string; active: boolean; chests: number; matches: number; treasures: number; sharedMatches: number; sharedChests: number;
  members: { userId: string; chests: number; matches: number; treasures: number }[];
}
/** A squad member's treasure, named by the engine. */
export interface SquadFind { userId: string; matchId: string; matchStartUtc?: string; tag: TreasureTag; shared: boolean }
export interface MissingContribution { userId: string; playerId: string; name: string; observedEstimate?: number }
export interface SharedMatch {
  matchId: string; startUtc?: string; playlist?: string; durationSeconds?: number; status: 'complete' | 'partial' | 'ambiguous';
  confirmedChests: number; contributions: SharedContribution[]; missing: MissingContribution[]; warnings: string[];
}
export interface Invite { code: string; link: string; createdUtc: string; expiresUtc: string; maxUses: number; uses: number }
export interface Squad {
  squadId: string; name: string; revision: number; serverTimeUtc: string; maxMembers: number; members: Member[];
  combinedMemberChests: number; sharedChests: number; sharedMatchCount: number; partialMatchCount: number; ambiguousMatchCount: number;
  sharedMatches: SharedMatch[]; invites: Invite[];
  combinedTreasures?: number; sharedTreasures?: number; session?: SquadSession;
}
export interface AppState {
  version: string; parserVersion: number; dataDir: string; settings: AppSettings; tracking: TrackingStatus; import: ImportStatus;
  recording: { state: 'likely_on' | 'likely_off' | 'no_folder' | 'no_replays' | 'stale' | 'unknown'; detail?: string };
  profile?: Profile; profiles: Profile[]; totals: Totals; recent: MatchSummary[]; sync: SyncStatus; squad?: Squad;
  squadFinds?: SquadFind[];
}
export interface TimelinePoint { date: string; chests: number; matches: number; ammoBoxes: number }
export interface MatchDetail {
  match: MatchSummary; map?: string; gameVersion?: string; playerId?: string; firstAnalyzedUtc: string; analyzedUtc: string;
  detail?: {
    byKind: Record<string, number>; chests: { timeSeconds: number; kind: string; destroyed: boolean }[];
    roster: { playerId?: string; name: string; isBot: boolean; observedChests: number }[];
    treasures: { prizeId?: string; rawItem: string; timeSeconds?: number; sourceKind: string; fromGoldenChest: boolean; sourceDestroyed?: boolean }[]; teamTreasures: number;
  };
}
export interface Diagnostics {
  fileCounts: Record<string, number>;
  problems: { fileName: string; status: string; detail?: string; processedUtc: string }[];
  notCounted: MatchSummary[];
  log: { id: number; atUtc: string; level: string; source: string; message: string }[];
  refusedUploads: { matchId: string; playerId: string; lastError?: string }[];
  databasePath: string; databaseBytes: number;
}

// ---- transport ---------------------------------------------------------------------------------------------------

declare global { interface Window { __CC_TOKEN?: string } }

function readToken(): string {
  if (window.__CC_TOKEN) return window.__CC_TOKEN;
  const fromUrl = new URLSearchParams(location.search).get('token');
  if (fromUrl) {
    try { sessionStorage.setItem('cc-token', fromUrl); } catch { /* private mode */ }
    return fromUrl;
  }
  try { return sessionStorage.getItem('cc-token') ?? ''; } catch { return ''; }
}
const token = readToken();

export class ApiError extends Error {
  constructor(public code: string, message: string) { super(message); }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    headers: { 'X-CC-Token': token, ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (!response.ok) {
    let payload: { error?: string; message?: string } = {};
    try { payload = await response.json(); } catch { /* not JSON */ }
    throw new ApiError(payload.error ?? String(response.status), payload.message ?? `Request failed (${response.status})`);
  }
  return response.status === 204 ? (undefined as T) : response.json();
}

export const api = {
  get: <T,>(path: string) => request<T>('GET', path),
  post: <T = unknown,>(path: string, body: unknown = {}) => request<T>('POST', path, body),
};

// One event stream for the whole page; components subscribe to it.
const subscribers = new Set<(topic: string) => void>();
let stream: EventSource | null = null;
function ensureStream() {
  if (stream) return;
  stream = new EventSource(`/api/events?token=${encodeURIComponent(token)}`);
  stream.onmessage = (e) => subscribers.forEach((s) => s(e.data));
  stream.onerror = () => {
    stream?.close();
    stream = null;
    setTimeout(() => { if (subscribers.size) { ensureStream(); subscribers.forEach((s) => s('reconnect')); } }, 2000);
  };
}

export function useEvents(handler: (topic: string) => void) {
  const ref = useRef(handler);
  ref.current = handler;
  useEffect(() => {
    const sub = (topic: string) => ref.current(topic);
    subscribers.add(sub);
    ensureStream();
    return () => { subscribers.delete(sub); };
  }, []);
}

/** Loads a resource and reloads it when the app announces a change on one of the topics. */
export function useLive<T>(path: string | null, topics: string[] = ['matches']) {
  const [data, setData] = useState<T | undefined>();
  const [error, setError] = useState<string | undefined>();
  const timer = useRef<number>();
  const load = useCallback(() => {
    if (!path) return;
    api.get<T>(path).then((d) => { setData(d); setError(undefined); }).catch((e) => setError(e.message));
  }, [path]);
  useEffect(() => { load(); }, [load]);
  useEvents((topic) => {
    if (topic === 'reconnect' || topics.includes(topic)) {
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(load, 120); // an import announces every file: coalesce
    }
  });
  return { data, error, reload: load };
}
