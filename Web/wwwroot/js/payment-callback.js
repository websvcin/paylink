// payment-callback.js
// document.addEventListener("DOMContentLoaded", () => {
//     const paymentId = window.PAYMENT_ID || "";
//     const gateway = (window.GATEWAY || "").toLowerCase();
//     const wsUrl = (window.WS_URL || "").trim();

//     const loader = document.getElementById("loaderContainer");
//     const pending = document.getElementById("pendingContainer");
//     const statusContainer = document.getElementById("statusContainer");
//     const statusText = document.getElementById("statusText");
//     const statusIcon = document.getElementById("statusIcon");
//     const statusDetails = document.getElementById("statusDetails");
//     const countdownText = document.getElementById("countdownText");
//     const progressBar = document.getElementById("progressBar");

//     const TIMEOUT_SECONDS = 60;
//     let timeLeft = TIMEOUT_SECONDS;
//     let socket = null;
//     let paymentFinalized = false;

//     // ⏱ Countdown progress bar
//     const countdownInterval = setInterval(() => {
//         const m = Math.floor(timeLeft / 60);
//         const s = timeLeft % 60;
//         countdownText.innerText = `Time left: ${m < 10 ? "0" + m : m}:${s < 10 ? "0" + s : s}`;
//         const percent = Math.round(((TIMEOUT_SECONDS - timeLeft) / TIMEOUT_SECONDS) * 100);
//         progressBar.style.width = percent + "%";
//         timeLeft--;
//         if (timeLeft < 0) clearInterval(countdownInterval);
//     }, 1000);

//     // ⌛ Timeout fallback
//     const overallTimeout = setTimeout(() => {
//         if (!paymentFinalized) {
//             clearInterval(countdownInterval);
//             closeSocketIfAny();
//             showPendingOrTimeout();
//         }
//     }, (TIMEOUT_SECONDS * 1000) + 500);

//     // 🌐 Connect WebSocket
//     function connectWebSocket() {
//         if (!wsUrl) return console.info("No WebSocket URL, skipping.");
//         try {
//             socket = new WebSocket(wsUrl);
//             socket.onopen = () => console.info("WebSocket connected:", wsUrl);
//             socket.onmessage = (evt) => handleIncomingStatus(evt.data);
//             socket.onerror = (err) => console.error("WebSocket error:", err);
//             socket.onclose = (e) => console.info("WebSocket closed:", e.code, e.reason);
//         } catch (ex) {
//             console.error("WebSocket connect failed:", ex);
//         }
//     }
//     connectWebSocket();

//     // 🧩 Dynamic message registry
//     const messageRegistry = {
//         success: {
//             icon: "✅",
//             colorClass: "text-success",
//             action: showSuccessUI
//         },
//         error: {
//             icon: "❌",
//             colorClass: "text-danger",
//             action: showFailureUI
//         },
//         warning: {
//             icon: "⚠️",
//             colorClass: "text-warning",
//             action: showPendingUI
//         },
//         info: {
//             icon: "ℹ️",
//             colorClass: "text-info",
//             action: showInfoUI
//         },
//         neutral: {
//             icon: "💬",
//             colorClass: "text-muted",
//             action: showNeutralUI
//         }
//     };

//     // 💬 Handle incoming messages
//     function handleIncomingStatus(raw) {
//         try {
//             if (!raw) return;
//             const data = typeof raw === "string" ? JSON.parse(raw) : raw;
//             const meta = JSON.parse(data.Metadata || "{}");
//             if (!meta.paymentId || meta.paymentId !== paymentId) return;

//             paymentFinalized = true;
//             clearInterval(countdownInterval);
//             clearTimeout(overallTimeout);
//             closeSocketIfAny();

//             const type = (meta.type || "neutral").toLowerCase();
//             const entry = messageRegistry[type] || messageRegistry["neutral"];

//             // Update UI header
//             statusIcon.innerText = entry.icon;
//             statusText.innerText = data.Title || "Payment Update";
//             statusText.className = entry.colorClass;

//             fadeOut(loader);
//             fadeOut(pending);
//             fadeIn(statusContainer);

//             // Execute the correct handler dynamically
//             entry.action(data, meta);
//         } catch (ex) {
//             console.error("Failed to parse WS message:", ex);
//         }
//     }

