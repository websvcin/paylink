/*
 * PayLink drop-in. Add to a page:
 *   <script src="https://YOUR-PAYLINK/js/paylink.js" defer></script>
 *   <button data-paylink="https://YOUR-PAYLINK/l/term-fees">Pay</button>      a payment link
 *   <button data-paylink="pay_8f2kQ...">Pay</button>                          a payment your server created
 * or call PayLink.open(urlOrId, { onResult: function (r) {} }).
 *
 * Checkout opens in a small window (gateway pages refuse to load inside frames). When it ends, a "paylink:result" event
 * fires on the button (it bubbles to document) with detail { payment_id, status }. That only tells your page what happened;
 * confirm with your server (API or webhook) before you deliver anything. If the window can't open, the page goes to checkout.
 */
(function () {
  "use strict";
  if (window.PayLink) return;
  var script = document.currentScript;
  var base = script && script.src ? new URL(script.src).origin : location.origin;

  function resolve(target) {
    if (/^pay_[A-Za-z0-9]+$/.test(target)) return base + "/pay/" + target;
    if (/^[a-z0-9][a-z0-9-]*$/.test(target)) return base + "/l/" + target;
    return new URL(target, base).href;
  }

  function open(target, options) {
    options = options || {};
    var url = new URL(resolve(String(target)));
    url.searchParams.set("embed", "1");
    var w = 480, h = 760;
    var left = Math.max(0, (window.screenX || 0) + (window.outerWidth - w) / 2);
    var top = Math.max(0, (window.screenY || 0) + (window.outerHeight - h) / 2);
    var popup = window.open(url.href, "paylink_checkout", "popup=yes,width=" + w + ",height=" + h + ",left=" + left + ",top=" + top);
    if (!popup) { url.searchParams.delete("embed"); location.href = url.href; return null; }

    var paymentId = null, done = false;
    function finish(detail) {
      if (done) return;
      done = true;
      window.removeEventListener("message", onMessage);
      clearInterval(timer);
      var target = options.element || document;
      target.dispatchEvent(new CustomEvent("paylink:result", { detail: detail, bubbles: true }));
      if (typeof options.onResult === "function") options.onResult(detail);
    }
    function onMessage(e) {
      if (e.origin !== url.origin) return;
      var d = e.data;
      if (!d || d.source !== "paylink") return;
      if (d.payment_id) paymentId = d.payment_id;
      if (d.type === "result") finish({ payment_id: d.payment_id, status: d.status });
    }
    window.addEventListener("message", onMessage);
    // Gateways can cut the link back to this page, so if the window closes without a result, ask PayLink.
    var timer = setInterval(function () {
      if (!popup.closed || done) return;
      clearInterval(timer);
      if (!paymentId) return finish({ payment_id: null, status: "closed" });
      fetch(url.origin + "/pay/" + encodeURIComponent(paymentId) + "/status")
        .then(function (r) { return r.json(); })
        .then(function (j) { finish({ payment_id: paymentId, status: j.status || "unknown" }); })
        .catch(function () { finish({ payment_id: paymentId, status: "unknown" }); });
    }, 600);
    popup.focus();
    return popup;
  }

  document.addEventListener("click", function (e) {
    var el = e.target && e.target.closest ? e.target.closest("[data-paylink]") : null;
    if (!el) return;
    e.preventDefault();
    open(el.getAttribute("data-paylink"), { element: el });
  });

  window.PayLink = { open: open };
})();
