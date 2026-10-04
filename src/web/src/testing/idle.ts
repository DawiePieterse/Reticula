/** Lets IndexedDB and other macrotask work (the offline outbox) finish before the test inspects requests. */
export async function idle(ticks = 25): Promise<void> {
  for (let i = 0; i < ticks; i++) await new Promise((r) => setTimeout(r, 0));
}
