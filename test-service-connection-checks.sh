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
    exit 1
else
    echo "✅ Service connections retrieved successfully"
    echo "Service connections found:"
    echo "$SERVICE_CONNECTIONS" | jq -r '.[] | "  - \(.name) (\(.id)) - Type: \(.type)"'
    
    # Check if our test connection exists
    if echo "$SERVICE_CONNECTIONS" | jq -e ".[] | select(.id == \"$TEST_CONN_ID\")" > /dev/null; then
        echo "✅ Test service connection '$TEST_CONN_NAME' found"
    else
        echo "⚠️  Test service connection '$TEST_CONN_NAME' not found in results"
        echo "Available service connections:"
        echo "$SERVICE_CONNECTIONS" | jq -r '.[] | "  - \(.name) (\(.id))"'
        exit 1
    fi
fi
echo ""

# Test 2: Check for existing approvals and checks using correct API
echo "=== Test 2: Check Service Connection Approvals and Checks ==="

# First, let's try to get the service connection details
echo "🔄 Getting service connection details..."
echo "Command: az devops service-endpoint show --id $TEST_CONN_ID"

SERVICE_ENDPOINT_DETAILS=$(az devops service-endpoint show --id "$TEST_CONN_ID" --output json 2>/dev/null || echo '{}')
echo "Service endpoint details retrieved: $(echo "$SERVICE_ENDPOINT_DETAILS" | jq -c '. | {name: .name, type: .type, isReady: .isReady}' 2>/dev/null || echo "Failed to parse")"

# Check for pipeline checks using correct API endpoint and parameters
echo ""
echo "🔄 Checking for pipeline checks on service connection..."

# Method 1: Pipeline checks with resource query
echo "📋 Method 2.1: Pipeline Checks API (correct resource query)"
echo "Command: az devops invoke --area pipelines --resource checks/queryresource --http-method POST"

# Create payload for resource query
RESOURCE_QUERY_PAYLOAD=$(cat << EOF
{
  "resourceType": "endpoint",
  "resourceId": "$TEST_CONN_ID"
}
EOF
)

TEMP_RESOURCE_PAYLOAD=$(mktemp)
echo "$RESOURCE_QUERY_PAYLOAD" > "$TEMP_RESOURCE_PAYLOAD"

PIPELINE_CHECKS_RESULT=$(az devops invoke \
    --area "pipelines" \
    --resource "checks/queryresource" \
    --route-parameters project="$PROJECT" \
    --http-method POST \
    --in-file "$TEMP_RESOURCE_PAYLOAD" \
    --api-version "7.1-preview.1" \
    2>/dev/null || echo '{"value": []}')

rm -f "$TEMP_RESOURCE_PAYLOAD"
echo "Response: $(echo "$PIPELINE_CHECKS_RESULT" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Method 2: Try getting all checks and filter
echo ""
echo "📋 Method 2.2: Get All Pipeline Checks"
echo "Command: az devops invoke --area pipelines --resource checks/configurations"

ALL_CHECKS_RESULT=$(az devops invoke \
    --area "pipelines" \
    --resource "checks/configurations" \
    --route-parameters project="$PROJECT" \
    --http-method GET \
    --api-version "7.1-preview.1" \
    2>/dev/null || echo '{"value": []}')

echo "Response: $(echo "$ALL_CHECKS_RESULT" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Filter for our specific resource
if [ "$(echo "$ALL_CHECKS_RESULT" | jq -r '.value | length // 0' 2>/dev/null || echo 0)" -gt 0 ]; then
    echo "Filtering for resource ID: $TEST_CONN_ID"
    FILTERED_CHECKS=$(echo "$ALL_CHECKS_RESULT" | jq --arg resource_id "$TEST_CONN_ID" '.value[] | select(.resource.id == $resource_id)' 2>/dev/null || echo "")
    if [ -n "$FILTERED_CHECKS" ] && [ "$FILTERED_CHECKS" != "null" ]; then
        echo "✅ Found checks for our service connection:"
        echo "$FILTERED_CHECKS" | jq '.'
    else
        echo "⚠️  No checks found for service connection $TEST_CONN_ID"
    fi
fi

# Method 3: Use distributedtask API to get service endpoint with checks
echo ""
echo "📋 Method 2.3: Distributed Task Service Endpoint API"
echo "Command: az devops invoke --area distributedtask --resource serviceendpoints"

SERVICE_ENDPOINT_FULL=$(az devops invoke \
    --area "distributedtask" \
    --resource "serviceendpoints" \
    --route-parameters project="$PROJECT" endpointId="$TEST_CONN_ID" \
    --http-method GET \
    --api-version "7.1-preview.4" \
    2>/dev/null || echo '{}')

echo "Response: $(echo "$SERVICE_ENDPOINT_FULL" | jq -c '. | {name: .name, type: .type, authorization: .authorization.scheme}' 2>/dev/null || echo "Invalid JSON")"

# Check if it has authorization or other check-related properties
if echo "$SERVICE_ENDPOINT_FULL" | jq -e '.authorization' > /dev/null 2>&1; then
    echo "Authorization details found:"
    echo "$SERVICE_ENDPOINT_FULL" | jq '.authorization'
fi

echo ""

# Test 3: Check for environment-based controls
echo "=== Test 3: Environment and Resource Authorization ==="

# Method 1: Check environments that might use this service connection
echo "🔄 Checking environments..."
echo "Command: az devops invoke --area distributedtask --resource environments"

