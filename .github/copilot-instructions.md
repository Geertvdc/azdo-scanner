# Azure DevOps Scanner - GitHub Copilot Instructions

Always follow these instructions first and only fallback to additional search and context gathering if the information here is incomplete or found to be in error.

## Working Effectively

### Bootstrap, Build, and Test the Repository
Execute these commands in order to set up the development environment:

1. Install .NET 9 SDK:
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
   bash dotnet-install.sh --channel 9.0 --install-dir $HOME/.dotnet --no-path
   export PATH="$HOME/.dotnet:$PATH"
   ```

2. Install Azure CLI with DevOps extension:
   ```bash
   curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
   # Azure DevOps extension is automatically included
   ```

3. Build the project:
   ```bash
   dotnet build
   ```
   - **NEVER CANCEL**: Takes approximately 9 seconds. Set timeout to 30+ seconds.

4. Run tests:
   ```bash
   dotnet test
   ```
   - **NEVER CANCEL**: Takes approximately 1 second (5 tests total). Set timeout to 15+ seconds.

5. Build release package:
   ```bash
   dotnet pack -c Release
   ```
   - **NEVER CANCEL**: Takes approximately 2 seconds. Set timeout to 15+ seconds.

### Run the Application

#### Development Mode
Always use `dotnet run` during development to avoid .NET runtime issues:
```bash
dotnet run --project src/azdo-scanner -- --help
dotnet run --project src/azdo-scanner -- list-projects --help
dotnet run --project src/azdo-scanner -- list-extensions --help
```

#### Global Tool Installation (Production)
```bash
# Install from local build
dotnet tool install --global --add-source ./src/azdo-scanner/bin/Release zure-azdo-scanner

# Verify installation
zure-azdo-scanner --help
```
**Note**: Global tool requires .NET 9 runtime to be installed system-wide.

### Prerequisites Validation
The application performs automatic prerequisite checking. It requires:
- Azure CLI (`az --version`)
- Azure DevOps CLI extension (`az extension list`)
- Authentication to Azure DevOps (`az login` and `az devops configure`)

## Validation Scenarios

**ALWAYS validate changes by running through these scenarios:**

1. **Build Validation**: Run `dotnet build` and ensure it succeeds without warnings
2. **Test Validation**: Run `dotnet test` and verify all 5 tests pass
3. **Application Startup**: Run `dotnet run --project src/azdo-scanner -- --help` and verify help output displays correctly
4. **Command Structure**: Verify both commands are available:
   - `list-projects` - Lists Azure DevOps projects with administrators
   - `list-extensions` - Lists installed extensions with permissions

**Manual Testing Requirements:**
- Always test that prerequisite checking works correctly
- Verify help text displays properly for all commands
- Test that the application fails gracefully when prerequisites are missing

## Architecture Overview

### Key Projects
- **Main Application**: `src/azdo-scanner/` - The CLI application
- **Tests**: `tests/AzdoScanner.Tests/` - Unit tests (xUnit framework)

### Important Files
- `azdo-scanner.sln` - Main solution file
- `src/azdo-scanner/azdo-scanner.csproj` - Main project file (.NET 9, CLI tool)
- `src/azdo-scanner/Program.cs` - Entry point with DI setup and command registration
- `src/azdo-scanner/Infrastructure/PrerequisiteChecker.cs` - Validates Azure CLI requirements
- `.github/workflows/publish.yml` - CI/CD pipeline for NuGet publishing

### Command Structure
The application uses Spectre.Console.Cli framework:
- Commands in `src/azdo-scanner/Cli/` directory
- `ListProjectsCommand.cs` - Implements list-projects command
- `ListExtensionsCommand.cs` - Implements list-extensions command
- Dependency injection configured in `Program.cs`

### External Dependencies
- **Azure CLI**: Used as subprocess to interact with Azure DevOps
- **Spectre.Console**: Provides rich CLI UI and command framework
- **Microsoft.Extensions**: Dependency injection and hosting

## Common Development Tasks

### Adding New Commands
1. Create command class in `src/azdo-scanner/Cli/` inheriting from `AsyncCommand<TSettings>`
2. Register in `Program.cs` in the `app.Configure()` method
3. Add corresponding unit tests in `tests/AzdoScanner.Tests/`

### Working with Prerequisites
- Always check `PrerequisiteChecker.cs` when modifying external dependencies
- The application exits early if prerequisites aren't met
- Prerequisite validation happens before command execution

### Testing Changes
- Run `dotnet test --verbosity normal` for detailed test output
- Tests use xUnit framework with fake process runners for Azure CLI mocking
- Focus on `OrganizationResolverTests.cs` when working with Azure DevOps organization logic

### Build Artifacts
- Debug build: `src/azdo-scanner/bin/Debug/net9.0/`
- Release build: `src/azdo-scanner/bin/Release/net9.0/`
- NuGet package: `src/azdo-scanner/bin/Release/zure-azdo-scanner.{version}.nupkg`

## CI/CD Pipeline (.github/workflows/publish.yml)
- Runs on push to main and pull requests
- Build, test, and publish to NuGet on main branch
- Uses automated versioning based on git tags
- **NEVER CANCEL**: Full pipeline may take several minutes

## Troubleshooting

### Common Issues
- **"Azure CLI not found"**: Run prerequisite installation commands
- **".NET runtime not found"**: Use `dotnet run` instead of direct execution during development
- **"No Azure DevOps defaults configured"**: Run `az devops configure --defaults organization=https://dev.azure.com/<org>`

### Development Environment
- Always use `export PATH="$HOME/.dotnet:$PATH"` when using locally installed .NET 9
- Global tool installation requires system-wide .NET 9 runtime
- Azure CLI requires login and organization configuration for actual functionality testing

## Quick Reference Commands
```bash
# Full development setup
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh && bash dotnet-install.sh --channel 9.0 --install-dir $HOME/.dotnet --no-path
export PATH="$HOME/.dotnet:$PATH"
curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash
dotnet build && dotnet test

# Run application commands
dotnet run --project src/azdo-scanner -- --help
dotnet run --project src/azdo-scanner -- list-projects --help

# Release build and package
dotnet pack -c Release
```