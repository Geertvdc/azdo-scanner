# Quick Reference: Service Connection Branch Protection Commands

## Basic Setup
```bash
# Set Personal Access Token
export AZDO_PAT="your-pat-here"

# Configure defaults
az devops configure --defaults organization=https://dev.azure.com/yourorg project=yourproject
```

## List Service Connections
```bash
# List all service connections
az devops service-endpoint list --output table

# List only AzureRM connections with JSON output
az devops service-endpoint list --output json | jq '.[] | select(.type=="azurerm") | {name, id, type}'
```

## Check Branch Protection (Method 1 - Pipeline Checks API)
```bash
az devops invoke \
  --area "pipelines" \
  --resource "checks" \
  --route-parameters project="yourproject" \
  --http-method GET \
  --api-version "7.1-preview.1" \
  --query-parameters '$expand=1&resourceType=endpoint&resourceId=YOUR-SERVICE-CONNECTION-ID'
```

## Check Branch Protection (Method 2 - Contribution API)
```bash
# Create payload file
cat > payload.json << 'EOF'
{
  "contributionIds": ["ms.vss-pipelinechecks.checks-data-provider"],
  "dataProviderContext": {
    "properties": {
      "resourceId": "YOUR-SERVICE-CONNECTION-ID",
      "sourcePage": {
        "url": "https://dev.azure.com/yourorg/yourproject/_settings/adminservices?resourceId=YOUR-SERVICE-CONNECTION-ID",
        "routeId": "ms.vss-admin-web.project-admin-hub-route",
        "routeValues": {
          "project": "yourproject",
          "adminPivot": "adminservices",
          "controller": "ContributedPage",
          "action": "Execute"
        }
      }
    }
  }
}
EOF

# Execute query
az devops invoke \
  --area "Contribution" \
  --resource "HierarchyQuery" \
  --http-method POST \
  --in-file payload.json \
  --api-version "7.1-preview.1"
```

## Parse Results
```bash
# Extract branch control settings
RESULT=$(az devops invoke ...) # your command here

# Check if branch control exists
echo "$RESULT" | jq '.dataProviders["ms.vss-pipelinechecks.checks-data-provider"].checkConfigurationDataList[] | select(.checkConfiguration.settings.displayName == "Branch control")'

# Get specific settings
echo "$RESULT" | jq -r '.dataProviders["ms.vss-pipelinechecks.checks-data-provider"].checkConfigurationDataList[] | select(.checkConfiguration.settings.displayName == "Branch control") | .checkConfiguration.settings.inputs.allowedBranches'
```

## Test Environment
For testing with provided example:
```bash
export AZDO_PAT="your-pat"
az devops configure --defaults organization=https://dev.azure.com/geertvdc project=test-project-1

# Test service connection: azure-1 (9159adab-5a9e-4594-8bcb-1abff7e6aab6)
./test-service-connection-checks.sh
```