# ⚠️ TO BE DELETED: legacy code, reference only

**Delete this whole folder once the items below are done** (planned for the end of Phase 2). Nothing here is built, tested or shipped, and nothing outside this folder references it.

`Api/` and `Web/` are the previous two-project version of PayLink. They're kept only so their gateway integrations can be ported into connector packages:

| Still to port | Where it is | Becomes |
|---|---|---|
| PhonePe (v1 pay API, X-Verify) | `Api/Services/PaymentService.cs` → `PhonePe()`, `Web/Pages/PhonePe.cshtml.cs` | `WebSvc.PayLink.Connector.PhonePe` (on PhonePe's current API) |
| PayU (hash, auto-posted form) | `PaymentService.PayU()`, `Web/Pages/PayU.cshtml.cs` | `WebSvc.PayLink.Connector.PayU` |
| Razorpay (payment links) | `PaymentService.RazorPay()`, `Web/Pages/RazorPay.cshtml.cs`, `_callback.cshtml.cs` | `WebSvc.PayLink.Connector.Razorpay` |
| CCAvenue (AES request) | `PaymentService.CCAvenue()`, `Api/Helper/CCAvenueHelper.cs` | `WebSvc.PayLink.Connector.CCAvenue` |
| ~~UPI QR + proof upload~~ **Done in Phase 1** | `PaymentService.UPI()`, `Web/Pages/UpiQR.cshtml`, `Web/Services/UpiQrParser.cs` | `WebSvc.PayLink.Connector.Upi` (rewritten: QR, UTR and screenshot, merchant verification) |
| ResellerClub flow | `Web/Pages/RcPay.cshtml.cs` | Decide: port or drop |

Secrets that were committed in these files (database password, JWT signing key, gateway keys and salts, a Google service-account key) have been replaced with placeholders or removed. **They are still in git history, so rotate every one of them.**
