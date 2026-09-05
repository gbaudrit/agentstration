# Local installation

Clone the repository, restore the solution, and build it:

```powershell
git clone https://github.com/gbaudrit/agentstration.git
cd agentstration
dotnet restore Agentstration.slnx
dotnet build Agentstration.slnx --configuration Release --no-restore
```

Run the operations Console with the offline deterministic provider:

```powershell
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
```

For the end-user Workplace, use two terminals:

```powershell
# Terminal 1
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web

# Terminal 2
dotnet run --project src/Agentstration.Workplace.Web
```

The same `Agentstration.Web` process is the authoritative server for Console and Workplace APIs. The direct server defaults to the persisted managed provider configuration, which is why the explicit environment override is used in the offline commands above.