//     // ✅ Success UI
//     function showSuccessUI(data, meta) {
//         const details = {
//             gateway: meta.gateway,
//             paymentId: meta.paymentId,
//             amount: window.PAYMENT_AMOUNT
//         };

//         const redirectUrl = `${window.REDIRECT_URL}?paymentId=${meta.paymentId}&status=success`;
//         const container = document.createElement("div");
//         container.className = "alert alert-success p-3 rounded shadow-sm mt-3 fade-in";
//         container.innerHTML = `
//             <h4 class="mb-2">✅ Payment Successful</h4>
//             <p>${data.Body}</p>
//             <div class="small text-muted mt-2">
//                 Payment ID: <code>${details.paymentId}</code><br>
//                 Gateway: <code>${details.gateway}</code>
//             </div>
//             <div class="mt-3">
//                 <button class="btn btn-success px-4 me-2" onclick="window.location.href='${redirectUrl}'">Continue</button>
//                 <button class="btn btn-outline-secondary px-4" onclick="window.close()">Close</button>
//             </div>
//         `;
//         statusDetails.innerHTML = "";
//         statusDetails.appendChild(container);
//     }

//     // ❌ Failure UI
//     function showFailureUI(data, meta) {
//         const redirectUrl = `${window.REDIRECT_URL}?paymentId=${meta.paymentId}&status=failed`;
//         const container = document.createElement("div");
//         container.className = "alert alert-danger p-3 rounded shadow-sm mt-3 fade-in";
//         container.innerHTML = `
//             <h4 class="mb-2">❌ Payment Failed</h4>
//             <p>${data.Body}</p>
//             <div class="small text-muted mt-2">
//                 Payment ID: <code>${meta.paymentId}</code><br>
//                 Gateway: <code>${meta.gateway}</code>
//             </div>
//             <div class="mt-3">
//                 <button class="btn btn-primary px-4" onclick="window.location.href='${redirectUrl}'">Return to Merchant</button>
//             </div>
//         `;
//         statusDetails.innerHTML = "";
//         statusDetails.appendChild(container);
//     }

//     // ⚠️ Pending UI
//     function showPendingUI(data, meta) {
//         const container = document.createElement("div");
//         container.className = "alert alert-warning p-3 rounded shadow-sm mt-3 fade-in";
//         container.innerHTML = `
//             <h4 class="mb-2">⌛ Payment Pending</h4>
//             <p>${data.Body}</p>
//             <p class="small text-muted">We'll notify you once the payment is confirmed.</p>
//         `;
//         statusDetails.innerHTML = "";
//         statusDetails.appendChild(container);
//     }

//     // ℹ️ Info UI
//     function showInfoUI(data, meta) {
//         const container = document.createElement("div");
//         container.className = "alert alert-info p-3 rounded shadow-sm mt-3 fade-in";
//         container.innerHTML = `
//             <h5>${data.Title || "Payment Info"}</h5>
//             <p>${data.Body}</p>
//         `;
//         statusDetails.innerHTML = "";
//         statusDetails.appendChild(container);
//     }

//     // 💬 Neutral UI
//     function showNeutralUI(data, meta) {
//         const container = document.createElement("div");
//         container.className = "alert alert-secondary p-3 rounded shadow-sm mt-3 fade-in";
//         container.innerHTML = `
//             <h5>${data.Title || "Unknown Payment Status"}</h5>
//             <p>${data.Body || "We couldn’t determine the payment status."}</p>
//         `;
//         statusDetails.innerHTML = "";
//         statusDetails.appendChild(container);
//     }

//     // ⏳ Timeout fallback
//     function showPendingOrTimeout() {
//         if (paymentFinalized) return;

//         fadeOut(loader);
//         fadeIn(statusContainer);
//         statusIcon.innerText = "⌛";
//         statusText.innerText = "Payment Pending Confirmation";
//         statusText.classList.remove("text-danger", "text-success");
//         statusText.classList.add("text-warning");

//         const pendingMessage = "Payment awaiting confirmation";
//         const pendingRedirect = `${window.REDIRECT_URL}?paymentId=${paymentId}&status=pending`;

