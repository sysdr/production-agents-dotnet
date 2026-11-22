#!/usr/bin/env bash
# Lesson 0.1 end-to-end check. Run while `dotnet run --project src/AppHost` is up.
set -euo pipefail
USER_URL=${USER_URL:-http://localhost:5101}
ORDER_URL=${ORDER_URL:-http://localhost:5102}
AZURITE="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"

echo "UserService  /health -> $(curl -s -o /dev/null -w '%{http_code}' $USER_URL/health)"
echo "OrderService /health -> $(curl -s -o /dev/null -w '%{http_code}' $ORDER_URL/health)"

TOKEN=$(curl -s -X POST $USER_URL/auth/token -H 'content-type: application/json' \
  -d '{"UserId":"verify","Email":"verify@example.com","TenantId":"tenant-01"}' \
  | sed -E 's/.*"token":"([^"]+)".*/\1/')
ORDER=$(curl -s -X POST $ORDER_URL/orders/create -H "Authorization: Bearer $TOKEN" \
  -H 'content-type: application/json' -d '{}')
echo "POST /orders/create -> $ORDER"
ID=$(echo "$ORDER" | sed -E 's/.*"orderId":"([^"]+)".*/\1/')

for i in $(seq 1 12); do
  BLOB=$(docker run --rm --network host mcr.microsoft.com/azure-cli:latest \
    az storage blob list --connection-string "$AZURITE" -c processed-orders \
    --query "[?ends_with(name,'$ID.json')].name" -o tsv 2>/dev/null || true)
  if [ -n "$BLOB" ]; then echo "Blob written: $BLOB"; echo PASS; exit 0; fi
  sleep 5
done
echo "FAIL: no processed blob for $ID after 60s"; exit 1
