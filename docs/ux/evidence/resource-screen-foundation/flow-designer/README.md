# Flow Designer — preserved specialized workshop

The canvas/palette/inspector and Graph/Definition/Split workshop remain specialized rather than receiving a fictitious Core Resource form. The Console wrapper accepts the existing namespace alias, preserves exact owner/context/version links and protects Graph/raw drafts when leaving. Accepted target replacement clears the previous private editor session before a failed load; source, graph, authorization and disposed-session checks reject late replies.

Execution permission is independent of definition writing. An authorized execute-only caller can run an unchanged saved draft without an implicit save; dirty Graph/raw input in a read-only view cannot be submitted. Saved operations retain the loaded owner, validate the returned Flow coordinates and navigate to the actual execution scope/context. Default component behavior remains compatible for callers omitting the optional execution capability.

The current coordinated checks pass: 96 Console Flow cases and 104 designer/store cases, within 397 targeted cases overall. After the final local caption/Entry preview changes, 121 affected cases were rerun successfully, and the three Release hosts were rebuilt without warnings/errors. Native English/dark desktop and French/light true-touch checks pass for this screen, with strict bounds and retained draft assertions. The maintained Flow and protected Agent journeys pass; the protected Agent source, styles and translations remain unchanged.

These PNGs are unmodified captures of the running application. Exact source, host, report and screenshot hashes are recorded in verification.json. New reusable #1236 coverage remains deferred until explicit UX validation and merge; maintained journeys continue to be checked. No CI investigation or full solution lanes were performed, as requested.

Observed limits:

- A resource creation wizard or standard persisted-resource shell would misrepresent this dedicated editor; the ordinary Flow detail screen supplies that resource frame.
- An initial temporary fragment probe resolved against base href and then placed its mobile link below the app footer. The final probe uses an absolute same-document URL and a normally positioned short link in the content toolbar, with real click/tap and unchanged path/query/draft assertions. No product fix, forced action or timeout increase was used for those probe errors.

- [flow-designer-canvas-desktop-en.png](flow-designer-canvas-desktop-en.png)
- [flow-designer-invalid-raw-draft-desktop-en.png](flow-designer-invalid-raw-draft-desktop-en.png)
- [flow-designer-retained-split-desktop-en.png](flow-designer-retained-split-desktop-en.png)
- [flow-designer-cancel-leave-desktop-en.png](flow-designer-cancel-leave-desktop-en.png)
- [flow-designer-canvas-mobile-fr.png](flow-designer-canvas-mobile-fr.png)
- [flow-designer-invalid-raw-draft-mobile-fr.png](flow-designer-invalid-raw-draft-mobile-fr.png)
- [flow-designer-retained-split-mobile-fr.png](flow-designer-retained-split-mobile-fr.png)
- [flow-designer-cancel-leave-mobile-fr.png](flow-designer-cancel-leave-mobile-fr.png)
