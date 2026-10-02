import { entryTypeLabel } from './entry-type-label';

describe('entryTypeLabel', () => {
  // The engine logs SagaCompleted for any terminal Finalize, failed sagas included.
  it('reads SagaCompleted as SagaFinalized', () => {
    expect(entryTypeLabel('SagaCompleted')).toBe('SagaFinalized');
  });

  it('leaves every other entry type unchanged', () => {
    for (const type of [
      'SagaStarted',
      'MessageReceived',
      'StepSucceeded',
      'StepFailed',
      'StatePersisted',
      'Unknown',
    ]) {
      expect(entryTypeLabel(type)).toBe(type);
    }
  });
});
