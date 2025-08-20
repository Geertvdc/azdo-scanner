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

# Try multiple API versions and approaches
echo "🔄 Trying different API approaches..."

# Approach 2.1: Try with different API versions and query formats
echo ""
echo "📋 Approach 2.1: Pipeline Checks API (v7.1-preview.1)"
echo "Command: az devops invoke --area pipelines --resource checks ..."

CHECKS_RESULT_V71=$(az devops invoke \
    --area "pipelines" \
    --resource "checks" \
    --route-parameters project="$PROJECT" \
    --http-method GET \
    --api-version "7.1-preview.1" \
    --query-parameters "resourceType=endpoint&resourceId=$TEST_CONN_ID" \
    2>/dev/null || echo '{"value": []}')

echo "Response: $(echo "$CHECKS_RESULT_V71" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Approach 2.2: Try with configurations endpoint
echo ""
echo "📋 Approach 2.2: Check Configurations API"
echo "Command: az devops invoke --area pipelines --resource checks/configurations ..."

CHECKS_CONFIG_RESULT=$(az devops invoke \
    --area "pipelines" \
    --resource "checks/configurations" \
    --route-parameters project="$PROJECT" \
    --http-method GET \
    --api-version "7.1-preview.1" \
    --query-parameters "resourceType=endpoint&resourceId=$TEST_CONN_ID" \
    2>/dev/null || echo '{"value": []}')

echo "Response: $(echo "$CHECKS_CONFIG_RESULT" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Approach 2.3: Try without route parameters
echo ""
echo "📋 Approach 2.3: Direct API call"
echo "Command: az devops invoke --area pipelines --resource checks ..."

CHECKS_DIRECT_RESULT=$(az devops invoke \
    --area "pipelines" \
    --resource "checks" \
    --http-method GET \
    --api-version "7.1-preview.1" \
    --query-parameters "project=$PROJECT&resourceType=endpoint&resourceId=$TEST_CONN_ID" \
    2>/dev/null || echo '{"value": []}')

echo "Response: $(echo "$CHECKS_DIRECT_RESULT" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Evaluate all results
echo ""
echo "📊 Evaluating results..."

# Check each result for valid data
BEST_RESULT=""
if [ "$(echo "$CHECKS_RESULT_V71" | jq -r '.value | length // 0' 2>/dev/null || echo 0)" -gt 0 ]; then
    echo "✅ Found data in Approach 2.1 (v7.1-preview.1)"
    BEST_RESULT="$CHECKS_RESULT_V71"
elif [ "$(echo "$CHECKS_CONFIG_RESULT" | jq -r '.value | length // 0' 2>/dev/null || echo 0)" -gt 0 ]; then
    echo "✅ Found data in Approach 2.2 (configurations endpoint)"
    BEST_RESULT="$CHECKS_CONFIG_RESULT"
elif [ "$(echo "$CHECKS_DIRECT_RESULT" | jq -r '.value | length // 0' 2>/dev/null || echo 0)" -gt 0 ]; then
    echo "✅ Found data in Approach 2.3 (direct call)"
    BEST_RESULT="$CHECKS_DIRECT_RESULT"
else
    echo "⚠️  No checks found in any approach"
fi

if [ -n "$BEST_RESULT" ] && [ "$BEST_RESULT" != '{"value": []}' ]; then
    echo ""
    echo "✅ Pipeline checks retrieved successfully"
    echo "Checks found:"
    echo "$BEST_RESULT" | jq -r '.value[]? | "  - \(.settings.displayName // .displayName // "Unknown") (Type: \(.type.name // .type // "Unknown"))"' 2>/dev/null || echo "  - Could not parse check details"
    
    # Look for branch control
    BRANCH_CONTROLS=$(echo "$BEST_RESULT" | jq '.value[]? | select(.settings.displayName == "Branch control" or .displayName == "Branch control")' 2>/dev/null || echo "")
    if [ -n "$BRANCH_CONTROLS" ] && [ "$BRANCH_CONTROLS" != "null" ] && [ "$BRANCH_CONTROLS" != "" ]; then
        echo "✅ Branch control found!"
        echo "$BRANCH_CONTROLS" | jq -r '"  Allowed branches: " + (.settings.inputs.allowedBranches // .inputs.allowedBranches // "Not set")' 2>/dev/null || echo "  Could not parse allowed branches"
        echo "$BRANCH_CONTROLS" | jq -r '"  Ensure protection: " + (.settings.inputs.ensureProtectionOfBranch // .inputs.ensureProtectionOfBranch // "Not set")' 2>/dev/null || echo "  Could not parse protection setting"
        echo "$BRANCH_CONTROLS" | jq -r '"  Allow unknown status: " + (.settings.inputs.allowUnknownStatusBranch // .inputs.allowUnknownStatusBranch // "Not set")' 2>/dev/null || echo "  Could not parse unknown status setting"
    else
        echo "⚠️  No branch control configured"
    fi
