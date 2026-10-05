# ADR 0004 – Offline field capture and sync

Date: 2026-10-05 · Status: accepted

## Decision

A. The field screen is offline-first. Every change goes to a queue on the device (the outbox) and shows at once. The queue is sent whenever the server can be reached, online or not when the change was made. There is one code path, not an online path and an offline one.
B. Each signed-in user has their own IndexedDB store (`reticula-field-<user id>`). It holds the last server copy of each opened project (the snapshot) and the queue. A change is therefore always sent as the person who made it. Signing out keeps the store, and the queue syncs at the next sign-in.
C. The screen shows the snapshot with the pending changes laid over it. Server results replace snapshot entries; the snapshot is never edited by hand.
D. Changes to one item (a building, its load, a candidate) are sent in the order they were made. A change made on top of an unsynced one is sent with the version the server returned for the earlier change, never with a version fetched later. Otherwise a refresh could pick up someone else's change and the queued change would overwrite it unseen.
E. A 409 holds the change and shows the server's state. The person sees both side by side and chooses Keep mine (sent again over the server's version) or Keep theirs (dropped). Nothing is overwritten automatically. Later changes to a held item wait. If the change they were made on is dropped, they are held too.
F. A refusal (400, 404, 413) keeps the change with the server's reason, to retry or discard. Discarding a refused new building or candidate also drops the queued changes to it. A network failure, a 401, a 408, a 429 or a 5xx leaves the queue as it is, and it is tried again with backoff (5 s up to 5 min) and whenever the connection returns.
G. Every request is safe to send twice. Building inspections, new buildings and photos already had device ids. Candidate and load saves now carry an `opId`, which the server records as the inspection id, and removing a removed candidate is not an error. A lost response, or two open tabs, cannot apply a change twice.
H. A load save made without the current load version is refused with 409 when the building already has a load. Before this, a save with no version silently replaced a load the inspector had not seen.
I. The calc service stays the only place engineering numbers are made (plan decision B). A load saved offline is recorded with its observations, class choice and override. Its kVA shows as "worked out when it syncs". The cached rules data is the form only: options, classes and special-load defaults.
J. Photos are queued as bytes (an `ArrayBuffer` with its content type), not as `Blob`s, because not every mobile browser stores blobs in IndexedDB reliably.

## Consequences

- A project works offline only after its field screen has been opened once online on that tablet. The offline screen lists the projects on the tablet.
- Progress counts are worked out on the device from the same rules as the server, so they stay right offline. The open-assumptions count is the server's, fetched after each sync.
- Re-importing buildings while changes are queued can leave those changes pointing at buildings that no longer exist. They come back as refusals to discard.
- The basemap needs a connection until offline map tiles land (plan item 1.9). Stands, buildings and candidates draw without it.
- Without IndexedDB (rare; some private modes) the queue lives in memory, and the sync panel warns that changes are lost on reload.
