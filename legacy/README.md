# Legacy code (reference only)

`Api/` and `Web/` are the previous two-project version of PayLink. They are not built or shipped. They stay here so the gateway integrations (PhonePe, PayU, Razorpay, CCAvenue, UPI QR) can be ported into connector packages in Phase 2, then this folder is deleted.

Secrets that were committed in these files (database password, JWT signing key, gateway keys and salts, a Google service-account key) have been replaced with placeholders or removed. **They are still in git history, so rotate every one of them.**
