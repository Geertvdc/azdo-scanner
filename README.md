# Azure DevOps Scanner

A command-line tool for scanning and analyzing Azure DevOps organizations to assist with governance and security compliance. This tool helps identify potential security risks, analyze project configurations, and ensure best practices are being followed across your Azure DevOps organization.

## Purpose

The Azure DevOps Scanner provides visibility into critical aspects of your Azure DevOps organization:

- List all projects with details about their administrators
- Identify repositories with insufficient branch protection policies
- Discover service connections with potential security risks
- Analyze installed extensions and their permissions

By running regular scans, you can maintain better control over your Azure DevOps environment and ensure it complies with your organization's governance standards.

## Prerequisites

To use this tool, you need:

- [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli) installed and configured
- [Azure DevOps CLI extension](https://docs.microsoft.com/en-us/azure/devops/cli/) installed
- [.NET SDK 9](https://dotnet.microsoft.com/download/dotnet/9.0) installed (see below)
- Authentication to your Azure DevOps organization (via `az login` and `az devops configure`)

## Installation

Clone this repository and build the project:

```bash
git clone https://github.com/Geertvdc/azdo-scanner.git
cd azdo-scanner
# Install the .NET 9 SDK if you don't already have it
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 9.0 --install-dir $HOME/.dotnet --no-path
export PATH="$HOME/.dotnet:$PATH"
dotnet build
```

### Running Tests

Execute the unit tests with:

```bash
dotnet test
```

### Building the Global Tool

Create a NuGet package that can be installed as a .NET tool:

```bash
dotnet pack -c Release
dotnet tool install --global --add-source ./src/azdo-scanner/bin/Release azdo-scanner
```

## Usage

Navigate to the build output directory and run the tool:

```bash
cd src/azdo-scanner/bin/Debug/net9.0
./azdo-scanner [command] [options]
```

### Available Commands

#### list-projects

Lists all Azure DevOps projects in the organization with their administrators, repositories and service connections (if specified).

```bash
# List all projects in the default organization
./azdo-scanner list-projects

# List projects with specific organization
./azdo-scanner list-projects --org https://dev.azure.com/myorg

# List specific projects only
./azdo-scanner list-projects --projects "Project1,Project2"

# Include repository information
./azdo-scanner list-projects --include-repos

# Include service connection information
./azdo-scanner list-projects --include-serviceconnections
```

Options:
- `--org <ORG>`: The Azure DevOps organization URL. If not provided, uses the default from Azure CLI configuration.
- `--projects <PROJECTS>`: Comma-separated list of project names to include. If not set, all projects are included.
- `--include-repos`: Include a list of repositories for each project with branch protection policy status.
- `--include-serviceconnections`: Include a list of service connections for each project.

#### list-extensions

Lists all installed Azure DevOps extensions in the organization with their permissions.

```bash
# List all extensions in the default organization
./azdo-scanner list-extensions

# List extensions with specific organization
./azdo-scanner list-extensions --org https://dev.azure.com/myorg
```

Options:
- `--org <ORG>`: The Azure DevOps organization URL. If not provided, uses the default from Azure CLI configuration.

## Research Documentation

### Service Connection Branch Protection Analysis

For detailed information on analyzing service connection security, particularly branch protection controls on AzureRM service connections, see:

- **[Service Connection Branch Protection Research](docs/service-connection-branch-protection-research.md)** - Comprehensive guide on using Azure DevOps CLI to check branch protection controls on service connections
- **[Test Script](test-service-connection-checks.sh)** - Validation script for testing the documented approach

This research addresses how to identify service connections that restrict usage to specific branches (e.g., main branch only) to prevent unauthorized deployments from feature branches to production environments.

## Note

This tool requires appropriate permissions in your Azure DevOps organization to access projects, repositories, and other resources.
