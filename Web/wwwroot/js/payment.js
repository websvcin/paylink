document.addEventListener("DOMContentLoaded", () => {
    const proceedButton = document.getElementById("proceedButton");
    const paymentForm = document.getElementById("paymentForm");
    const gatewayModal = new bootstrap.Modal(document.getElementById("gatewayModal"));
    const paymentMethodInput = document.getElementById("paymentMethod");

    const steps = {
        step1: document.getElementById("step1"),
        step2: document.getElementById("step2"),
        step3: document.getElementById("step3")
    };

    // Step 1 -> Show modal
    proceedButton.addEventListener("click", () => {
        if (!paymentForm.checkValidity()) {
            paymentForm.reportValidity();
            return;
        }
        steps.step1.classList.remove("active");
        steps.step1.classList.add("done");
        steps.step2.classList.add("active");
        gatewayModal.show();
    });

    // Step 2 -> Select gateway
    document.querySelectorAll(".gateway-btn").forEach(btn => {
        btn.addEventListener("click", () => {
            const method = btn.dataset.method;
            paymentMethodInput.value = method;

            steps.step2.classList.remove("active");
            steps.step2.classList.add("done");
            steps.step3.classList.add("active");

            gatewayModal.hide();
            showOverlay("Redirecting to payment gateway...");

            setTimeout(() => paymentForm.submit(), 500);
        });
    });

    // Overlay function
    function showOverlay(message) {
        const overlay = document.createElement("div");
        overlay.className = "payment-overlay";
        overlay.textContent = message;
        document.body.appendChild(overlay);
    }
});
