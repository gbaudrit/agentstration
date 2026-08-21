# Who Am I? sample Pack

This is a distribution smoke-test Pack, not yet a complete game implementation.

It installs three role-specific Agent resources, one active Direct Flow targeting the Judge, and one published conversational Entry. Installation asks for both the conversational Model Profile and the Runtime Profile used to prepare the Agents locally.

## What this validates

- archive preview and five-resource validation;
- dependency-safe Agent, Flow, and Entry installation;
- installed-Pack inventory and provenance;
- namespaced Model Profile and Runtime Profile binding resolution;
- conflict detection and modification-safe uninstall;
- a representative conversational product skeleton.

## Current execution limits

- Pack V1 does not add the Entry to a Workplace Workspace;
- the Direct Flow invokes only the Judge Agent;
- turn loops, private participant context, durable game state, scoring, and generic human-input suspension/resume are not implemented.

To expose the Entry after installation, add `who-am-i` to a Workplace Workspace. Starting the Flow prepares the Judge with the Runtime Profile selected during installation.

## Create the local archive

From the repository root:

```powershell
Compress-Archive -Path samples/packs/who-am-i/* -DestinationPath who-am-i.pack.zip
```

Open **Configure → Packs**, select `who-am-i.pack.zip`, inspect the preview, and confirm installation.
