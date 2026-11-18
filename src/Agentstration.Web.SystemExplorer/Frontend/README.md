# Optional System Explorer renderer

This adapter alone owns LikeC4 Model Builder -> Graphviz WASM layout -> React.
It accepts authorized neutral pages and reports only selected canonical IDs.
It cannot navigate, edit resource dependencies or persist layouts. The neutral
Console table, inspection, logical Systems and JSON export do not need the island.

Rebuild the committed local module with Node 24 and the locked dependency graph:

```sh
npm ci
npm test
npm run build
node notices.mjs
```

`npm run build` checks TypeScript before creating `../wwwroot/system-explorer.js`.
A production React build is explicit because Vite library mode leaves NODE_ENV
to consumers by default. Shadow-root styles are embedded by ReactLikeC4; no global
library CSS or external fonts are injected. Runtime needs neither npm nor internet.

The current module is about 4.4 MB / 1.50 MB gzip including WASM. The Console
requests 80 nodes; the adapter rejects more than 100. Graphviz has a 20-second
layout deadline. Errors leave the neutral list/export available. Resource labels
are React text, not model expressions. IDs use collision-free UTF-8 hex mapping;
ordinal sorting preserves determinism for canonically equivalent Unicode text.

Dependencies and notices are recorded in package-lock.json and
THIRD_PARTY_NOTICES.txt. The generated module retains upstream license comments.
The adapter tests cover mapping, hostile names, layout, semantic immutability,
unknown endpoints and size/duplicate rejection. Real browser smoke additionally
checks selection, text escaping, no external requests and disposal.

Progressive arrivals reuse the current React/Graphviz mount. One active layout and
one latest pending request bound work; intermediate pending requests are coalesced.
The previously rendered graph remains until another layout commits. Disposal makes
old layouts unable to commit. Console windows stay at 80 nodes, so a large received
catalogue never bypasses the adapter's 100-node guard. Unchanged windows and
heartbeat-only frames do not invoke layout. Stream consistency belongs to the
application and client observation buffer, never to this renderer.
