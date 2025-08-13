# zure-azdo-scanner

A .NET Global Tool for scanning and analyzing Azure DevOps organizations to assist with governance and security compliance. It helps identify potential security risks, analyze project configurations, and ensure best practices are followed.

## Installation

Prerequisites:
- Azure CLI installed & logged in (`az login`)
- Azure DevOps CLI extension installed (`az extension add --name azure-devops`)
- .NET SDK 9 (or runtime for installing/running the tool)
- Run `az devops configure --defaults organization=https://dev.azure.com/<yourorg>` (or pass `--org` each command)

Install (first time):
```bash
dotnet tool install --global zure-azdo-scanner
```

Update (later):
```bash
dotnet tool update --global zure-azdo-scanner
```

Verify:
```bash
zure-azdo-scanner --help
```

## Commands

### list-projects
List Azure DevOps projects with optional repository (branch policy status) and service connection info.

Examples:
```bash
zure-azdo-scanner list-projects
zure-azdo-scanner list-projects --org https://dev.azure.com/myorg
zure-azdo-scanner list-projects --projects "ProjA,ProjB" --include-repos --include-serviceconnections
```
Options:
- `--org <ORG>` Azure DevOps organization URL
- `--projects <NAMES>` Comma-separated project names
- `--include-repos` Include repositories and branch protection policy evaluation
- `--include-serviceconnections` Include service connections

### list-extensions
List installed extensions with their permissions.

```bash
zure-azdo-scanner list-extensions
zure-azdo-scanner list-extensions --org https://dev.azure.com/myorg
```
Options:
- `--org <ORG>` Azure DevOps organization URL

## Exit Codes
- `0` Success
- Non-zero: execution / prerequisite failure

## Troubleshooting
- Ensure `az` and `az devops` commands work standalone.
- Set default org: `az devops configure --defaults organization=https://dev.azure.com/<org>`
- Use PAT via environment if needed: `AZDO_PERSONAL_ACCESS_TOKEN` (ensure proper scopes)

## Contributing
Issues and PRs are welcome. Please retain NOTICE and license headers in derivative work.

## License
Apache License 2.0. See LICENSE and NOTICE files packaged with this tool.
