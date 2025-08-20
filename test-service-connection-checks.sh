#!/bin/bash

# Test script for Azure DevOps Service Connection Branch Protection Research
# This script tests the documented approach for checking branch protection on service connections

set -e

# Configuration
ORG="${AZDO_ORG:-https://dev.azure.com/geertvdc}"
PROJECT="${AZDO_PROJECT:-test-project-1}"
TEST_CONN_ID="${AZDO_TEST_CONN_ID:-9159adab-5a9e-4594-8bcb-1abff7e6aab6}"
TEST_CONN_NAME="${AZDO_TEST_CONN_NAME:-azure-1}"

echo "=== Azure DevOps Service Connection Branch Protection Test ==="
echo "Organization: $ORG"
echo "Project: $PROJECT"
echo "Test Service Connection: $TEST_CONN_NAME ($TEST_CONN_ID)"
echo ""

# Check prerequisites
echo "Checking prerequisites..."

# Check if Azure CLI is installed
if ! command -v az &> /dev/null; then
    echo "❌ Azure CLI is not installed. Please install it first."
    echo "   curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash"
    exit 1
fi
echo "✅ Azure CLI found: $(az --version | head -1)"

# Check if Azure DevOps extension is installed
if ! az extension list | grep -q "azure-devops"; then
    echo "❌ Azure DevOps CLI extension is not installed."
    echo "   Please install it: az extension add --name azure-devops"
    exit 1
fi
echo "✅ Azure DevOps CLI extension found"

# Check if authenticated (PAT token)
if [ -z "${AZDO_PAT}" ]; then
    echo "❌ AZDO_PAT environment variable is not set."
    echo "   Please set your Personal Access Token: export AZDO_PAT='your-pat-token'"
    exit 1
fi
echo "✅ Personal Access Token found"

# Check if jq is available for JSON parsing
if ! command -v jq &> /dev/null; then
    echo "❌ jq is not installed. Please install it for JSON parsing."
    echo "   sudo apt-get update && sudo apt-get install -y jq"
    exit 1
fi
echo "✅ jq found for JSON parsing"

echo ""

# Configure Azure DevOps CLI
echo "Configuring Azure DevOps CLI..."
export AZURE_DEVOPS_EXT_PAT="$AZDO_PAT"
az devops configure --defaults organization="$ORG" project="$PROJECT"
echo "✅ Azure DevOps CLI configured"
echo ""

# Test 1: List all service connections in the project
echo "=== Test 1: Listing Service Connections ==="
echo "Command: az devops service-endpoint list --project \"$PROJECT\" --output json"

SERVICE_CONNECTIONS=$(az devops service-endpoint list --project "$PROJECT" --output json 2>/dev/null || echo "[]")

if [ "$SERVICE_CONNECTIONS" = "[]" ]; then
    echo "⚠️  No service connections found or access denied"
else
    echo "✅ Service connections retrieved successfully"
    echo "Service connections found:"
    echo "$SERVICE_CONNECTIONS" | jq -r '.[] | "  - \(.name) (\(.id)) - Type: \(.type)"'
    
    # Check if our test connection exists
    if echo "$SERVICE_CONNECTIONS" | jq -e ".[] | select(.id == \"$TEST_CONN_ID\")" > /dev/null; then
        echo "✅ Test service connection '$TEST_CONN_NAME' found"
    else
        echo "⚠️  Test service connection '$TEST_CONN_NAME' not found in results"
    fi
fi
echo ""

# Test 2: Try Method 1 - Pipeline Checks API
echo "=== Test 2: Pipeline Checks API Method ==="
echo "Command: az devops invoke --area pipelines --resource checks ..."

CHECKS_RESULT=$(az devops invoke \
    --area "pipelines" \
    --resource "checks" \
    --route-parameters project="$PROJECT" \
    --http-method GET \
    --api-version "7.0-preview.1" \
    --query-parameters "\$expand=1&resourceType=endpoint&resourceId=$TEST_CONN_ID" \
    2>/dev/null || echo '{"value": []}')

if [ "$(echo "$CHECKS_RESULT" | jq -r '.value | length')" -gt 0 ]; then
    echo "✅ Pipeline checks retrieved successfully"
    echo "Checks found:"
    echo "$CHECKS_RESULT" | jq -r '.value[] | "  - \(.settings.displayName // "Unknown") (Type: \(.type.name // "Unknown"))"'
    
    # Look for branch control
    BRANCH_CONTROLS=$(echo "$CHECKS_RESULT" | jq '.value[] | select(.settings.displayName == "Branch control")')
    if [ -n "$BRANCH_CONTROLS" ]; then
        echo "✅ Branch control found!"
        echo "$BRANCH_CONTROLS" | jq -r '"  Allowed branches: " + (.settings.inputs.allowedBranches // "Not set")'
        echo "$BRANCH_CONTROLS" | jq -r '"  Ensure protection: " + (.settings.inputs.ensureProtectionOfBranch // "Not set")'
        echo "$BRANCH_CONTROLS" | jq -r '"  Allow unknown status: " + (.settings.inputs.allowUnknownStatusBranch // "Not set")'
    else
        echo "⚠️  No branch control configured"
    fi
