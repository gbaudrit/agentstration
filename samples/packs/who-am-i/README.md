# Who Am I? sample Pack

This is a distribution smoke-test Pack, not yet a complete game implementation.

It installs three role-specific Agent resources, one active Direct Flow targeting the Judge, and one published conversational Entry. The resources intentionally use unique names and depend on the standalone seed Model Profile `reasoning-default`.

## What this validates

- archive preview and five-resource validation;
- dependency-safe Agent, Flow, and Entry installation;
- installed-Pack inventory and provenance;
- conflict detection and modification-safe uninstall;
- a representative conversational product skeleton.

## Current execution limits

- Pack installation does not create Agent revisions or Runtime deployments;
- Pack V1 does not add the Entry to a Workplace Workspace;
- the Direct Flow invokes only the Judge Agent;
- turn loops, private participant context, durable game state, scoring, and generic human-input suspension/resume are not implemented.

To expose the Entry after installation, add `who-am-i` to a Workplace Workspace. To execute the Flow, create and reconcile a deployment for `who-am-i-judge` with an available Runtime Profile and model provider.

## Create the local archive

From the repository root:

```powershell
Compress-Archive -Path samples/packs/who-am-i/* -DestinationPath who-am-i.pack.zip
```

Open **Configure → Packs**, select `who-am-i.pack.zip`, inspect the preview, and confirm installation.
