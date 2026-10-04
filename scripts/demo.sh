#!/usr/bin/env bash
set -e
PRODUCT="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
CUSTOMER="11111111-1111-1111-1111-111111111111"

echo '=== HAPPY PATH ==='
curl -s -X POST http://localhost:5001/api/orders -H 'Content-Type: application/json' -d "{\"customerId\":\"$CUSTOMER\",\"productId\":\"$PRODUCT\",\"quantity\":2,\"amount\":2000}"; echo

echo '=== FAILURE + COMPENSATION PATH ==='
curl -s -X POST http://localhost:5001/api/orders -H 'Content-Type: application/json' -d "{\"customerId\":\"$CUSTOMER\",\"productId\":\"$PRODUCT\",\"quantity\":1,\"amount\":100000}"; echo

echo '=== INVENTORY AFTER COMPENSATION ==='
curl -s http://localhost:5002/api/inventory/$PRODUCT; echo