ENVIRONMENTS_RESULT=$(az devops invoke \
    --area "distributedtask" \
    --resource "environments" \
    --route-parameters project="$PROJECT" \
    --http-method GET \
    --api-version "7.1-preview.1" \
    2>/dev/null || echo '{"value": []}')

echo "Environments found: $(echo "$ENVIRONMENTS_RESULT" | jq -r '.value | length // 0' 2>/dev/null || echo 0)"

if [ "$(echo "$ENVIRONMENTS_RESULT" | jq -r '.value | length // 0' 2>/dev/null || echo 0)" -gt 0 ]; then
    echo "Environment details:"
    echo "$ENVIRONMENTS_RESULT" | jq -r '.value[] | "  - \(.name) (ID: \(.id))"' 2>/dev/null || echo "Could not parse environments"
    
    # For each environment, check if it has checks related to our service connection
    echo "$ENVIRONMENTS_RESULT" | jq -r '.value[].id' 2>/dev/null | while read -r env_id; do
        if [ -n "$env_id" ]; then
            echo "Checking environment $env_id for approvals..."
            ENV_CHECKS=$(az devops invoke \
                --area "pipelines" \
                --resource "checks/configurations" \
                --route-parameters project="$PROJECT" \
                --query-parameters "resourceType=environment&resourceId=$env_id" \
                --http-method GET \
                --api-version "7.1-preview.1" \
                2>/dev/null || echo '{"value": []}')
            
            if [ "$(echo "$ENV_CHECKS" | jq -r '.value | length // 0' 2>/dev/null || echo 0)" -gt 0 ]; then
                echo "Found checks for environment $env_id:"
                echo "$ENV_CHECKS" | jq '.value[]'
            fi
        fi
    done
fi

echo ""

# Test 4: Check build/release definitions that use this service connection
echo "=== Test 4: Build/Release Definition Analysis ==="

echo "🔄 Checking build definitions that might use this service connection..."

# Get build definitions
BUILD_DEFINITIONS=$(az devops invoke \
    --area "build" \
    --resource "definitions" \
    --route-parameters project="$PROJECT" \
    --http-method GET \
    --api-version "7.1-preview.7" \
    2>/dev/null || echo '{"value": []}')

echo "Build definitions found: $(echo "$BUILD_DEFINITIONS" | jq -r '.value | length // 0' 2>/dev/null || echo 0)"

if [ "$(echo "$BUILD_DEFINITIONS" | jq -r '.value | length // 0' 2>/dev/null || echo 0)" -gt 0 ]; then
    # Check each build definition for our service connection
    echo "Checking build definitions for service connection usage..."
    echo "$BUILD_DEFINITIONS" | jq -r '.value[] | select(.repository.properties.connectedServiceId // .triggers[]?.branchFilters[]? // false) | .name' 2>/dev/null | head -3 | while read -r build_name; do
        if [ -n "$build_name" ]; then
            echo "  - Found potential usage in: $build_name"
        fi
    done
fi

echo ""

# Test 5: Alternative approach - Check project settings and policies
echo "=== Test 5: Project Settings and Security ==="

echo "🔄 Checking project-level policies..."

# Check project policies
PROJECT_POLICIES=$(az devops invoke \
    --area "policy" \
    --resource "configurations" \
    --route-parameters project="$PROJECT" \
    --http-method GET \
    --api-version "7.1-preview.1" \
    2>/dev/null || echo '{"value": []}')

echo "Project policies found: $(echo "$PROJECT_POLICIES" | jq -r '.value | length // 0' 2>/dev/null || echo 0)"

if [ "$(echo "$PROJECT_POLICIES" | jq -r '.value | length // 0' 2>/dev/null || echo 0)" -gt 0 ]; then
    echo "Policy types found:"
    echo "$PROJECT_POLICIES" | jq -r '.value[] | "  - \(.type.displayName // .type.id) (Enabled: \(.isEnabled))"' 2>/dev/null || echo "Could not parse policies"
fi

echo ""

echo "=== Test Summary ==="
echo "This comprehensive test script validates multiple approaches for checking branch protection:"
echo ""
echo "📋 Methods Tested:"
echo "  - Test 1: Service connection listing and validation"
echo "  - Test 2: Pipeline checks and approvals API (3 different methods)"
echo "  - Test 3: Environment-based authorization controls"
echo "  - Test 4: Build/Release definition analysis"
echo "  - Test 5: Project-level policy configurations"
echo ""
echo "✅ If any method showed successful results above, that approach can be used."
echo ""
echo "🔍 **Key Findings:**"
echo "1. If no checks were found, the service connection might not have any configured"
echo "2. Branch protection might be enforced at the environment level instead"
echo "3. Some protection might be in build/release definitions rather than service connection checks"
echo "4. Project-level policies might provide the protection instead"
echo ""
echo "⚠️  **Next Steps if No Protection Found:**"
echo "1. **Configure branch protection**: Go to Project Settings > Service connections > Select '$TEST_CONN_NAME' > Security"
echo "2. **Add approvals and checks**: Add 'Branch control' check to restrict deployments to main branch"
echo "3. **Set up environments**: Use environments with approval checks for better control"
echo "4. **Implement pipeline policies**: Use YAML pipeline approvals and checks"
echo ""
echo "🔧 **Troubleshooting:**"
echo "- Verify you have 'Project Collection Administrators' or 'Project Administrators' permissions"
echo "- Check if the service connection is used in any pipelines"
echo "- Ensure the PAT token has sufficient scopes (Build, Release, Service Connections)"
echo ""
echo "📚 **Documentation Reference:**"
echo "docs/service-connection-branch-protection-research.md"