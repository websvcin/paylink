---
sidebar_position: 7
title: Pay button (drop-in)
---

# Pay button (drop-in)

A few lines of HTML put a Pay button on any page, for a payment link or a payment your server created.

```html
<script src="https://pay.example.com/js/paylink.js" defer></script>

<button data-paylink="https://pay.example.com/l/term-fees">Pay fees</button>   <!-- a payment link -->
<button data-paylink="pay_8f2kQ…">Pay</button>                                  <!-- a payment from the API -->

<script>
  document.addEventListener("paylink:result", e => {
    // e.detail = { payment_id, status }: ask your server to check payment_id, then show the result
  });
</script>
```

Or from your own code:

```js
PayLink.open(checkout_url, { onResult: r => console.log(r.payment_id, r.status) });
```

Checkout opens in a small window (gateway pages refuse to load inside frames), and the result comes back to your page when it ends, even after a gateway's own pages. If the browser blocks the window, the page goes to checkout instead, and `return_url` brings the payer back.

When the window closes early, the drop-in asks `GET /pay/{id}/status`, which answers only the id and status.

:::caution
The event is a convenience for your page, not proof of payment. Confirm with `GET /api/v1/payments/{id}` or the [webhook](webhooks.md) before you deliver anything.
:::
