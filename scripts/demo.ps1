$customer = "11111111-1111-1111-1111-111111111111"
$product = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"

Write-Host "=== HAPPY PATH ===" -ForegroundColor Green
$body = @{ customerId=$customer; productId=$product; quantity=2; amount=2000 } | ConvertTo-Json
$order = Invoke-RestMethod -Method Post -Uri "http://localhost:5001/api/orders" -ContentType "application/json" -Body $body
$order | ConvertTo-Json
Invoke-RestMethod -Uri "http://localhost:5001/api/orders/$($order.orderId)" | ConvertTo-Json

Write-Host "=== FAILURE + COMPENSATION PATH ===" -ForegroundColor Yellow
$body = @{ customerId=$customer; productId=$product; quantity=1; amount=100000 } | ConvertTo-Json
$order = Invoke-RestMethod -Method Post -Uri "http://localhost:5001/api/orders" -ContentType "application/json" -Body $body
$order | ConvertTo-Json
Invoke-RestMethod -Uri "http://localhost:5001/api/orders/$($order.orderId)" | ConvertTo-Json

Write-Host "=== INVENTORY AFTER COMPENSATION ===" -ForegroundColor Cyan
Invoke-RestMethod -Uri "http://localhost:5002/api/inventory/$product" | ConvertTo-Json
