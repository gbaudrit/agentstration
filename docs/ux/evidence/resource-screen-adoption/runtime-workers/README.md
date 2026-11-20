# Runtime workers: application evidence

Source: `81b3095ad9caa023addccb5f3d121476fbe0017b`. These PNGs are screenshots of the running application, not mock-ups. The Release host build completed before the campaign and no referenced assemblies changed during it.

Real worker Overview and Assignments, after the maintained runtime execution setup. Long build metadata stays within mobile bounds. These captures show observed state and bounded assignment history; they do not invent namespace, mutable definition, activation or a resource-owner field.

Both desktop English/dark (1440×1000) and French touch/light (390×844) pass horizontal-bound checks before each capture. Prerequisites await persisted preferences, language and theme; the operational setup hard-reloads the shell after changing preferences.

At this commit the Release solution build has no warning or error, and all 819 Console component cases pass. The temporary storage, connected-service and operational probes (6 cases), maintained connected/storage collection regression (2 cases), and separate temporary storage-type probes (2 cases) pass. These are focused evidence, not the final required Fast/Integration and browser-smoke validation. New reusable UX journeys remain deferred under the repository authoring rules.

- [worker-overview-desktop-en.png](worker-overview-desktop-en.png)
- [worker-assignments-desktop-en.png](worker-assignments-desktop-en.png)
- [worker-overview-mobile-fr.png](worker-overview-mobile-fr.png)
- [worker-assignments-mobile-fr.png](worker-assignments-mobile-fr.png)
