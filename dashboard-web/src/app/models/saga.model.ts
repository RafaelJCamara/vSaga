export type SagaKind = 'Orchestrated' | 'Choreographed';

export type SagaStatus =
  | 'Running'
  | 'Completed'
  | 'Failed'
  | 'Compensating'
  | 'Compensated'
  | 'TimedOut'
  | 'Cancelled';

export type SagaEntryType =
  | 'SagaStarted'
  | 'StateEntered'
  | 'MessageReceived'
  | 'UnexpectedEvent'
  | 'StepStarted'
  | 'StepSucceeded'
  | 'StepFailed'
  | 'CompensationStarted'
  | 'CompensationStepSucceeded'
  | 'CompensationStepFailed'
  | 'TimeoutScheduled'
  | 'TimeoutFired'
  | 'TimeoutCancelled'
  | 'ManualRetryRequested'
  | 'SagaCompleted'
  | 'SagaCancelled'
  | 'DeliveryExhausted'
  | 'MessagePublished'
  | 'MessageSent'
  | 'ChildSagaStarted'
  | 'ChildSagaFinished'
  /**
   * The saga's state as committed by the step it follows, in `payloadJson`. A snapshot, never a
   * row: the timeline folds it into its step (util/saga-transitions.ts).
   */
  | 'StatePersisted';

export interface SagaSummary {
  correlationId: string;
  sagaType: string;
  kind: SagaKind;
  currentState: string;
  status: SagaStatus;
  createdAtUtc: string;
  updatedAtUtc: string;
  version: number;
  /** The saga that started this one via StartChildAsync; null for a root saga. */
  parentSagaType: string | null;
  parentCorrelationId: string | null;
}

export interface SagaDetail {
  summary: SagaSummary;
  dataJson: string | null;
}

export interface SagaLogEntry {
  sequenceNumber: number;
  correlationId: string;
  sagaType: string;
  entryType: SagaEntryType;
  fromState: string | null;
  toState: string | null;
  messageType: string | null;
  messageId: string | null;
  payloadJson: string | null;
  errorMessage: string | null;
  traceId: string | null;
  spanId: string | null;
  occurredAtUtc: string;
  /**
   * Who sent an inbound message, or the saga for an outbound one. Optional because older specs and
   * fixtures leave them out; the API always sends them, null when unknown.
   */
  sourceService?: string | null;
  /** Where an outbound message was addressed (MessageSent, an HTTP call's host). */
  destinationService?: string | null;
  /**
   * The id of the message that caused this entry: an outbound entry's inbound message, an HTTP
   * reply's request.
   */
  causationId?: string | null;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface SagaTypeInfo {
  sagaType: string;
  kind: SagaKind;
}

export type SagaSortColumn = 'UpdatedAt' | 'Status';

export interface SagaListFilter {
  status?: SagaStatus;
  sagaType?: string;
  kind?: SagaKind;
  search?: string;
  page?: number;
  pageSize?: number;
  sortBy?: SagaSortColumn;
  sortDescending?: boolean;
}

export type SagaMapNodeKind = 'Initiator' | 'Orchestrator' | 'Participant' | 'Unresolved';

export type SagaMapNodeStatus = 'ok' | 'failed' | 'unanswered';

export interface SagaMapNode {
  id: string;
  displayName: string;
  kind: SagaMapNodeKind;
  status: SagaMapNodeStatus;
  messagesIn: number;
  messagesOut: number;
}

export interface SagaMapEdge {
  id: string;
  fromNodeId: string;
  toNodeId: string;
  messageType: string;
  messageId: string | null;
  isCompensation: boolean;
  failed: boolean;
  unanswered: boolean;
  occurredAtUtc: string;
}

export interface SagaMapEvent {
  sequenceNumber: number;
  edgeId: string | null;
  nodeId: string | null;
  entryType: SagaEntryType;
  messageType: string | null;
  errorMessage: string | null;
  occurredAtUtc: string;
}

export interface SagaMap {
  summary: SagaSummary;
  nodes: SagaMapNode[];
  edges: SagaMapEdge[];
  events: SagaMapEvent[];
  failureEventIndex: number | null;
}
