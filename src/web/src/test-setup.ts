import 'fake-indexeddb/auto';
import { IDBFactory } from 'fake-indexeddb';

// Every test starts with an empty device store, so an outbox left by one test never leaks into the next.
beforeEach(() => {
  globalThis.indexedDB = new IDBFactory();
});