//         statusDetails.innerHTML = `
//             <div class="alert alert-warning shadow-sm rounded p-3 mb-3 fade-in">
//                 <h5 class="mb-2">Your payment is being verified</h5>
//                 <p class="mb-1 small">We haven’t received confirmation yet. This can take a few moments.</p>
//                 <p class="text-muted small mb-0">You can return to the merchant or retry.</p>
//             </div>
//             <div class="d-flex flex-wrap justify-content-center align-items-center mt-3">
//                 <button class="btn btn-primary me-3 px-4" onclick="window.location.href='${pendingRedirect}'">Return to Merchant</button>
//                 <button class="btn btn-outline-secondary px-4" onclick="window.location.reload()">Retry</button>
//             </div>
//         `;
//     }

//     // 🎬 UI utilities
//     function fadeIn(el) {
//         if (!el) return;
//         el.classList.remove("d-none");
//         el.style.display = "block";
//         el.style.opacity = 0;
//         let op = 0;
//         const timer = setInterval(() => {
//             if (op >= 1) clearInterval(timer);
//             el.style.opacity = op;
//             op += 0.1;
//         }, 30);
//     }

//     function fadeOut(el) {
//         if (!el || el.classList.contains("d-none")) return;
//         let op = 1;
//         const timer = setInterval(() => {
//             if (op <= 0.1) {
//                 clearInterval(timer);
//                 el.classList.add("d-none");
//                 el.style.display = "none";
//             }
//             el.style.opacity = op;
//             op -= 0.1;
//         }, 30);
//     }

//     function closeSocketIfAny() {
//         try {
//             if (socket && socket.readyState === WebSocket.OPEN) socket.close();
//         } catch {}
//     }
// });