else
    echo "⚠️  No checks found or access denied"
fi
echo ""

# Test 3: Try Method 2 - Contribution HierarchyQuery API
echo "=== Test 3: Contribution HierarchyQuery API Method ==="

# Create query payload
QUERY_PAYLOAD=$(cat << EOF
{
  "contributionIds": ["ms.vss-pipelinechecks.checks-data-provider"],
  "dataProviderContext": {
    "properties": {
      "resourceId": "$TEST_CONN_ID",
      "sourcePage": {
        "url": "$ORG/$PROJECT/_settings/adminservices?resourceId=$TEST_CONN_ID",
        "routeId": "ms.vss-admin-web.project-admin-hub-route",
        "routeValues": {
          "project": "$PROJECT",
          "adminPivot": "adminservices",
          "controller": "ContributedPage",
          "action": "Execute"
        }
      }
    }
  }
}
EOF
)

echo "Query payload:"
echo "$QUERY_PAYLOAD" | jq '.'
echo ""

# Create temporary file for payload
TEMP_PAYLOAD=$(mktemp)
echo "$QUERY_PAYLOAD" > "$TEMP_PAYLOAD"

echo "Command: az devops invoke --area Contribution --resource HierarchyQuery ..."

HIERARCHY_RESULT=$(az devops invoke \
    --area "Contribution" \
    --resource "HierarchyQuery" \
    --http-method POST \
    --in-file "$TEMP_PAYLOAD" \
    --api-version "7.0-preview.1" \
    2>/dev/null || echo '{}')

# Clean up temp file
rm -f "$TEMP_PAYLOAD"

if echo "$HIERARCHY_RESULT" | jq -e '.dataProviders["ms.vss-pipelinechecks.checks-data-provider"].checkConfigurationDataList' > /dev/null 2>&1; then
    echo "✅ HierarchyQuery API responded successfully"
    
    # Extract branch controls
    BRANCH_CHECKS=$(echo "$HIERARCHY_RESULT" | jq '.dataProviders["ms.vss-pipelinechecks.checks-data-provider"].checkConfigurationDataList[] | select(.checkConfiguration.settings.displayName == "Branch control")')
    
    if [ -n "$BRANCH_CHECKS" ] && [ "$BRANCH_CHECKS" != "null" ]; then
        echo "✅ Branch control configuration found!"
        echo ""
        echo "Branch Control Details:"
        
        ALLOWED_BRANCHES=$(echo "$BRANCH_CHECKS" | jq -r '.checkConfiguration.settings.inputs.allowedBranches // "Not set"')
        ENSURE_PROTECTION=$(echo "$BRANCH_CHECKS" | jq -r '.checkConfiguration.settings.inputs.ensureProtectionOfBranch // "Not set"')
        ALLOW_UNKNOWN=$(echo "$BRANCH_CHECKS" | jq -r '.checkConfiguration.settings.inputs.allowUnknownStatusBranch // "Not set"')
        
        echo "  Allowed branches: $ALLOWED_BRANCHES"
        echo "  Ensure protection: $ENSURE_PROTECTION"
        echo "  Allow unknown status: $ALLOW_UNKNOWN"
        echo ""
        
        # Security assessment
        echo "Security Assessment:"
        if [[ "$ENSURE_PROTECTION" == "true" && "$ALLOW_UNKNOWN" == "false" ]] && [[ "$ALLOWED_BRANCHES" =~ ^(main|master)(,.*)?$ ]]; then
            echo "  🔒 Status: GOOD - Well configured branch protection"
        elif [[ "$ENSURE_PROTECTION" == "true" ]]; then
            echo "  ⚠️  Status: MODERATE - Branch protection enabled but with some risks"
        else
            echo "  ❌ Status: POOR - Insufficient branch protection"
        fi
        
        # Additional details
        echo ""
        echo "Full configuration:"
        echo "$BRANCH_CHECKS" | jq '.'
    else
        echo "⚠️  No branch control configured for this service connection"
    fi
else
    echo "⚠️  HierarchyQuery API did not return expected data structure"
    echo "Response received:"
    echo "$HIERARCHY_RESULT" | jq '.' 2>/dev/null || echo "Invalid JSON response"
fi

echo ""
echo "=== Test Summary ==="
echo "This test script validates the approach documented in:"
echo "docs/service-connection-branch-protection-research.md"
echo ""
echo "If you saw successful results above, the research approach is validated."
echo "If you encountered errors, it may be due to:"
echo "1. Insufficient permissions in the Azure DevOps organization"
echo "2. API version changes"
echo "3. Network connectivity issues"
echo "4. Different Azure DevOps organization structure"
echo ""
echo "The documented approach should work with proper authentication and permissions."