else
    echo "⚠️  No checks found or access denied"
fi
echo ""

# Test 3: Try Method 2 - Contribution HierarchyQuery API
echo "=== Test 3: Contribution HierarchyQuery API Method ==="

# Try multiple contribution API approaches
echo "🔄 Trying different contribution API approaches..."

# Approach 3.1: Original documented approach
echo ""
echo "📋 Approach 3.1: Original contribution approach"

QUERY_PAYLOAD_V1=$(cat << EOF
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

TEMP_PAYLOAD_V1=$(mktemp)
echo "$QUERY_PAYLOAD_V1" > "$TEMP_PAYLOAD_V1"

HIERARCHY_RESULT_V1=$(az devops invoke \
    --area "Contribution" \
    --resource "HierarchyQuery" \
    --http-method POST \
    --in-file "$TEMP_PAYLOAD_V1" \
    --api-version "7.1-preview.1" \
    2>/dev/null || echo '{}')

rm -f "$TEMP_PAYLOAD_V1"
echo "Response: $(echo "$HIERARCHY_RESULT_V1" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Approach 3.2: Alternative contribution ID
echo ""
echo "📋 Approach 3.2: Alternative contribution ID"

QUERY_PAYLOAD_V2=$(cat << EOF
{
  "contributionIds": ["ms.vss-pipelinechecks-web.checks-hub-data-provider"],
  "dataProviderContext": {
    "properties": {
      "resourceId": "$TEST_CONN_ID",
      "resourceType": "endpoint"
    }
  }
}
EOF
)

TEMP_PAYLOAD_V2=$(mktemp)
echo "$QUERY_PAYLOAD_V2" > "$TEMP_PAYLOAD_V2"

HIERARCHY_RESULT_V2=$(az devops invoke \
    --area "Contribution" \
    --resource "HierarchyQuery" \
    --http-method POST \
    --in-file "$TEMP_PAYLOAD_V2" \
    --api-version "7.1-preview.1" \
    2>/dev/null || echo '{}')

rm -f "$TEMP_PAYLOAD_V2"
echo "Response: $(echo "$HIERARCHY_RESULT_V2" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Approach 3.3: Simplified data provider context
echo ""
echo "📋 Approach 3.3: Simplified context"

QUERY_PAYLOAD_V3=$(cat << EOF
{
  "contributionIds": ["ms.vss-pipelinechecks.checks-data-provider"],
  "dataProviderContext": {
    "properties": {
      "resourceId": "$TEST_CONN_ID",
      "resourceType": "endpoint",
      "project": "$PROJECT"
    }
  }
}
EOF
)

TEMP_PAYLOAD_V3=$(mktemp)
echo "$QUERY_PAYLOAD_V3" > "$TEMP_PAYLOAD_V3"

HIERARCHY_RESULT_V3=$(az devops invoke \
    --area "Contribution" \
    --resource "HierarchyQuery" \
    --http-method POST \
    --in-file "$TEMP_PAYLOAD_V3" \
    --api-version "7.1-preview.1" \
    2>/dev/null || echo '{}')

rm -f "$TEMP_PAYLOAD_V3"
echo "Response: $(echo "$HIERARCHY_RESULT_V3" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Approach 3.4: Try direct REST API approach using az rest
echo ""
echo "📋 Approach 3.4: Direct REST API via az rest"

# Try using az rest command which might handle authentication better
REST_RESULT=$(az rest \
    --method GET \
    --url "https://dev.azure.com/${ORG#https://dev.azure.com/}/$PROJECT/_apis/pipelines/checks/configurations?resourceType=endpoint&resourceId=$TEST_CONN_ID&api-version=7.1-preview.1" \
    2>/dev/null || echo '{"value": []}')

echo "Response: $(echo "$REST_RESULT" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Evaluate all results
echo ""
echo "📊 Evaluating contribution API results..."

BEST_CONTRIB_RESULT=""
if echo "$HIERARCHY_RESULT_V1" | jq -e '.dataProviders' > /dev/null 2>&1; then
    echo "✅ Found data in Approach 3.1 (original)"
    BEST_CONTRIB_RESULT="$HIERARCHY_RESULT_V1"
elif echo "$HIERARCHY_RESULT_V2" | jq -e '.dataProviders' > /dev/null 2>&1; then
    echo "✅ Found data in Approach 3.2 (alternative contribution)"
    BEST_CONTRIB_RESULT="$HIERARCHY_RESULT_V2"
elif echo "$HIERARCHY_RESULT_V3" | jq -e '.dataProviders' > /dev/null 2>&1; then
    echo "✅ Found data in Approach 3.3 (simplified)"
    BEST_CONTRIB_RESULT="$HIERARCHY_RESULT_V3"
elif [ "$(echo "$REST_RESULT" | jq -r '.value | length // 0' 2>/dev/null || echo 0)" -gt 0 ]; then
    echo "✅ Found data in Approach 3.4 (REST API)"
    # Convert REST API result to contribution format for consistent processing
    BEST_CONTRIB_RESULT="$REST_RESULT"
else
    echo "⚠️  No data found in any contribution approach"
fi

# Process the best result
if [ -n "$BEST_CONTRIB_RESULT" ] && [ "$BEST_CONTRIB_RESULT" != '{}' ]; then
    echo ""
    
    # Try to extract branch controls from different possible structures
    BRANCH_CHECKS=""
    
    # Try contribution API structure first
    if echo "$BEST_CONTRIB_RESULT" | jq -e '.dataProviders' > /dev/null 2>&1; then
        echo "✅ Processing contribution API response"
        BRANCH_CHECKS=$(echo "$BEST_CONTRIB_RESULT" | jq -r '.dataProviders | to_entries[] | .value.checkConfigurationDataList[]? | select(.checkConfiguration.settings.displayName == "Branch control")' 2>/dev/null || echo "")
    fi
    
    # Try REST API structure if contribution didn't work
    if [ -z "$BRANCH_CHECKS" ] && echo "$BEST_CONTRIB_RESULT" | jq -e '.value' > /dev/null 2>&1; then
        echo "✅ Processing REST API response"
        BRANCH_CHECKS=$(echo "$BEST_CONTRIB_RESULT" | jq -r '.value[]? | select(.settings.displayName == "Branch control" or .displayName == "Branch control")' 2>/dev/null || echo "")
    fi
    
    if [ -n "$BRANCH_CHECKS" ] && [ "$BRANCH_CHECKS" != "null" ] && [ "$BRANCH_CHECKS" != "" ]; then
        echo "✅ Branch control configuration found!"
        echo ""
        echo "Branch Control Details:"
        
        # Try different possible field structures
        ALLOWED_BRANCHES=$(echo "$BRANCH_CHECKS" | jq -r '.checkConfiguration.settings.inputs.allowedBranches // .settings.inputs.allowedBranches // .inputs.allowedBranches // "Not set"' 2>/dev/null || echo "Not set")
        ENSURE_PROTECTION=$(echo "$BRANCH_CHECKS" | jq -r '.checkConfiguration.settings.inputs.ensureProtectionOfBranch // .settings.inputs.ensureProtectionOfBranch // .inputs.ensureProtectionOfBranch // "Not set"' 2>/dev/null || echo "Not set")
        ALLOW_UNKNOWN=$(echo "$BRANCH_CHECKS" | jq -r '.checkConfiguration.settings.inputs.allowUnknownStatusBranch // .settings.inputs.allowUnknownStatusBranch // .inputs.allowUnknownStatusBranch // "Not set"' 2>/dev/null || echo "Not set")
        
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
        echo "$BRANCH_CHECKS" | jq '.' 2>/dev/null || echo "$BRANCH_CHECKS"
    else
        echo "⚠️  No branch control configured for this service connection"
    fi
else
    echo "⚠️  No valid API response received from any approach"
    echo "This might indicate:"
    echo "  - No checks are configured on this service connection"
    echo "  - Insufficient permissions to access check configurations"
    echo "  - API endpoint or structure has changed"
    echo "  - Authentication issues"
fi

echo ""

# Test 4: Additional API exploration
echo "=== Test 4: Additional API Methods ==="

echo "🔄 Exploring additional Azure DevOps APIs..."

# Approach 4.1: Try the approvals endpoint
echo ""
echo "📋 Approach 4.1: Approvals API"
APPROVALS_RESULT=$(az devops invoke \
    --area "pipelines" \
    --resource "approvals" \
    --route-parameters project="$PROJECT" \
    --http-method GET \
    --api-version "7.1-preview.1" \
    --query-parameters "resourceType=endpoint&resourceId=$TEST_CONN_ID" \
    2>/dev/null || echo '{"value": []}')

echo "Response: $(echo "$APPROVALS_RESULT" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Approach 4.2: Try direct resource endpoint
echo ""
echo "📋 Approach 4.2: Resource endpoint checks"
RESOURCE_CHECKS=$(az devops invoke \
    --area "distributedtask" \
    --resource "serviceendpoints" \
    --route-parameters project="$PROJECT" endpointId="$TEST_CONN_ID" \
    --http-method GET \
    --api-version "7.1-preview.1" \
    2>/dev/null || echo '{}')

echo "Response: $(echo "$RESOURCE_CHECKS" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Approach 4.3: Try policy configurations
echo ""
echo "📋 Approach 4.3: Policy configurations"
POLICY_RESULT=$(az devops invoke \
    --area "policy" \
    --resource "configurations" \
    --route-parameters project="$PROJECT" \
    --http-method GET \
    --api-version "7.1-preview.1" \
    2>/dev/null || echo '{"value": []}')

echo "Response: $(echo "$POLICY_RESULT" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

# Approach 4.4: Environment checks (if applicable)  
echo ""
echo "📋 Approach 4.4: Environment checks"
ENV_CHECKS=$(az devops invoke \
    --area "distributedtask" \
    --resource "environments" \
    --route-parameters project="$PROJECT" \
    --http-method GET \
    --api-version "7.1-preview.1" \
    2>/dev/null || echo '{"value": []}')

echo "Response: $(echo "$ENV_CHECKS" | jq -c '.' 2>/dev/null || echo "Invalid JSON")"

echo ""
echo "=== Test Summary ==="
echo "This enhanced test script validates multiple approaches for checking branch protection:"
echo ""
echo "📋 Methods Tested:"
echo "  - Test 1: Service connection listing (basic functionality)"
echo "  - Test 2: Pipeline Checks API (3 different approaches)"
echo "  - Test 3: Contribution HierarchyQuery API (4 different approaches)"
echo "  - Test 4: Additional exploration APIs (4 alternative endpoints)"
echo ""
echo "📊 Total: 12 different API approaches tested"
echo ""
echo "✅ If any method showed successful results above, that approach can be used."
echo ""
echo "⚠️  If all methods failed, it could be due to:"
echo "1. Insufficient permissions (Contributor/Project Administrator rights needed)"
echo "2. No branch protection checks configured on the test service connection"
echo "3. API version changes or endpoint availability in your Azure DevOps instance"
echo "4. Network connectivity or authentication issues"
echo "5. Service connection doesn't support checks (older connection types)"
echo ""
echo "🔧 Troubleshooting steps:"
echo "1. Verify you have sufficient permissions in the Azure DevOps project"
echo "2. Check if the service connection has any 'Approvals and checks' configured in the UI"
echo "3. Try with a different service connection that you know has branch controls"
echo "4. Verify the PAT token has the required scopes (Build, Release, Service Connections)"
echo ""
echo "📚 Documentation Reference:"
echo "docs/service-connection-branch-protection-research.md"
echo ""
echo "The research approach is comprehensive and should work with proper setup."