---
sidebar_position: 8
title: Webhooks
---

# Webhooks

PayLink tells your server when something happens. Add endpoints in **Console → Developers → Webhooks**, separately for Test and Live (Live needs `https`), or with `POST /api/tenant/webhooks`. Each endpoint gets a signing secret (`whsec_…`, shown once) and can take all events or only some.

## Events

| Event | `data` |
|---|---|
| `payment.processing` | The payment. The payer says they've paid; confirmation pending. |
| `payment.on_hold` | The payment. A risk rule held it. |
| `payment.succeeded` | The payment. **Fulfil the order on this.** |
| `payment.failed` | The payment, with `failure`. |
| `payment.cancelled` | The payment. |
| `payment.expired` | The payment. |
| `refund.succeeded`, `refund.failed` | The refund and the payment. |
| `dispute.created`, `dispute.updated`, `dispute.closed` | The `dispute` and the `payment`. |
| `alert.triggered` | Live endpoints only. `kind`, `threshold`, `window_minutes` and a readable `message`. |
| `webhook.test` | From **Send test**. |

## A delivery

```
POST https://acme.in/hooks/paylink
PayLink-Signature: t=1760900120,v1=5f2b9c…e41a
PayLink-Event: payment.succeeded
Content-Type: application/json

{ "id": "evt_…", "type": "payment.succeeded", "created_at": "2026-10-21T10:02:11.000Z", "livemode": false, "data": { …the payment… } }
```

## Verify the signature

`v1` is the lowercase hex HMAC-SHA256 of `"{t}.{raw body}"` with your endpoint's secret. Check it against the **raw** body (before JSON parsing), compare in constant time, and reject old `t` values (e.g. older than 5 minutes).

```js
// Node.js
const crypto = require('crypto');
function verify(rawBody, header, secret) {
  const parts = Object.fromEntries(header.split(',').map(p => p.split('=', 2)));
  const expected = crypto.createHmac('sha256', secret).update(`${parts.t}.${rawBody}`).digest('hex');
  const ok = crypto.timingSafeEqual(Buffer.from(expected), Buffer.from(parts.v1 || ''));
  return ok && Math.abs(Date.now() / 1000 - Number(parts.t)) < 300;
}
```

```python
# Python
import hmac, hashlib, time
def verify(raw_body: bytes, header: str, secret: str) -> bool:
    parts = dict(p.split('=', 1) for p in header.split(','))
    expected = hmac.new(secret.encode(), f"{parts['t']}.".encode() + raw_body, hashlib.sha256).hexdigest()
    return hmac.compare_digest(expected, parts.get('v1', '')) and abs(time.time() - int(parts['t'])) < 300
```

```php
// PHP
function verify(string $rawBody, string $header, string $secret): bool {
    parse_str(str_replace(',', '&', $header), $parts);
    $expected = hash_hmac('sha256', $parts['t'] . '.' . $rawBody, $secret);
    return hash_equals($expected, $parts['v1'] ?? '') && abs(time() - (int)$parts['t']) < 300;
}
```

```csharp
// C#
var parts = header.Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{parts["t"]}.{rawBody}"))).ToLowerInvariant();
var ok = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts["v1"]));
```

## Answering and retries

Answer `2xx` within 10 seconds; do slow work after answering. Anything else is retried after 1 min, 5 min, 30 min, 2 h, 6 h, 12 h and 24 h, then marked failed. Every delivery is listed on the payment and on the Developers page, with **Send again** (or `POST /api/tenant/webhooks/deliveries/{id}/redeliver`).

Deliveries can arrive more than once and out of order: handle duplicates by the event `id`, and treat the payment's `status` in `data` as the truth.

PayLink only delivers to public addresses, unless the platform sets `PayLink__Webhooks__AllowPrivateNetworks=true`.
