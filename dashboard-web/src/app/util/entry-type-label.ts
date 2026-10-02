/**
 * The entry type as the dashboard should read it. The engine logs one terminal entry for every
 * Finalize, so a saga that failed still ends on SagaCompleted — accurate about the lifecycle, but
 * on screen directly under a red "Failed" badge it reads as a contradiction. "SagaFinalized" says
 * the same thing without arguing with the status. Presentation only: the persisted SagaEntryType
 * member is unchanged, and its `toState` already carries the outcome the entry ended on.
 */
export function entryTypeLabel(entryType: string): string {
  return entryType === 'SagaCompleted' ? 'SagaFinalized' : entryType;
}