// // payment-callback.js
document.addEventListener("DOMContentLoaded", () => {
    const paymentId = PAYMENT_ID || "";
    const gateway = (GATEWAY || "").toLowerCase();
    const wsUrl = (WS_URL || "").trim();

    const loader = document.getElementById("loaderContainer");
    const pending = document.getElementById("pendingContainer");
    const statusContainer = document.getElementById("statusContainer");
    const statusText = document.getElementById("statusText");
    const statusIcon = document.getElementById("statusIcon");
    const statusDetails = document.getElementById("statusDetails");
    const countdownText = document.getElementById("countdownText");
    const progressBar = document.getElementById("progressBar");

    const TIMEOUT_SECONDS = 60;
    let timeLeft = TIMEOUT_SECONDS;
    let socket = null;
    let paymentFinalized = false;

    // countdown visual
    const countdownInterval = setInterval(() => {
        const m = Math.floor(timeLeft / 60);
        const s = timeLeft % 60;
        countdownText.innerText = `Time left: ${m < 10 ? "0" + m : m}:${s < 10 ? "0" + s : s}`;
        const percent = Math.round(((TIMEOUT_SECONDS - timeLeft) / TIMEOUT_SECONDS) * 100);
        progressBar.style.width = percent + "%";
        progressBar.setAttribute("aria-valuenow", TIMEOUT_SECONDS - timeLeft);
        timeLeft--;
        if (timeLeft < 0) clearInterval(countdownInterval);
    }, 1000);

    // timeout fallback
    const overallTimeout = setTimeout(() => {
        if (!paymentFinalized) {
            clearInterval(countdownInterval);
            closeSocketIfAny();
            showPendingOrTimeout();
        }
    }, (TIMEOUT_SECONDS * 1000) + 500);

    function connectWebSocket() {
        if (!wsUrl) return console.info("No WebSocket URL, skipping.");
        try {
            socket = new WebSocket(wsUrl);
            socket.onopen = () => console.info("WebSocket connected:", wsUrl);
            socket.onmessage = (evt) => handleIncomingStatus(evt.data);
            socket.onerror = (err) => console.error("WebSocket error", err);
            socket.onclose = (e) => console.info("WebSocket closed", e.code, e.reason);
        } catch (ex) {
            console.error("WebSocket connect failed:", ex);
        }
    }

    connectWebSocket();

    // ✅ Handle WebSocket updates
    // function handleIncomingStatus(raw) {
    //     try {
    //         if (!raw) return;
    //         const data = typeof raw === "string" ? JSON.parse(raw) : raw;
    //         if (!data.Metadata) return;
    //         const meta = JSON.parse(data.Metadata || "{}");
    //         if (!meta?.paymentId || meta.paymentId !== PAYMENT_ID) return;

    //         paymentFinalized = true;
    //         clearInterval(countdownInterval);
    //         clearTimeout(overallTimeout);
    //         closeSocketIfAny();

    //         const isSuccess = meta.type === "success";
    //         const message = data.Title || (isSuccess ? "Payment successful!" : "Payment failed.");
    //         const details = {
    //             title: data.Title,
    //             body: data.Body,
    //             gateway: meta.gateway || GATEWAY,
    //             paymentId: meta.paymentId,
    //             amount: PAYMENT_AMOUNT
    //         };

    //         statusDetails.innerHTML = "";
    //         showStatus(isSuccess, message, details);
    //     } catch (ex) {
    //         console.error("Failed to parse WS message:", ex);
    //     }
    // }

    function handleIncomingStatus(raw) {
        try {
            if (!raw) return;
            const data = typeof raw === "string" ? JSON.parse(raw) : raw;
            if (!data.Metadata) return;
            const meta = JSON.parse(data.Metadata || "{}");
            if (!meta?.paymentId || meta.paymentId !== PAYMENT_ID) return;

            paymentFinalized = true;
            clearInterval(countdownInterval);
            clearTimeout(overallTimeout);
            closeSocketIfAny();

            const type = (meta.type || "").toLowerCase();

            const details = {
                title: data.Title,
                body: data.Body,
                gateway: meta.gateway || GATEWAY,
                paymentId: meta.paymentId,
                amount: PAYMENT_AMOUNT
            };

            statusDetails.innerHTML = "";

            // handle types dynamically
            switch (type) {
                case "success":
                    showStatus(true, data.Title || "Payment Successful!", details);
                    break;
                case "error":
                case "failed":
                    showStatus(false, data.Title || "Payment Failed", details);
                    break;
                case "warning":
                case "pending":
                    showPendingOrTimeout(); // reuse your pending UI
                    break;
                case "processing":
                case "initiated":
                case "info":
                    showInfoPaymentUI(data.Title || "Payment Received", details);
                    break;
                case "neutral":
                default:
                    showNeutralPaymentUI(data.Title || "Payment Status Unknown", details);
                    break;

            }
        } catch (ex) {
            console.error("Failed to parse WS message:", ex);
        }
    }


    // 🌟 Show final result UI
    function showStatus(success, message, details) {
        fadeOut(loader);
        fadeOut(pending);
        fadeIn(statusContainer);

        const redirectUrl = `${REDIRECT_URL}?paymentId=${PAYMENT_ID}&status=${success ? "success" : "failed"}&message=${encodeURIComponent(message)}`;

        if (success) {
            setupManualRedirectUI(redirectUrl, message, details);
        } else {
            showFailureUI(redirectUrl, message, details);
        }
    }

    // ✅ Business-friendly success UI
    function setupManualRedirectUI(redirectUrl, message, details) {
        const AUTO_REDIRECT_DELAY = 10000;
        let userHasClicked = false;
        let secondsLeft = AUTO_REDIRECT_DELAY / 1000;

        const container = document.createElement("div");
        container.className = "mt-3 text-start fade-in";
        container.innerHTML = `
            <div class="alert alert-success shadow-sm rounded p-3 mb-3">
                <h4 class="mb-2">✅ Payment Confirmed</h4>
                <p class="mb-1">
                    ${details?.amount ? `₹${details.amount.toFixed(2)} payment` : "Your payment"} 
                    has been processed securely${details?.gateway ? ` via <strong>${details.gateway}</strong>` : ""}.
                </p>
                <p class="text-muted small mb-0">You’ll shortly be redirected to complete your order.</p>
            </div>
            <div class="bg-light border rounded p-3 small">
                <strong>Transaction Details</strong><br>
                ${details?.gateway ? `<div>Gateway: <code>${details.gateway}</code></div>` : ""}
                ${details?.amount ? `<div>Amount: <code>₹${details.amount.toFixed(2)}</code></div>` : ""}
                ${details?.paymentId ? `<div>Payment ID: <code>${details.paymentId}</code></div>` : ""}
            </div>
        `;

        const redirectNotice = document.createElement("p");
        redirectNotice.className = "text-muted mt-3 mb-2";
        redirectNotice.innerText = `Redirecting automatically in ${secondsLeft}s...`;
        container.appendChild(redirectNotice);

        const btnContainer = document.createElement("div");
        btnContainer.className = "d-flex justify-content-center align-items-center flex-wrap mt-3";

        const continueBtn = document.createElement("button");
        continueBtn.className = "btn btn-success px-4 py-2 me-3";
        continueBtn.innerText = "Continue to Merchant";
        continueBtn.style.marginRight = "10px";
        continueBtn.onclick = () => {
            userHasClicked = true;
            window.location.href = redirectUrl;
        };

        const stayBtn = document.createElement("button");
        stayBtn.className = "btn btn-outline-secondary px-4 py-2";
        stayBtn.innerText = "Stay Here";
        stayBtn.onclick = () => {
            userHasClicked = true;
            clearInterval(countdown);
            redirectNotice.innerText = "Auto-redirect cancelled. You may close this page.";
        };

        btnContainer.appendChild(continueBtn);
        btnContainer.appendChild(stayBtn);
        container.appendChild(btnContainer);
        statusDetails.appendChild(container);

        const countdown = setInterval(() => {
            secondsLeft--;
            if (secondsLeft <= 0) clearInterval(countdown);
            else redirectNotice.innerText = `Redirecting automatically in ${secondsLeft}s...`;
        }, 1000);

        setTimeout(() => {
            if (!userHasClicked) window.location.href = redirectUrl;
        }, AUTO_REDIRECT_DELAY);
    }

    // ❌ Failure UI
    function showFailureUI(redirectUrl, message, details) {
        const container = document.createElement("div");
        container.className = "mt-3 text-start fade-in";
        container.innerHTML = `
            <div class="alert alert-danger shadow-sm rounded p-3 mb-3">
                <h4 class="mb-2">❌ Payment Could Not Be Completed</h4>
                <p class="mb-1">${message || "Something went wrong while processing your payment."}</p>
                <p class="text-muted small mb-0">If the amount was debited, please contact support with your Payment ID below.</p>
            </div>
            <div class="bg-light border rounded p-3 small">
                <strong>Payment Details</strong><br>
                ${details?.gateway ? `<div>Gateway: <code>${details.gateway}</code></div>` : ""}
                ${details?.paymentId ? `<div>Payment ID: <code>${details.paymentId}</code></div>` : ""}
            </div>
            <div class="mt-3">
                <button class="btn btn-success me-2" onclick="window.location.href='${redirectUrl}'">Return to Merchant</button>
            </div>
        `;
        statusDetails.appendChild(container);
    }

    // 🌟 Cloned success UI for informational payment statuses
    function showInfoPaymentUI(message, details) {
        const AUTO_REDIRECT_DELAY = 10000;
        let userHasClicked = false;
        let secondsLeft = AUTO_REDIRECT_DELAY / 1000;

        const redirectUrl = `${REDIRECT_URL}?paymentId=${details.paymentId}&status=processing&message=${encodeURIComponent(message)}`;

        const container = document.createElement("div");
        container.className = "mt-3 text-start fade-in";
        container.innerHTML = `
            <div class="alert alert-info shadow-sm rounded p-3 mb-3">
                <h4 class="mb-2">ℹ️ Payment Received</h4>
                <p class="mb-1">
                    ${details?.amount ? `₹${details.amount.toFixed(2)} payment` : "Your payment"} 
                    has been received and is being processed. Final confirmation may take a few moments.
                </p>
                <p class="text-muted small mb-0">You’ll shortly be notified once it’s fully confirmed.</p>
            </div>
            <div class="bg-light border rounded p-3 small">
                <strong>Transaction Details</strong><br>
                ${details?.gateway ? `<div>Gateway: <code>${details.gateway}</code></div>` : ""}
                ${details?.amount ? `<div>Amount: <code>₹${details.amount.toFixed(2)}</code></div>` : ""}
                ${details?.paymentId ? `<div>Payment ID: <code>${details.paymentId}</code></div>` : ""}
            </div>
        `;

        const redirectNotice = document.createElement("p");
        redirectNotice.className = "text-muted mt-3 mb-2";
        redirectNotice.innerText = `Redirecting automatically in ${secondsLeft}s...`;
        container.appendChild(redirectNotice);

        const btnContainer = document.createElement("div");
        btnContainer.className = "d-flex justify-content-center align-items-center flex-wrap mt-3";

        const continueBtn = document.createElement("button");
        continueBtn.className = "btn btn-info px-4 py-2 me-3";
        continueBtn.innerText = "Continue to Merchant";
        continueBtn.style = "margin-right: 10px;";
        continueBtn.onclick = () => {
            userHasClicked = true;
            window.location.href = redirectUrl;
        };

        const stayBtn = document.createElement("button");
        stayBtn.className = "btn btn-outline-secondary px-4 py-2";
        stayBtn.innerText = "Stay Here";
        stayBtn.onclick = () => {
            userHasClicked = true;
            clearInterval(countdown);
            redirectNotice.innerText = "Auto-redirect cancelled. You may close this page.";
        };

        btnContainer.appendChild(continueBtn);
        btnContainer.appendChild(stayBtn);
        container.appendChild(btnContainer);
        statusDetails.appendChild(container);

        const countdown = setInterval(() => {
            secondsLeft--;
            if (secondsLeft <= 0) clearInterval(countdown);
            else redirectNotice.innerText = `Redirecting automatically in ${secondsLeft}s...`;
        }, 1000);

        setTimeout(() => {
            if (!userHasClicked) window.location.href = redirectUrl;
        }, AUTO_REDIRECT_DELAY);

        fadeOut(loader);
        fadeOut(pending);
        fadeIn(statusContainer);
    }

    // 🌟 Cloned UI for neutral / unknown payment statuses
    function showNeutralPaymentUI(message, details) {
        const AUTO_REDIRECT_DELAY = 10000;
        let userHasClicked = false;
        let secondsLeft = AUTO_REDIRECT_DELAY / 1000;

        const redirectUrl = `${REDIRECT_URL}?paymentId=${details.paymentId}&status=neutral&message=${encodeURIComponent(message)}`;

        const container = document.createElement("div");
        container.className = "mt-3 text-start fade-in";
        container.innerHTML = `
            <div class="alert alert-secondary shadow-sm rounded p-3 mb-3">
                <h4 class="mb-2">💬 Payment Status Unknown</h4>
                <p class="mb-1">
                    ${details?.amount ? `₹${details.amount.toFixed(2)} payment` : "Your payment"} 
                    status could not be determined at this time. Please check back later or contact support.
                </p>
            </div>
            <div class="bg-light border rounded p-3 small">
                <strong>Transaction Details</strong><br>
                ${details?.gateway ? `<div>Gateway: <code>${details.gateway}</code></div>` : ""}
                ${details?.amount ? `<div>Amount: <code>₹${details.amount.toFixed(2)}</code></div>` : ""}
                ${details?.paymentId ? `<div>Payment ID: <code>${details.paymentId}</code></div>` : ""}
            </div>
        `;

        const redirectNotice = document.createElement("p");
        redirectNotice.className = "text-muted mt-3 mb-2";
        redirectNotice.innerText = `Redirecting automatically in ${secondsLeft}s...`;
        container.appendChild(redirectNotice);

        const btnContainer = document.createElement("div");
        btnContainer.className = "d-flex justify-content-center align-items-center flex-wrap mt-3";

        const continueBtn = document.createElement("button");
        continueBtn.className = "btn btn-secondary px-4 py-2 me-3";
        continueBtn.innerText = "Continue to Merchant";
        continueBtn.style = "margin-right: 10px;";
        continueBtn.onclick = () => {
            userHasClicked = true;
            window.location.href = redirectUrl;
        };

        const stayBtn = document.createElement("button");
        stayBtn.className = "btn btn-outline-secondary px-4 py-2";
        stayBtn.innerText = "Stay Here";
        stayBtn.onclick = () => {
            userHasClicked = true;
            clearInterval(countdown);
            redirectNotice.innerText = "Auto-redirect cancelled. You may close this page.";
        };

        btnContainer.appendChild(continueBtn);
        btnContainer.appendChild(stayBtn);
        container.appendChild(btnContainer);
        statusDetails.appendChild(container);

        const countdown = setInterval(() => {
            secondsLeft--;
            if (secondsLeft <= 0) clearInterval(countdown);
            else redirectNotice.innerText = `Redirecting automatically in ${secondsLeft}s...`;
        }, 1000);

        setTimeout(() => {
            if (!userHasClicked) window.location.href = redirectUrl;
        }, AUTO_REDIRECT_DELAY);

        fadeOut(loader);
        fadeOut(pending);
        fadeIn(statusContainer);
    }


    // 🕓 Pending or timeout UI
    function showPendingOrTimeout() {
        if (paymentFinalized) return;

        fadeOut(loader);
        fadeIn(statusContainer);

        const pendingMessage = "Payment awaiting confirmation";
        const pendingRedirect = `${REDIRECT_URL}?paymentId=${PAYMENT_ID}&status=pending&message=${encodeURIComponent(pendingMessage)}`;

        statusIcon.innerText = "⌛";
        statusText.innerText = "Payment Pending Confirmation";
        statusText.classList.remove("text-danger", "text-success");

        statusDetails.innerHTML = `
            <div class="alert alert-warning shadow-sm rounded p-3 mb-3 fade-in">
                <h5 class="mb-2">Your payment is being verified</h5>
                <p class="mb-1 small">We haven’t received confirmation yet. This can take a few moments depending on your bank or network.</p>
                <p class="text-muted small mb-0">You can stay on this page or manually check below.</p>
            </div>
            <div class="d-flex flex-wrap justify-content-center align-items-center mt-3">
                <button class="btn btn-primary me-3 px-4" style="margin-right: 10px;" onclick="window.location.href='${pendingRedirect}'">Return to Merchant</button>
                <button class="btn btn-outline-secondary me-3 px-4" style="margin-right: 10px;" onclick="window.location.reload()">Retry</button>
                <button class="btn btn-success px-4" id="checkStatusButton">Check Status Now</button>
            </div>
        `;

        const checkStatusButton = document.getElementById("checkStatusButton");
        if (checkStatusButton) bindCheckStatusHandler(checkStatusButton);
    }

    // 🔍 Check Status handler (REST API fallback)
    function bindCheckStatusHandler(btn) {
        btn.addEventListener("click", async () => {
            btn.disabled = true;
            btn.innerText = "Checking...";

            try {
                const res = await fetch(CHECK_STATUS_URL, {
                    method: "GET",
                    headers: {
                        "Content-Type": "application/json",
                        "RequestVerificationToken": document.querySelector('input[name="__RequestVerificationToken"]')?.value || ""
                    }
                });

                if (!res.ok) throw new Error(`Server responded ${res.status}`);
                const json = await res.json();

                btn.disabled = false;
                btn.innerText = "Check Status Now";

                if (json.status && json.paymentStatus) {
                    const status = json.paymentStatus.toLowerCase();
                    let success = false;

                    switch (status) {
                        case "completed":
                            success = true;
                            break;
                        case "failed":
                            success = false;
                            break;
                        case "pending":
                            statusText.innerText = "Payment is still pending confirmation.";
                            return;
                        default:
                            statusText.innerText = "Unknown payment status.";
                            return;
                    }
                    statusDetails.innerHTML = "";
                    statusIcon.innerHTML = "";
                    statusText.innerHTML = "";
                    showStatus(success, json.message || status, { paymentId, gateway });
                } else {
                    statusText.innerText = "Unable to determine payment status.";
                }
            } catch (err) {
                console.error("Check Status Error:", err);
                btn.disabled = false;
                btn.innerText = "Check Status Now";
                statusText.innerText = "Error checking payment status.";
            }
        });
    }



    // 🔧 Utility animations
    function fadeIn(el) {
        if (!el) return;
        el.classList.remove("d-none");
        el.style.display = "block";
        el.style.opacity = 0;
        let op = 0;
        const timer = setInterval(() => {
            if (op >= 1) clearInterval(timer);
            el.style.opacity = op;
            op += 0.1;
        }, 30);
    }

    function fadeOut(el) {
        if (!el || el.classList.contains("d-none")) return;
        let op = 1;
        const timer = setInterval(() => {
            if (op <= 0.1) {
                clearInterval(timer);
                el.classList.add("d-none");
                el.style.display = "none";
            }
            el.style.opacity = op;
            op -= 0.1;
        }, 30);
    }

    function closeSocketIfAny() {
        try {
            if (socket && socket.readyState === WebSocket.OPEN) socket.close();
        } catch {}
    }
});
